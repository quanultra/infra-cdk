using System.Collections.Generic;
using Amazon.CDK;
using Amazon.CDK.AWS.ApplicationAutoScaling;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using Amazon.CDK.AWS.Logs;
using Amazon.CDK.AWS.SecretsManager;
using Constructs;
// Alias tránh xung đột tên giữa Amazon.CDK.AWS.ECS.Secret và Amazon.CDK.AWS.SecretsManager.Secret
using EcsSecret = Amazon.CDK.AWS.ECS.Secret;

namespace InfraCdk.Constructs
{
    public class EcsConstructProps
    {
        public Vpc Vpc { get; set; }
        public ISubnet PrivateSubnet1 { get; set; }
        public ISubnet PrivateSubnet2 { get; set; }
        public SecurityGroup EcsSg { get; set; }

        /// <summary>
        /// Secret chứa DB credentials (username, password) từ DatabaseConstruct.
        /// CDK sẽ tự động grant secretsmanager:GetSecretValue cho ECS Task Execution Role.
        /// App nhận credentials qua environment variables: DB_USERNAME, DB_PASSWORD.
        /// </summary>
        public ISecret DbSecret { get; set; }

        /// <summary>
        /// Endpoint của RDS Proxy — app nên kết nối qua đây thay vì Aurora trực tiếp.
        /// Được inject vào container dưới dạng env var: DB_HOST.
        /// </summary>
        public string DbProxyEndpoint { get; set; }

        /// <summary>
        /// Cấu hình theo environment — xác định ECS scaling values, desired count.
        /// Được tạo từ EnvironmentConfig.FromName() trong InfraCdkStack.
        /// </summary>
        public EnvironmentConfig EnvConfig { get; set; }

        /// <summary>
        /// Tag của Docker image trên ECR. Truyền qua CLI:
        ///   cdk deploy --context imageTag=v1.2.3
        /// Mặc định "latest" — dùng cho lần deploy đầu tiên.
        /// ⚠️ Phải push image lên ECR trước khi deploy ECS service.
        /// </summary>
        public string ImageTag { get; set; } = "latest";
    }

    /// <summary>
    /// Triển khai ECS Cluster, Fargate Task Definition, Fargate Service,
    /// ALB Target Group, CloudWatch Log Group, và Auto Scaling (CPU + Schedule).
    /// </summary>
    public class EcsConstruct : Construct
    {
        public Cluster Cluster { get; }
        public FargateService FargateService { get; }
        public ApplicationTargetGroup TargetGroup { get; }

        public EcsConstruct(Construct scope, string id, EcsConstructProps props)
            : base(scope, id)
        {
            // --- ECR Repository ---
            // #9: Tạo ECR repo trong CDK thay vì dùng FromAsset (không CI/CD friendly).
            // ⚠️ Phải push image trước khi deploy ECS:
            //   docker build -t <ECR_URI>:<tag> .
            //   aws ecr get-login-password | docker login --username AWS --password-stdin <ECR_URI>
            //   docker push <ECR_URI>:<tag>
            var ecrRepo = new Repository(
                this,
                "AppEcrRepository",
                new RepositoryProps
                {
                    RepositoryName = $"{props.EnvConfig.Suffix.ToLower()}-app",
                    ImageScanOnPush = true, // Tự động scan CVE khi push image
                    LifecycleRules = new[]
                    {
                        new LifecycleRule
                        {
                            MaxImageCount = 10,
                            TagStatus = TagStatus.ANY,
                            Description = "Giữ tối đa 10 images, xóa cũ hơn",
                        },
                    },
                    RemovalPolicy = props.EnvConfig.EcrRemovalPolicy,
                }
            );

            new CfnOutput(
                this,
                "EcrRepositoryUri",
                new CfnOutputProps
                {
                    Value = ecrRepo.RepositoryUri,
                    Description =
                        "ECR URI — push image: docker push <URI>:<tag> rồi deploy với --context imageTag=<tag>",
                    ExportName = $"{props.EnvConfig.Suffix}-EcrRepositoryUri",
                }
            );

            // --- CloudWatch Log Group ---
            // #7: Tên log group có env suffix để tránh conflict khi deploy nhiều env cùng account.
            // #8: Retention đọc từ EnvironmentConfig: Dev=1W, Stg=2W, Prod=1M.
            var logGroup = new LogGroup(
                this,
                "FargateLogGroup",
                new LogGroupProps
                {
                    LogGroupName = $"/ecs/{props.EnvConfig.Suffix.ToLower()}-fargate-service-logs",
                    Retention = props.EnvConfig.LogRetentionDays,
                    RemovalPolicy = RemovalPolicy.DESTROY,
                }
            );

            // --- ECS Cluster ---
            // #7: ClusterName có env suffix, ContainerInsights bật để có RunningTaskCount metric.
            Cluster = new Cluster(
                this,
                "ECSCluster",
                new ClusterProps
                {
                    Vpc = props.Vpc,
                    ClusterName = $"{props.EnvConfig.Suffix}-ECSCluster",
                    ContainerInsights = true, // Cần cho alarm ECS-Zero-Tasks (RunningTaskCount metric)
                }
            );

            // --- Fargate Task Definition ---
            var taskDefinition = new FargateTaskDefinition(
                this,
                "FargateTaskDef",
                new FargateTaskDefinitionProps { Cpu = 256, MemoryLimitMiB = 512 }
            );

            taskDefinition.AddContainer(
                "AppContainer",
                new ContainerDefinitionOptions
                {
                    // #9: Image từ ECR repo được tạo ở trên — hỗ trợ CI/CD và rollback qua imageTag.
                    Image = ContainerImage.FromEcrRepository(ecrRepo, props.ImageTag),
                    PortMappings = new[] { new PortMapping { ContainerPort = 80 } },
                    Logging = LogDrivers.AwsLogs(
                        new AwsLogDriverProps { LogGroup = logGroup, StreamPrefix = "fargate" }
                    ),

                    // ── DB Credentials Injection ──────────────────────────────
                    // CDK tự động grant Task Execution Role quyền GetSecretValue.
                    // ECS agent fetch secret và inject TRƯỚC khi container khởi động.
                    // App chỉ cần đọc Environment.GetEnvironmentVariable() — không cần AWS SDK.
                    Secrets =
                        props.DbSecret != null
                            ? new Dictionary<string, EcsSecret>
                            {
                                // Inject từng field của JSON secret thành env var riêng
                                {
                                    "DB_USERNAME",
                                    EcsSecret.FromSecretsManager(props.DbSecret, "username")
                                },
                                {
                                    "DB_PASSWORD",
                                    EcsSecret.FromSecretsManager(props.DbSecret, "password")
                                },
                            }
                            : null,

                    // ── DB Connection Config (non-secret) ────────────────────
                    // Kết nối qua RDS Proxy để tận dụng connection pooling
                    Environment = new Dictionary<string, string>
                    {
                        { "DB_HOST", props.DbProxyEndpoint ?? string.Empty },
                        { "DB_PORT", "3306" },
                        { "DB_NAME", "mydatabase" },
                        { "ASPNETCORE_ENVIRONMENT", "Production" },
                    },
                }
            );

            // --- ALB Target Group ---
            // Được tạo tại ECS Construct vì liên kết chặt với Fargate Service
            TargetGroup = new ApplicationTargetGroup(
                this,
                "FargateTargetGroup",
                new ApplicationTargetGroupProps
                {
                    Vpc = props.Vpc,
                    Port = 80,
                    Protocol = ApplicationProtocol.HTTP,
                    TargetType = TargetType.IP,

                    // ── #18: DeregistrationDelay ─────────────────────────────
                    // Khi ECS scale-in, ALB đợi bao lâu trước khi xóa task khỏi target group.
                    // Default = 300s (5 phút) — quá dài, request in-flight chỉ cần vài giây.
                    // 30s đủ để drain request đang xử lý mà không chặn scale-in lâu.
                    DeregistrationDelay = Duration.Seconds(30),

                    // ── #5: Health Check ──────────────────────────────────────
                    // ALB dùng endpoint này để kiểm tra task còn sống không.
                    // App PHẢI trả về HTTP 200 tại GET /health khi healthy.
                    // Nếu không có /health endpoint, ALB sẽ dùng / — nhưng tốt nhất nên định nghĩa rõ.
                    HealthCheck = new Amazon.CDK.AWS.ElasticLoadBalancingV2.HealthCheck
                    {
                        Path = "/health",
                        HealthyHttpCodes = "200",
                        // 2 lần liên tiếp OK → task được đánh dấu Healthy (thay default 5)
                        HealthyThresholdCount = 2,
                        // 3 lần liên tiếp fail → task bị đánh dấu Unhealthy và bị replace
                        UnhealthyThresholdCount = 3,
                        // Mỗi request health check timeout sau 5s
                        Timeout = Duration.Seconds(5),
                        // Cứ 30s gửi 1 request health check
                        Interval = Duration.Seconds(30),
                    },
                }
            );

            // --- Fargate Service ---
            FargateService = new FargateService(
                this,
                "FargateService",
                new FargateServiceProps
                {
                    Cluster = Cluster,
                    // #7: ServiceName có env suffix tránh conflict multi-env trong cùng account.
                    ServiceName = $"{props.EnvConfig.Suffix}-FargateService",
                    TaskDefinition = taskDefinition,
                    AssignPublicIp = false,
                    DesiredCount = props.EnvConfig.EcsDesiredCount, // #3 fix: dùng từ EnvironmentConfig
                    SecurityGroups = new[] { props.EcsSg },
                    VpcSubnets = new SubnetSelection
                    {
                        Subnets = new ISubnet[] { props.PrivateSubnet1, props.PrivateSubnet2 },
                    },

                    // ── #6: Deployment Circuit Breaker ───────────────────────
                    // Nếu deploy mới bị lỗi (tasks crash liên tục), ECS sẽ:
                    //   1. Phát hiện: tasks mới không đạt trạng thái RUNNING trong thời gian nhất định
                    //   2. Dừng deploy: không tiếp tục rollout task mới lỗi
                    //   3. Rollback = true: tự động rollback về Task Definition cũ đang hoạt động
                    // Không có Circuit Breaker → ECS cứ retry mãi → downtime kéo dài.
                    CircuitBreaker = new DeploymentCircuitBreaker { Rollback = true },

                    // ── #7: Health Check Grace Period ──────────────────────
                    // Sau khi task được đăng ký vào ALB, chờ 60s TRƯỚC khi bắt đầu gửi health check.
                    // Cần thiết vì:
                    //   - App cần thời gian khởi động (.NET warm-up, kết nối DB, load cache)
                    //   - Nếu không có: ALB ngay lập tức check /health → fail → trigger Circuit Breaker
                    //   - Đặc biệt quan trọng khi scale từ 0 tasks lên (sáng sớm 7h)
                    HealthCheckGracePeriod = Duration.Seconds(60),
                }
            );

            FargateService.AttachToApplicationTargetGroup(TargetGroup);

            // --- Auto Scaling: CPU-Based ---
            // Giới hạn tổng thể theo EnvironmentConfig:
            //   Dev:  Min=1, Max=4 | Stg: Min=1, Max=4 | Prod: Min=2, Max=8
            // Scheduled actions bên dưới sẽ OVERRIDE giới hạn này vào ban đêm
            var scaling = FargateService.AutoScaleTaskCount(
                new EnableScalingProps
                {
                    MinCapacity = props.EnvConfig.EcsMinCapacity,
                    MaxCapacity = props.EnvConfig.EcsMaxCapacity,
                }
            );

            scaling.ScaleOnCpuUtilization(
                "CpuScaling",
                new CpuUtilizationScalingProps
                {
                    TargetUtilizationPercent = 50,
                    ScaleInCooldown = Duration.Seconds(60),
                    ScaleOutCooldown = Duration.Seconds(60),
                }
            );

            // --- Auto Scaling: Schedule ---
            // #13: Giờ scale đọc từ EnvironmentConfig — linh hoạt theo region/timezone.
            //   Dev/Stg → 0 tasks ban đêm — tắt hoàn toàn, tiết kiệm 100% Fargate cost
            //   Prod    → 1 task ban đêm — luôn có task sẵn sàng, tránh cold start hoàn toàn
            scaling.ScaleOnSchedule(
                "ScaleDownAtNight",
                new ScalingSchedule
                {
                    // #13: ScaleDownHourUtc từ EnvironmentConfig (default "15" = 22:00 VN UTC+7)
                    Schedule = Schedule.Cron(
                        new CronOptions { Hour = props.EnvConfig.ScaleDownHourUtc, Minute = "0" }
                    ),
                    MinCapacity = props.EnvConfig.EcsNightMinCapacity,
                    MaxCapacity = props.EnvConfig.EcsNightMaxCapacity,
                }
            );

            // Bật lại ECS buổi sáng
            scaling.ScaleOnSchedule(
                "ScaleUpInMorning",
                new ScalingSchedule
                {
                    // #13: ScaleUpHourUtc từ EnvironmentConfig (default "0" = 07:00 VN UTC+7)
                    Schedule = Schedule.Cron(
                        new CronOptions { Hour = props.EnvConfig.ScaleUpHourUtc, Minute = "0" }
                    ),
                    MinCapacity = props.EnvConfig.EcsMinCapacity,
                    MaxCapacity = props.EnvConfig.EcsMaxCapacity,
                }
            );
        }
    }
}
