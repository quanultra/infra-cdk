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
                            InstanceType = Amazon.CDK.AWS.EC2.InstanceType.Of(
                                InstanceClass.BURSTABLE3,
                                InstanceSize.MEDIUM
                            ),
                            PubliclyAccessible = false,
                        }
                    ),
                    Readers = new IClusterInstance[]
                    {
                        ClusterInstance.Provisioned(
                            "reader",
                            new ProvisionedClusterInstanceProps
                            {
                                InstanceType = Amazon.CDK.AWS.EC2.InstanceType.Of(
                                    InstanceClass.BURSTABLE3,
                                    InstanceSize.MEDIUM
                                ),
                                PubliclyAccessible = false,
                            }
                        ),
                    },
                    Vpc = props.Vpc,
                    VpcSubnets = privateSubnets,
                    SecurityGroups = new[] { props.RdsSg },
                    SubnetGroup = rdsSubnetGroup,
                    DefaultDatabaseName = "mydatabase",
                    RemovalPolicy = RemovalPolicy.DESTROY, // TODO: Đổi thành SNAPSHOT cho production
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
    }
}
