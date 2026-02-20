using System.Collections.Generic;
using Amazon.CDK;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.CloudWatch.Actions;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using Amazon.CDK.AWS.RDS;
using Amazon.CDK.AWS.SNS;
using Amazon.CDK.AWS.SNS.Subscriptions;
using Constructs;

namespace InfraCdk.Constructs
{
    public class MonitoringConstructProps
    {
        public FargateService FargateService { get; set; }
        public ApplicationLoadBalancer Alb { get; set; }
        public ApplicationTargetGroup TargetGroup { get; set; }
        public DatabaseCluster AuroraCluster { get; set; }

        /// <summary>
        /// Email nhận CloudWatch Alarm notifications qua SNS.
        /// Nếu null/empty, SNS Topic vẫn được tạo nhưng không có subscriber.
        /// Có thể set qua: cdk deploy --context notificationEmail=admin@example.com
        /// </summary>
        public string NotificationEmail { get; set; }
    }

    /// <summary>
    /// Cấu hình toàn bộ Monitoring &amp; Alerting:
    ///   - SNS Topic → email notifications
    ///   - 8 CloudWatch Alarms: ECS (CPU, Memory), ALB (5XX, ResponseTime, UnhealthyHost), Aurora (CPU, Connections, Memory)
    ///   - CloudWatch Dashboard tổng quan
    /// </summary>
    public class MonitoringConstruct : Construct
    {
        public Topic AlarmTopic { get; }

        public MonitoringConstruct(Construct scope, string id, MonitoringConstructProps props)
            : base(scope, id)
        {
            // ── SNS Topic ─────────────────────────────────────────────────────
            AlarmTopic = new Topic(
                this,
                "AlarmTopic",
                new TopicProps
                {
                    TopicName = "InfraAlarmTopic",
                    DisplayName = "Infrastructure CloudWatch Alarms",
                }
            );

            if (!string.IsNullOrEmpty(props.NotificationEmail))
            {
                // ⚠️ Sau deploy, AWS sẽ gửi email xác nhận subscription — phải click "Confirm"
                AlarmTopic.AddSubscription(new EmailSubscription(props.NotificationEmail));
            }

            var alarmAction = new SnsAction(AlarmTopic);

            // ── Metrics ───────────────────────────────────────────────────────
            // Định nghĩa metrics tập trung để tái dùng cho Alarm và Dashboard

            var ecsCpuMetric = new Metric(
                new MetricProps
                {
                    Namespace = "AWS/ECS",
                    MetricName = "CPUUtilization",
                    DimensionsMap = new Dictionary<string, string>
                    {
                        { "ClusterName", props.FargateService.Cluster.ClusterName },
                        { "ServiceName", props.FargateService.ServiceName },
                    },
                    Period = Duration.Minutes(5),
                    Statistic = "Average",
                    Label = "CPU Utilization (avg)",
                }
            );

            var ecsMemoryMetric = new Metric(
                new MetricProps
                {
                    Namespace = "AWS/ECS",
                    MetricName = "MemoryUtilization",
                    DimensionsMap = new Dictionary<string, string>
                    {
                        { "ClusterName", props.FargateService.Cluster.ClusterName },
                        { "ServiceName", props.FargateService.ServiceName },
                    },
                    Period = Duration.Minutes(5),
                    Statistic = "Average",
                    Label = "Memory Utilization (avg)",
                }
            );

            var rdsCpuMetric = new Metric(
                new MetricProps
                {
                    Namespace = "AWS/RDS",
                    MetricName = "CPUUtilization",
                    DimensionsMap = new Dictionary<string, string>
                    {
                        { "DBClusterIdentifier", props.AuroraCluster.ClusterIdentifier },
                    },
                    Period = Duration.Minutes(5),
                    Statistic = "Average",
                    Label = "Aurora CPU (avg)",
                }
            );

            var rdsConnectionsMetric = new Metric(
                new MetricProps
                {
                    Namespace = "AWS/RDS",
                    MetricName = "DatabaseConnections",
                    DimensionsMap = new Dictionary<string, string>
                    {
                        { "DBClusterIdentifier", props.AuroraCluster.ClusterIdentifier },
                    },
                    Period = Duration.Minutes(5),
                    Statistic = "Maximum",
                    Label = "DB Connections (max)",
                }
            );

            var rdsFreeMemoryMetric = new Metric(
                new MetricProps
                {
                    Namespace = "AWS/RDS",
                    MetricName = "FreeableMemory",
                    DimensionsMap = new Dictionary<string, string>
                    {
                        { "DBClusterIdentifier", props.AuroraCluster.ClusterIdentifier },
                    },
                    Period = Duration.Minutes(5),
                    Statistic = "Minimum",
                    Label = "Freeable Memory (min bytes)",
                }
            );

            var alb5xxMetric = props.TargetGroup.Metrics.HttpCodeTarget(
                HttpCodeTarget.TARGET_5XX_COUNT,
                new MetricOptions
                {
                    Period = Duration.Minutes(5),
                    Statistic = "Sum",
                    Label = "5XX Count",
                }
            );
            var alb4xxMetric = props.TargetGroup.Metrics.HttpCodeTarget(
                HttpCodeTarget.TARGET_4XX_COUNT,
                new MetricOptions
                {
                    Period = Duration.Minutes(5),
                    Statistic = "Sum",
                    Label = "4XX Count",
                }
            );
            var albResponseTimeP99 = props.TargetGroup.Metrics.TargetResponseTime(
                new MetricOptions
                {
                    Period = Duration.Minutes(5),
                    Statistic = "p99",
                    Label = "Response Time p99",
                }
            );
            var albResponseTimeP50 = props.TargetGroup.Metrics.TargetResponseTime(
                new MetricOptions
                {
                    Period = Duration.Minutes(5),
                    Statistic = "p50",
                    Label = "Response Time p50",
                }
            );
            var albUnhealthyHostMetric = props.TargetGroup.Metrics.UnhealthyHostCount(
                new MetricOptions
                {
                    Period = Duration.Minutes(1),
                    Statistic = "Maximum",
                    Label = "Unhealthy Hosts",
                }
            );

            // ── CloudWatch Alarms ─────────────────────────────────────────────

            // [ECS-1] CPU cao → xem xét scale-up hoặc optimize
            var ecsCpuAlarm = CreateAlarm(
                "EcsCpuHighAlarm",
                new AlarmProps
                {
                    AlarmName = "ECS-CPU-High",
                    AlarmDescription =
                        "ECS CPU > 80% trong 15 phút — xem xét scale-up hoặc optimize code",
                    Metric = ecsCpuMetric,
                    Threshold = 80,
                    EvaluationPeriods = 3, // 3 x 5min = 15 phút
                    ComparisonOperator = ComparisonOperator.GREATER_THAN_THRESHOLD,
                    TreatMissingData = TreatMissingData.NOT_BREACHING,
                },
                alarmAction,
                notifyOk: true
            );

            // [ECS-2] Memory cao → tăng Task Memory hoặc investigate memory leak
            var ecsMemoryAlarm = CreateAlarm(
                "EcsMemoryHighAlarm",
                new AlarmProps
                {
                    AlarmName = "ECS-Memory-High",
                    AlarmDescription =
                        "ECS Memory > 80% trong 15 phút — tăng Task Memory hoặc investigate leak",
                    Metric = ecsMemoryMetric,
                    Threshold = 80,
                    EvaluationPeriods = 3,
                    ComparisonOperator = ComparisonOperator.GREATER_THAN_THRESHOLD,
                    TreatMissingData = TreatMissingData.NOT_BREACHING,
                },
                alarmAction,
                notifyOk: true
            );

            // [ALB-1] 5XX errors → ứng dụng đang có lỗi
            var alb5xxAlarm = CreateAlarm(
                "Alb5xxAlarm",
                new AlarmProps
                {
                    AlarmName = "ALB-5XX-Errors",
                    AlarmDescription = "ALB nhận > 10 lỗi 5XX trong 5 phút — ứng dụng đang bị lỗi",
                    Metric = alb5xxMetric,
                    Threshold = 10,
                    EvaluationPeriods = 1,
                    ComparisonOperator = ComparisonOperator.GREATER_THAN_THRESHOLD,
                    TreatMissingData = TreatMissingData.NOT_BREACHING,
                },
                alarmAction,
                notifyOk: false
            );

            // [ALB-2] Response time cao → bottleneck ở app hoặc DB
            var albResponseTimeAlarm = CreateAlarm(
                "AlbResponseTimeAlarm",
                new AlarmProps
                {
                    AlarmName = "ALB-High-Response-Time",
                    AlarmDescription =
                        "ALB p99 Response Time > 2s trong 10 phút — bottleneck ở app hoặc DB",
                    Metric = albResponseTimeP99,
                    Threshold = 2,
                    EvaluationPeriods = 2, // 2 x 5min = 10 phút
                    ComparisonOperator = ComparisonOperator.GREATER_THAN_THRESHOLD,
                    TreatMissingData = TreatMissingData.NOT_BREACHING,
                },
                alarmAction,
                notifyOk: true
            );

            // [ALB-3] Unhealthy host → ECS task crash hoặc health check fail (critical)
            var albUnhealthyHostAlarm = CreateAlarm(
                "AlbUnhealthyHostAlarm",
                new AlarmProps
                {
                    AlarmName = "ALB-Unhealthy-Hosts",
                    AlarmDescription =
                        "ALB phát hiện host unhealthy 2 phút liên tiếp — ECS task đang crash",
                    Metric = albUnhealthyHostMetric,
                    Threshold = 0,
                    EvaluationPeriods = 2, // 2 x 1min = 2 phút
                    ComparisonOperator = ComparisonOperator.GREATER_THAN_THRESHOLD,
                    TreatMissingData = TreatMissingData.NOT_BREACHING,
                },
                alarmAction,
                notifyOk: true
            );

            // [RDS-1] Aurora CPU cao
            var rdsCpuAlarm = CreateAlarm(
                "RdsCpuHighAlarm",
                new AlarmProps
                {
                    AlarmName = "Aurora-CPU-High",
                    AlarmDescription =
                        "Aurora CPU > 80% trong 15 phút — xem xét upgrade instance hoặc read replica",
                    Metric = rdsCpuMetric,
                    Threshold = 80,
                    EvaluationPeriods = 3,
                    ComparisonOperator = ComparisonOperator.GREATER_THAN_THRESHOLD,
                    TreatMissingData = TreatMissingData.NOT_BREACHING,
                },
                alarmAction,
                notifyOk: true
            );

            // [RDS-2] Connections cao → RDS Proxy giúp giảm nhưng vẫn cần theo dõi
            var rdsConnectionsAlarm = CreateAlarm(
                "RdsConnectionsHighAlarm",
                new AlarmProps
                {
                    AlarmName = "Aurora-Connections-High",
                    AlarmDescription =
                        "Aurora DatabaseConnections > 100 — xem xét tối ưu connection pool",
                    Metric = rdsConnectionsMetric,
                    Threshold = 100,
                    EvaluationPeriods = 2,
                    ComparisonOperator = ComparisonOperator.GREATER_THAN_THRESHOLD,
                    TreatMissingData = TreatMissingData.NOT_BREACHING,
                },
                alarmAction,
                notifyOk: false
            );

            // [RDS-3] Freeable Memory thấp → Aurora sắp hết RAM, nguy cơ OOM
            var rdsFreeMemoryAlarm = CreateAlarm(
                "RdsFreeMemoryLowAlarm",
                new AlarmProps
                {
                    AlarmName = "Aurora-Low-Freeable-Memory",
                    AlarmDescription =
                        "Aurora FreeableMemory < 200 MB — nguy cơ OOM, xem xét upgrade instance",
                    Metric = rdsFreeMemoryMetric,
                    Threshold = 200_000_000, // 200 MB tính bằng bytes
                    EvaluationPeriods = 2,
                    ComparisonOperator = ComparisonOperator.LESS_THAN_THRESHOLD,
                    TreatMissingData = TreatMissingData.NOT_BREACHING,
                },
                alarmAction,
                notifyOk: true
            );

            // ── CloudWatch Dashboard ──────────────────────────────────────────
            var dashboard = new Dashboard(
                this,
                "InfraDashboard",
                new DashboardProps
                {
                    DashboardName = "InfraOverview",
                    DefaultInterval = Duration.Hours(3),
                }
            );

            dashboard.AddWidgets(
                // Row 1: ECS
                new TextWidget(
                    new TextWidgetProps
                    {
                        Markdown = "# 🖥️ ECS Fargate",
                        Width = 24,
                        Height = 1,
                    }
                ),
                new GraphWidget(
                    new GraphWidgetProps
                    {
                        Title = "ECS CPU Utilization (%)",
                        Left = new IMetric[] { ecsCpuMetric },
                        LeftAnnotations = new[] { ecsCpuAlarm.ToAnnotation() },
                        Width = 12,
                        Height = 6,
                    }
                ),
                new GraphWidget(
                    new GraphWidgetProps
                    {
                        Title = "ECS Memory Utilization (%)",
                        Left = new IMetric[] { ecsMemoryMetric },
                        LeftAnnotations = new[] { ecsMemoryAlarm.ToAnnotation() },
                        Width = 12,
                        Height = 6,
                    }
                ),
                // Row 2: ALB
                new TextWidget(
                    new TextWidgetProps
                    {
                        Markdown = "# ⚖️ Application Load Balancer",
                        Width = 24,
                        Height = 1,
                    }
                ),
                new GraphWidget(
                    new GraphWidgetProps
                    {
                        Title = "ALB HTTP Error Counts (5min sum)",
                        Left = new IMetric[] { alb5xxMetric, alb4xxMetric },
                        LeftAnnotations = new[] { alb5xxAlarm.ToAnnotation() },
                        Width = 12,
                        Height = 6,
                    }
                ),
                new GraphWidget(
                    new GraphWidgetProps
                    {
                        Title = "ALB Target Response Time (s)",
                        Left = new IMetric[] { albResponseTimeP99, albResponseTimeP50 },
                        LeftAnnotations = new[] { albResponseTimeAlarm.ToAnnotation() },
                        Width = 12,
                        Height = 6,
                    }
                ),
                // Row 3: Aurora
                new TextWidget(
                    new TextWidgetProps
                    {
                        Markdown = "# 🗄️ Aurora MySQL",
                        Width = 24,
                        Height = 1,
                    }
                ),
                new GraphWidget(
                    new GraphWidgetProps
                    {
                        Title = "Aurora CPU Utilization (%)",
                        Left = new IMetric[] { rdsCpuMetric },
                        LeftAnnotations = new[] { rdsCpuAlarm.ToAnnotation() },
                        Width = 8,
                        Height = 6,
                    }
                ),
                new GraphWidget(
                    new GraphWidgetProps
                    {
                        Title = "Aurora Database Connections",
                        Left = new IMetric[] { rdsConnectionsMetric },
                        LeftAnnotations = new[] { rdsConnectionsAlarm.ToAnnotation() },
                        Width = 8,
                        Height = 6,
                    }
                ),
                new GraphWidget(
                    new GraphWidgetProps
                    {
                        Title = "Aurora Freeable Memory (bytes)",
                        Left = new IMetric[] { rdsFreeMemoryMetric },
                        LeftAnnotations = new[] { rdsFreeMemoryAlarm.ToAnnotation() },
                        Width = 8,
                        Height = 6,
                    }
                )
            );

            // Output Dashboard URL để dễ truy cập
            new CfnOutput(
                this,
                "DashboardUrl",
                new CfnOutputProps
                {
                    Value =
                        $"https://{Stack.Of(this).Region}.console.aws.amazon.com/cloudwatch/home#dashboards:name=InfraOverview",
                    Description = "CloudWatch Dashboard — xem tổng quan toàn bộ hệ thống",
                }
            );
        }

        /// <summary>Helper tạo Alarm và gán action, tránh lặp code.</summary>
        private Alarm CreateAlarm(string id, AlarmProps alarmProps, SnsAction action, bool notifyOk)
        {
            var alarm = new Alarm(this, id, alarmProps);
            alarm.AddAlarmAction(action);
            if (notifyOk)
                alarm.AddOkAction(action); // Gửi email khi Alarm trở về OK
            return alarm;
        }
    }
}
