using System.Collections.Generic;
using Amazon.CDK;
using Amazon.CDK.AWS.ApplicationAutoScaling;
using Amazon.CDK.AWS.EC2;
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
            // --- CloudWatch Log Group ---
            var logGroup = new LogGroup(
                this,
                "FargateLogGroup",
                new LogGroupProps
                {
                    LogGroupName = "/ecs/fargate-service-logs",
                    Retention = RetentionDays.ONE_WEEK,
                    RemovalPolicy = RemovalPolicy.DESTROY,
                }
            );

            // --- ECS Cluster ---
            Cluster = new Cluster(
                this,
                "ECSCluster",
                new ClusterProps { Vpc = props.Vpc, ClusterName = "ECSCluster" }
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
                    // Build image từ local và push lên ECR Private — không phụ thuộc Docker Hub
                    Image = ContainerImage.FromAsset("src/InfraCdk/docker-app"),
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
                    ServiceName = "MyFargateService",
                    TaskDefinition = taskDefinition,
                    AssignPublicIp = false,
                    DesiredCount = 2,
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
                }
            );

            FargateService.AttachToApplicationTargetGroup(TargetGroup);

            // --- Auto Scaling: CPU-Based ---
            var scaling = FargateService.AutoScaleTaskCount(
                new EnableScalingProps { MinCapacity = 2, MaxCapacity = 8 }
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

            // --- Auto Scaling: Schedule (tiết kiệm chi phí ban đêm) ---
            // Tắt ECS lúc 22:00 VN (15:00 UTC)
            scaling.ScaleOnSchedule(
                "ScaleDownAtNight",
                new ScalingSchedule
                {
                    Schedule = Schedule.Cron(new CronOptions { Hour = "15", Minute = "0" }),
                    MinCapacity = 0,
                    MaxCapacity = 0,
                }
            );

            // Bật lại ECS lúc 07:00 VN (00:00 UTC)
            scaling.ScaleOnSchedule(
                "ScaleUpInMorning",
                new ScalingSchedule
                {
                    Schedule = Schedule.Cron(new CronOptions { Hour = "0", Minute = "0" }),
                    MinCapacity = 2,
                    MaxCapacity = 8,
                }
            );
        }
    }
}
