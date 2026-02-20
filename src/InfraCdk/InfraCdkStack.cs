using System.Linq;
using Amazon.CDK;
using Amazon.CDK.AWS.ApplicationAutoScaling;
using Amazon.CDK.AWS.CertificateManager;
using Amazon.CDK.AWS.CloudFront;
using Amazon.CDK.AWS.CloudFront.Origins;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.RDS;
using Amazon.CDK.AWS.Route53;
using Amazon.CDK.AWS.Route53.Targets;
using Amazon.CDK.AWS.SecretsManager;
using Amazon.CDK.AWS.WAFv2;
using Constructs;

namespace InfraCdk
{
    public class InfraCdkStack : Stack
    {
        internal InfraCdkStack(Construct scope, string id, IStackProps props = null)
            : base(scope, id, props)
        {
            // Tạo VPC (Virtual Private Cloud) với CIDR 10.0.0.0/16, không tạo subnet tự động, không tạo NAT Gateway tự động
            var vpc = new Vpc(
                this,
                "MyVPC",
                new VpcProps
                {
                    IpAddresses = IpAddresses.Cidr("10.0.0.0/16"),
                    MaxAzs = 2,
                    SubnetConfiguration = new SubnetConfiguration[] { }, // Không tạo subnet tự động
                    NatGateways = 0, // Không tạo NAT Gateway tự động
                }
            );

            // Tạo 2 public subnet thủ công trên 2 AZ (mỗi subnet ở một Availability Zone, cho phép gán public IP khi launch EC2)
            var publicSubnet1 = new Subnet(
                this,
                "PublicSubnet1",
                new SubnetProps
                {
                    VpcId = vpc.VpcId,
                    AvailabilityZone = vpc.AvailabilityZones[0],
                    CidrBlock = "10.0.1.0/24",
                    MapPublicIpOnLaunch = true,
                }
            );
            var publicSubnet2 = new Subnet(
                this,
                "PublicSubnet2",
                new SubnetProps
                {
                    VpcId = vpc.VpcId,
                    AvailabilityZone = vpc.AvailabilityZones[1],
                    CidrBlock = "10.0.2.0/24",
                    MapPublicIpOnLaunch = true,
                }
            );

            // Tạo Internet Gateway (IGW) thủ công và gắn vào VPC để các public subnet có thể truy cập Internet
            var igw = new CfnInternetGateway(this, "MyIGW");
            new CfnVPCGatewayAttachment(
                this,
                "IGWAttachment",
                new CfnVPCGatewayAttachmentProps { VpcId = vpc.VpcId, InternetGatewayId = igw.Ref }
            );

            // Tạo Route Table cho public subnet, thêm route mặc định ra Internet và gắn vào từng public subnet
            var publicRouteTable = new CfnRouteTable(
                this,
                "PublicRouteTable",
                new CfnRouteTableProps { VpcId = vpc.VpcId }
            );
            // Route mặc định ra Internet
            new CfnRoute(
                this,
                "DefaultRouteToInternet",
                new CfnRouteProps
                {
                    RouteTableId = publicRouteTable.Ref,
                    DestinationCidrBlock = "0.0.0.0/0",
                    GatewayId = igw.Ref,
                }
            );
            // Gắn route table vào từng public subnet
            new CfnSubnetRouteTableAssociation(
                this,
                "PublicSubnet1RouteTableAssoc",
                new CfnSubnetRouteTableAssociationProps
                {
                    SubnetId = publicSubnet1.SubnetId,
                    RouteTableId = publicRouteTable.Ref,
                }
            );
            new CfnSubnetRouteTableAssociation(
                this,
                "PublicSubnet2RouteTableAssoc",
                new CfnSubnetRouteTableAssociationProps
                {
                    SubnetId = publicSubnet2.SubnetId,
                    RouteTableId = publicRouteTable.Ref,
                }
            );

            // Tạo 2 private subnet thủ công trên 2 AZs (mỗi subnet ở một Availability Zone, không gán public IP)
            var privateSubnet1 = new Subnet(
                this,
                "PrivateSubnet1",
                new SubnetProps
                {
                    VpcId = vpc.VpcId,
                    AvailabilityZone = vpc.AvailabilityZones[0],
                    CidrBlock = "10.0.11.0/24",
                    MapPublicIpOnLaunch = false,
                }
            );
            var privateSubnet2 = new Subnet(
                this,
                "PrivateSubnet2",
                new SubnetProps
                {
                    VpcId = vpc.VpcId,
                    AvailabilityZone = vpc.AvailabilityZones[1],
                    CidrBlock = "10.0.12.0/24",
                    MapPublicIpOnLaunch = false,
                }
            );

            // Tạo Elastic IP (EIP) để gán cho NAT Gateway (giúp private subnet truy cập Internet)
            var natEip = new CfnEIP(this, "NatEIP", new CfnEIPProps { Domain = "vpc" });

            // Tạo NAT Gateway thủ công trong public subnet 1, dùng EIP ở trên
            var natGateway = new CfnNatGateway(
                this,
                "NatGateway",
                new CfnNatGatewayProps
                {
                    SubnetId = publicSubnet1.SubnetId,
                    AllocationId = natEip.AttrAllocationId,
                }
            );

            // Tạo Route Table cho private subnet, thêm route mặc định ra Internet qua NAT Gateway và gắn vào từng private subnet
            var privateRouteTable = new CfnRouteTable(
                this,
                "PrivateRouteTable",
                new CfnRouteTableProps { VpcId = vpc.VpcId }
            );
            new CfnRoute(
                this,
                "PrivateDefaultRouteToNatGateway",
                new CfnRouteProps
                {
                    RouteTableId = privateRouteTable.Ref,
                    DestinationCidrBlock = "0.0.0.0/0",
                    NatGatewayId = natGateway.Ref,
                }
            );
            new CfnSubnetRouteTableAssociation(
                this,
                "PrivateSubnet1RouteTableAssoc",
                new CfnSubnetRouteTableAssociationProps
                {
                    SubnetId = privateSubnet1.SubnetId,
                    RouteTableId = privateRouteTable.Ref,
                }
            );
            new CfnSubnetRouteTableAssociation(
                this,
                "PrivateSubnet2RouteTableAssoc",
                new CfnSubnetRouteTableAssociationProps
                {
                    SubnetId = privateSubnet2.SubnetId,
                    RouteTableId = privateRouteTable.Ref,
                }
            );

            // Tạo Security Group cho Application Load Balancer (ALB), cho phép HTTP từ mọi nơi và outbound HTTPS
            var albSecurityGroup = new SecurityGroup(
                this,
                "ALBSecurityGroup",
                new SecurityGroupProps
                {
                    Vpc = vpc,
                    AllowAllOutbound = true,
                    Description = "Security group for Application Load Balancer",
                }
            );
            albSecurityGroup.AddIngressRule(
                Peer.AnyIpv4(),
                Port.Tcp(80),
                "Allow HTTP traffic from anywhere"
            );
            albSecurityGroup.AddEgressRule(
                Peer.AnyIpv4(),
                Port.Tcp(443),
                "Allow HTTPS traffic to anywhere"
            );

            // Tạo Application Load Balancer (ALB) trong public subnet, gắn security group ở trên, tạo listener HTTP
            var alb = new ApplicationLoadBalancer(
                this,
                "MyALB",
                new ApplicationLoadBalancerProps
                {
                    Vpc = vpc,
                    InternetFacing = true,
                    LoadBalancerName = "MyALB",
                    SecurityGroup = albSecurityGroup,
                    VpcSubnets = new SubnetSelection
                    {
                        Subnets = new ISubnet[] { publicSubnet1, publicSubnet2 },
                    },
                }
            );

            // Tạo S3 bucket để lưu trữ Access Logs của ALB (giúp debug và audit)
            var albLogBucket = new Amazon.CDK.AWS.S3.Bucket(
                this,
                "ALBLogBucket",
                new Amazon.CDK.AWS.S3.BucketProps
                {
                    RemovalPolicy = RemovalPolicy.DESTROY, // Xóa bucket khi destroy stack (chỉ dùng cho lab/dev)
                    AutoDeleteObjects = true, // Tự động xóa objects trong bucket khi destroy
                    Encryption = Amazon.CDK.AWS.S3.BucketEncryption.S3_MANAGED,
                    BlockPublicAccess = Amazon.CDK.AWS.S3.BlockPublicAccess.BLOCK_ALL,
                    EnforceSSL = true,
                }
            );

            // Kích hoạt ghi log truy cập cho ALB vào bucket vừa tạo
            alb.LogAccessLogs(albLogBucket);
            // Tạo listener cho ALB
            // Tạo listener cho ALB - Redirect HTTP sang HTTPS
            var listener = alb.AddListener(
                "Listener",
                new BaseApplicationListenerProps
                {
                    Port = 80,
                    Open = true,
                    DefaultAction = ListenerAction.Redirect(
                        new RedirectOptions
                        {
                            Protocol = "HTTPS",
                            Port = "443",
                            Permanent = true,
                        }
                    ),
                }
            );

            // Tạo Security Group cho ECS Fargate, chỉ cho phép nhận HTTP từ ALB
            var ecsSecurityGroup = new SecurityGroup(
                this,
                "ECSSecurityGroup",
                new SecurityGroupProps
                {
                    Vpc = vpc,
                    AllowAllOutbound = true,
                    Description = "Security group for ECS Fargate tasks",
                }
            );
            ecsSecurityGroup.AddIngressRule(
                albSecurityGroup,
                Port.Tcp(80),
                "Allow HTTP traffic from ALB"
            );

            // Tạo Security Group cho RDS, chỉ cho phép nhận MySQL traffic từ ECS Fargate
            var rdsSecurityGroup = new SecurityGroup(
                this,
                "RDSSecurityGroup",
                new SecurityGroupProps
                {
                    Vpc = vpc,
                    AllowAllOutbound = true,
                    Description = "Security group for RDS instance",
                }
            );
            rdsSecurityGroup.AddIngressRule(
                ecsSecurityGroup,
                Port.Tcp(3306),
                "Allow MySQL traffic from ECS tasks"
            );

            // Tạo ECS Cluster để quản lý các Fargate Service, gắn vào VPC
            var ecsCluster = new Cluster(
                this,
                "ECSCluster",
                new ClusterProps { Vpc = vpc, ClusterName = "ECSCluster" }
            );

            // Định nghĩa Task cho Fargate Service (chỉ định CPU, RAM, image, port mapping cho container)
            var taskDefinition = new FargateTaskDefinition(
                this,
                "FargateTaskDef",
                new FargateTaskDefinitionProps { Cpu = 256, MemoryLimitMiB = 512 }
            );
            taskDefinition.AddContainer(
                "AppContainer",
                new ContainerDefinitionOptions
                {
                    Image = ContainerImage.FromRegistry("amazon/amazon-ecs-sample"),
                    PortMappings = new[] { new PortMapping { ContainerPort = 80 } },
                }
            );

            // Tạo Fargate Service chạy trong private subnet, gắn security group, chỉ định số lượng task mong muốn
            var fargateService = new FargateService(
                this,
                "FargateService",
                new FargateServiceProps
                {
                    Cluster = ecsCluster,
                    ServiceName = "MyFargateService",
                    TaskDefinition = taskDefinition,
                    AssignPublicIp = false,
                    DesiredCount = 2,
                    SecurityGroups = new[] { ecsSecurityGroup },
                    VpcSubnets = new SubnetSelection
                    {
                        Subnets = new ISubnet[] { privateSubnet1, privateSubnet2 },
                    },
                }
            );

            // Tạo Target Group cho ALB, gắn Fargate Service vào target group để nhận traffic từ ALB
            var targetGroup = new ApplicationTargetGroup(
                this,
                "FargateTargetGroup",
                new ApplicationTargetGroupProps
                {
                    Vpc = vpc,
                    Port = 80,
                    Protocol = ApplicationProtocol.HTTP,
                    TargetType = TargetType.IP,
                }
            );

            fargateService.AttachToApplicationTargetGroup(targetGroup);

            // --- HTTPS & Domain Configuration ---
            const string domainName = "example.com";

            // 1. Tìm Hosted Zone đã có trong Route 53
            var hostedZone = HostedZone.FromLookup(
                this,
                "HostedZone",
                new HostedZoneProviderProps { DomainName = domainName }
            );

            // 2. Tạo Certificate Manager (ACM) để quản lý SSL Certificate
            var certificate = new Certificate(
                this,
                "SiteCertificate",
                new CertificateProps
                {
                    DomainName = domainName,
                    SubjectAlternativeNames = new[] { $"www.{domainName}" },
                    Validation = CertificateValidation.FromDns(hostedZone),
                }
            );

            // 3. Thêm HTTPS Listener vào ALB - Cấu hình bảo mật kiểm tra Header từ CloudFront
            var customHeaderName = "X-Origin-Verify";
            // Tạo Secret ngẫu nhiên trong AWS Secrets Manager thay vì hardcode
            var headerSecret = new Secret(
                this,
                "HeaderSecret",
                new SecretProps
                {
                    GenerateSecretString = new SecretStringGenerator
                    {
                        ExcludePunctuation = true,
                        IncludeSpace = false,
                        PasswordLength = 64,
                    },
                }
            );
            // Lấy giá trị secret để sử dụng (CDK sẽ resolve lúc deploy)
            var customHeaderValue = headerSecret.SecretValue.UnsafeUnwrap();

            var httpsListener = alb.AddListener(
                "HttpsListener",
                new BaseApplicationListenerProps
                {
                    Port = 443,
                    Certificates = new[]
                    {
                        ListenerCertificate.FromCertificateManager(certificate),
                    },
                    Open = true,
                    // Mặc định từ chối tất cả request không có header hợp lệ (từ CloudFront)
                    DefaultAction = ListenerAction.FixedResponse(
                        403,
                        new FixedResponseOptions
                        {
                            ContentType = "text/plain",
                            MessageBody = "Access Denied",
                        }
                    ),
                }
            );

            // Chỉ forward request vào Target Group nếu có Header bí mật từ CloudFront
            httpsListener.AddTargetGroups(
                "AppTarget",
                new AddApplicationTargetGroupsProps
                {
                    TargetGroups = new[] { targetGroup },
                    Priority = 1,
                    Conditions = new[]
                    {
                        ListenerCondition.HttpHeader(customHeaderName, new[] { customHeaderValue }),
                    },
                }
            );

            // 4. Tạo bản ghi A Record (Alias) trỏ tên miền về ALB
            // Lưu ý: Chúng ta sẽ trỏ về CloudFront ở bước 6, nên bước này có thể bỏ qua hoặc comment out nếu muốn đi vòng qua CloudFront
            // new ARecord(this, "AliasRecord", new ARecordProps
            // {
            //     Zone = hostedZone,
            //     Target = RecordTarget.FromAlias(new LoadBalancerTarget(alb)),
            //     RecordName = domainName,
            // });

            // (Optional) Redirect HTTP sang HTTPS
            // Chúng ta sẽ add một listener mới port 8888 tạm thời để tránh conflict với listener 80 cũ (sẽ xóa sau)
            // Hoặc tốt hơn là không add ở đây mà sửa listener 80 cũ.

            // 5. Tạo CloudFront Distribution
            var distribution = new Distribution(
                this,
                "SiteDistribution",
                new DistributionProps
                {
                    DefaultBehavior = new BehaviorOptions
                    {
                        Origin = new HttpOrigin(
                            alb.LoadBalancerDnsName,
                            new HttpOriginProps
                            {
                                ProtocolPolicy = OriginProtocolPolicy.HTTPS_ONLY,
                                CustomHeaders = new System.Collections.Generic.Dictionary<
                                    string,
                                    string
                                >
                                {
                                    { customHeaderName, customHeaderValue },
                                },
                            }
                        ),
                        ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
                        AllowedMethods = AllowedMethods.ALLOW_ALL,
                        Compress = true,
                    },
                    DomainNames = new[] { domainName, $"www.{domainName}" },
                    Certificate = certificate,
                }
            );

            // 6. Cập nhật A Record trỏ tên miền về CloudFront
            new ARecord(
                this,
                "AliasRecordCF",
                new ARecordProps
                {
                    Zone = hostedZone,
                    Target = RecordTarget.FromAlias(new CloudFrontTarget(distribution)),
                    RecordName = domainName,
                }
            );

            var scaling = fargateService.AutoScaleTaskCount(
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

            // Tạo RDS subnet group để RDS instance có thể sử dụng 2 private subnet
            var rdsSubnetGroup = new SubnetGroup(
                this,
                "RDSSubnetGroup",
                new SubnetGroupProps
                {
                    Vpc = vpc,
                    SubnetGroupName = "RDSSubnetGroup",
                    Description = "Subnet group for RDS instance",
                    VpcSubnets = new SubnetSelection
                    {
                        Subnets = new ISubnet[] { privateSubnet1, privateSubnet2 },
                    },
                }
            );

            // 6. Cấu hình Database User & Password Rotation
            // Định nghĩa Credentials tự sinh (Username: sysadmin) và lưu vào Secrets Manager
            var dbCredentials = Credentials.FromGeneratedSecret("sysadmin");

            // Tạo Aurora MySQL cluster trong private subnet, gắn security group, chỉ định thông số cơ bản
            var auroraCluster = new DatabaseCluster(
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
                    Credentials = dbCredentials, // Sử dụng credentials vừa tạo
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
                    Vpc = vpc,
                    VpcSubnets = new SubnetSelection
                    {
                        Subnets = new ISubnet[] { privateSubnet1, privateSubnet2 },
                    },
                    SecurityGroups = new[] { rdsSecurityGroup },
                    SubnetGroup = rdsSubnetGroup,
                    DefaultDatabaseName = "mydatabase",
                    RemovalPolicy = RemovalPolicy.DESTROY, // Chỉ dùng cho môi trường dev/test
                }
            );

            // Cấu hình tự động xoay vòng mật khẩu (Password Rotation) mỗi 30 ngày
            auroraCluster.AddRotationSingleUser(
                "Rotation",
                new RotationSingleUserOptions
                {
                    AutomaticallyAfter = Duration.Days(30),
                    // Lambda function thực hiện rotate cần nằm trong cùng VPC để connect DB
                    VpcSubnets = new SubnetSelection
                    {
                        Subnets = new ISubnet[] { privateSubnet1, privateSubnet2 },
                    },
                }
            );

            // Proxy cho phép Fargate Service kết nối đến RDS cluster qua endpoint của cluster (sử dụng RDS Proxy để quản lý kết nối hiệu quả hơn)
            // Proxy role
            var proxyRole = new Role(
                this,
                "RDSProxyRole",
                new RoleProps
                {
                    AssumedBy = new ServicePrincipal("rds.amazonaws.com"),
                    ManagedPolicies = new[]
                    {
                        ManagedPolicy.FromAwsManagedPolicyName("AmazonRDSProxyReadOnlyAccess"),
                        ManagedPolicy.FromAwsManagedPolicyName(
                            "service-role/AmazonRDSProxyServiceRolePolicy"
                        ),
                    },
                }
            );
            // Tạo RDS Proxy, gắn vào Aurora cluster, chỉ định IAM role và security group cho proxy
            var rdsProxy = new DatabaseProxy(
                this,
                "RDSProxy",
                new DatabaseProxyProps
                {
                    ProxyTarget = ProxyTarget.FromCluster(auroraCluster),
                    Secrets = auroraCluster.Secret != null ? new[] { auroraCluster.Secret } : null,
                    Vpc = vpc,
                    SecurityGroups = new[] { rdsSecurityGroup },
                    Role = proxyRole,
                    IdleClientTimeout = Duration.Seconds(300),
                    RequireTLS = true,
                    VpcSubnets = new SubnetSelection
                    {
                        Subnets = new ISubnet[] { privateSubnet1, privateSubnet2 },
                    },
                    DebugLogging = true,
                }
            );

            // Xuất output endpoint của RDS Proxy để Fargate Service có thể kết nối đến database qua proxy
            new CfnOutput(
                this,
                "RDSProxyEndpoint",
                new CfnOutputProps
                {
                    Value = rdsProxy.Endpoint,
                    Description = "Endpoint for RDS Proxy to connect to Aurora cluster",
                    ExportName = "RDSProxyEndpoint",
                }
            );

            // S3 bucket để lưu trữ tài nguyên tĩnh (nếu cần thiết), có thể gắn vào Fargate Service để ứng dụng sử dụng
            var staticBucket = new Amazon.CDK.AWS.S3.Bucket(
                this,
                "StaticBucket",
                new Amazon.CDK.AWS.S3.BucketProps
                {
                    BucketName = "my-static-resources-bucket",
                    Versioned = true,
                    RemovalPolicy = RemovalPolicy.DESTROY, // Chỉ dùng cho môi trường dev/test
                }
            );

            // VPC Gateway Endpoint cho S3 để Fargate Service có thể truy cập S3 mà không cần đi qua Internet (tăng bảo mật và hiệu suất)
            vpc.AddGatewayEndpoint(
                "S3Endpoint",
                new GatewayVpcEndpointOptions
                {
                    Service = GatewayVpcEndpointAwsService.S3,
                    Subnets = new[]
                    {
                        new SubnetSelection
                        {
                            Subnets = new ISubnet[] { privateSubnet1, privateSubnet2 },
                        },
                    },
                }
            );

            // Tạo CloudWatch Log Group để Fargate Service có thể ghi log (giúp theo dõi và debug ứng dụng)
            var logGroup = new Amazon.CDK.AWS.Logs.LogGroup(
                this,
                "FargateLogGroup",
                new Amazon.CDK.AWS.Logs.LogGroupProps
                {
                    LogGroupName = "/ecs/fargate-service-logs",
                    Retention = Amazon.CDK.AWS.Logs.RetentionDays.ONE_WEEK,
                    RemovalPolicy = RemovalPolicy.DESTROY, // Chỉ dùng cho môi trường dev/test
                }
            );

            // Tạo WAF (Web Application Firewall) để bảo vệ ALB khỏi các mối đe dọa web phổ biến, gắn WAF vào ALB
            var webAcl = new CfnWebACL(
                this,
                "WebACL",
                new CfnWebACLProps
                {
                    DefaultAction = new CfnWebACL.DefaultActionProperty
                    {
                        Allow = new CfnWebACL.AllowActionProperty(),
                    },
                    Scope = "REGIONAL",
                    VisibilityConfig = new CfnWebACL.VisibilityConfigProperty
                    {
                        SampledRequestsEnabled = true,
                        CloudWatchMetricsEnabled = true,
                        MetricName = "WebACLMetric",
                    },
                    Rules = new[]
                    {
                        new CfnWebACL.RuleProperty
                        {
                            Name = "AWS-AWSManagedRulesCommonRuleSet",
                            Priority = 1,
                            OverrideAction = new CfnWebACL.OverrideActionProperty { None = null },
                            Statement = new CfnWebACL.StatementProperty
                            {
                                ManagedRuleGroupStatement =
                                    new CfnWebACL.ManagedRuleGroupStatementProperty
                                    {
                                        VendorName = "AWS",
                                        Name = "AWSManagedRulesCommonRuleSet",
                                    },
                            },
                            VisibilityConfig = new CfnWebACL.VisibilityConfigProperty
                            {
                                SampledRequestsEnabled = true,
                                CloudWatchMetricsEnabled = true,
                                MetricName = "AWSManagedRulesCommonRuleSetMetric",
                            },
                        },
                    },
                }
            );
            // Gắn WAF vào ALB
            new CfnWebACLAssociation(
                this,
                "WebACLAssociation",
                new CfnWebACLAssociationProps
                {
                    ResourceArn = alb.LoadBalancerArn,
                    WebAclArn = webAcl.AttrArn,
                }
            );
        }
    }
}
