using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.RDS;
using Constructs;

namespace InfraCdk.Constructs
{
    public class DatabaseConstructProps
    {
        public Vpc Vpc { get; set; }
        public ISubnet PrivateSubnet1 { get; set; }
        public ISubnet PrivateSubnet2 { get; set; }
        public SecurityGroup RdsSg { get; set; }

        /// <summary>
        /// Cấu hình theo environment: xác định RemovalPolicy và Aurora instance type.
        /// Được tạo từ EnvironmentConfig.FromName() trong InfraCdkStack.
        /// </summary>
        public EnvironmentConfig EnvConfig { get; set; }
    }

    /// <summary>
    /// Triển khai Aurora MySQL Cluster (Writer + Reader), RDS Proxy để
    /// pool kết nối hiệu quả, và cấu hình tự động xoay vòng mật khẩu mỗi 30 ngày.
    /// </summary>
    public class DatabaseConstruct : Construct
    {
        public DatabaseCluster AuroraCluster { get; }
        public DatabaseProxy RdsProxy { get; }

        public DatabaseConstruct(Construct scope, string id, DatabaseConstructProps props)
            : base(scope, id)
        {
            var privateSubnets = new SubnetSelection
            {
                Subnets = new ISubnet[] { props.PrivateSubnet1, props.PrivateSubnet2 },
            };

            // --- RDS Subnet Group ---
            var rdsSubnetGroup = new SubnetGroup(
                this,
                "RDSSubnetGroup",
                new SubnetGroupProps
                {
                    Vpc = props.Vpc,
                    SubnetGroupName = "RDSSubnetGroup",
                    Description = "Subnet group for Aurora cluster",
                    VpcSubnets = privateSubnets,
                }
            );

            // --- Aurora MySQL Cluster ---
            // Credentials tự sinh và lưu vào Secrets Manager — không hardcode password
            var dbCredentials = Credentials.FromGeneratedSecret("sysadmin");

            AuroraCluster = new DatabaseCluster(
                this,
                "MyAuroraCluster",
                new DatabaseClusterProps
                {
                    Engine = DatabaseClusterEngine.AuroraMysql(
                        new AuroraMysqlClusterEngineProps
                        {
                            Version = AuroraMysqlEngineVersion.VER_3_04_0,
                        }
                    ),
                    Credentials = dbCredentials,
                    Writer = ClusterInstance.Provisioned(
                        "writer",
                        new ProvisionedClusterInstanceProps
                        {
                            // Dev/Stg: t3.small (~$0.04/h) | Prod: t3.medium (~$0.08/h)
                            InstanceType = Amazon.CDK.AWS.EC2.InstanceType.Of(
                                props.EnvConfig.AuroraInstanceClass,
                                props.EnvConfig.AuroraInstanceSize
                            ),
                            PubliclyAccessible = false,
                        }
                    ),
                    // #6: Readers = 0 cho Dev — không có read workload thực tế.
                    // Aurora hoạt động bình thường với chỉ Writer, Writer cũng serve reads.
                    // Stg/Prod = 1 Reader: tăng HA và failover tự động.
                    Readers = BuildReaderInstances(props.EnvConfig),
                    Vpc = props.Vpc,
                    VpcSubnets = privateSubnets,
                    SecurityGroups = new[] { props.RdsSg },
                    SubnetGroup = rdsSubnetGroup,
                    DefaultDatabaseName = "mydatabase",
                    RemovalPolicy = props.EnvConfig.DbRemovalPolicy,
                }
            );

            // --- Password Rotation mỗi 30 ngày ---
            AuroraCluster.AddRotationSingleUser(
                new RotationSingleUserOptions
                {
                    AutomaticallyAfter = Duration.Days(30),
                    VpcSubnets = privateSubnets,
                }
            );

            // --- RDS Proxy ---
            // Pool kết nối DB; giảm số lượng connection từ Fargate tasks
            var proxyRole = new Role(
                this,
                "RDSProxyRole",
                new RoleProps
                {
                    AssumedBy = new ServicePrincipal("rds.amazonaws.com"),
                    // TODO: Thay bằng inline policy chỉ cho phép secretsmanager:GetSecretValue
                    // trên secret cụ thể thay vì dùng managed policy quá rộng
                    ManagedPolicies = new[]
                    {
                        ManagedPolicy.FromAwsManagedPolicyName("AmazonRDSProxyReadOnlyAccess"),
                        ManagedPolicy.FromAwsManagedPolicyName(
                            "service-role/AmazonRDSProxyServiceRolePolicy"
                        ),
                    },
                }
            );

            RdsProxy = new DatabaseProxy(
                this,
                "RDSProxy",
                new DatabaseProxyProps
                {
                    ProxyTarget = ProxyTarget.FromCluster(AuroraCluster),
                    Secrets = AuroraCluster.Secret != null ? new[] { AuroraCluster.Secret } : null,
                    Vpc = props.Vpc,
                    SecurityGroups = new[] { props.RdsSg },
                    Role = proxyRole,
                    IdleClientTimeout = Duration.Seconds(300),
                    RequireTLS = true,
                    VpcSubnets = privateSubnets,
                    DebugLogging = false, // TODO: Chỉ bật khi troubleshoot — tốn chi phí CloudWatch Logs
                }
            );

            // --- Output ---
            new CfnOutput(
                this,
                "RDSProxyEndpoint",
                new CfnOutputProps
                {
                    Value = RdsProxy.Endpoint,
                    Description =
                        "RDS Proxy endpoint — dùng trong app config thay vì Aurora endpoint trực tiếp",
                    ExportName = "RDSProxyEndpoint",
                }
            );
        }

        /// <summary>
        /// Tạo danh sách Aurora Reader instances dựa trên AuroraReaderCount trong EnvironmentConfig.
        /// Dev: 0 readers → null (Aurora hoạt động bình thường với chỉ Writer).
        /// Stg/Prod: 1 reader → tăng HA và read throughput.
        /// </summary>
        private static IClusterInstance[] BuildReaderInstances(EnvironmentConfig envConfig)
        {
            if (envConfig.AuroraReaderCount <= 0)
                return null; // Aurora chỉ có Writer là hoàn toàn hợp lệ

            var readers = new System.Collections.Generic.List<IClusterInstance>();
            for (int i = 0; i < envConfig.AuroraReaderCount; i++)
            {
                readers.Add(
                    ClusterInstance.Provisioned(
                        $"reader{i + 1}",
                        new ProvisionedClusterInstanceProps
                        {
                            InstanceType = Amazon.CDK.AWS.EC2.InstanceType.Of(
                                envConfig.AuroraInstanceClass,
                                envConfig.AuroraInstanceSize
                            ),
                            PubliclyAccessible = false,
                        }
                    )
                );
            }
            return readers.ToArray();
        }
    }
}
