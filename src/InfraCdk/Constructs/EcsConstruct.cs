using Amazon.CDK;
using Amazon.CDK.AWS.ApplicationAutoScaling;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using Amazon.CDK.AWS.Logs;
using Constructs;

namespace InfraCdk.Constructs
{
    public class EcsConstructProps
    {
        public Vpc Vpc { get; set; }
        public ISubnet PrivateSubnet1 { get; set; }
        public ISubnet PrivateSubnet2 { get; set; }
        public SecurityGroup EcsSg { get; set; }
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
