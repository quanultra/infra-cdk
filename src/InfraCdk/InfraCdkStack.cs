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
            // Tạo listener cho ALB
            var listener = alb.AddListener(
                "Listener",
                new BaseApplicationListenerProps { Port = 80, Open = true }
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
            listener.AddTargetGroups(
                "DefaultTargetGroup",
                new AddApplicationTargetGroupsProps { TargetGroups = new[] { targetGroup } }
            );

            // Thiết lập Auto Scaling cho Fargate Service dựa trên CPU utilization (tự động scale in/out task)
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

            // 3. Thêm HTTPS Listener vào ALB
            alb.AddListener(
                "HttpsListener",
                new BaseApplicationListenerProps
                {
                    Port = 443,
                    Certificates = new[] { certificate },
                    Open = true,
                    DefaultTargetGroups = new[] { targetGroup },
                }
            );

            // 4. Tạo bản ghi A Record (Alias) trỏ tên miền về ALB
            new ARecord(
                this,
                "AliasRecord",
                new ARecordProps
                {
                    Zone = hostedZone,
                    Target = RecordTarget.FromAlias(new LoadBalancerTarget(alb)),
                    RecordName = domainName,
                }
            );

            // (Optional) Redirect HTTP sang HTTPS
            // Nếu bạn muốn ép buộc HTTPS, hãy cấu hình lại listener port 80 ở trên để redirect
            alb.AddListener(
                "HttpListener",
                new BaseApplicationListenerProps
                {
                    Port = 80,
                    Open = true,
                    DefaultTargetGroups = new[] { targetGroup },
                }
            );

            // 5. Tạo CloudFront Distribution để tăng tốc độ truy cập và bảo mật
            var distribution = new Distribution(
                this,
                "SiteDistribution",
                new DistributionProps
                {
                    DefaultBehavior = new BehaviorOptions
                    {
                        Origin = new HttpOrigin(alb.LoadBalancerDnsName),
                        ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
                        AllowedMethods = AllowedMethods.ALLOW_ALL,
                        Compress = true,
                    },
                    Aliases = new[] { domainName, $"www.{domainName}" },
                    Certificate = certificate,
                }
            );

            // 6. Cập nhật A Record trỏ tên miền về CloudFront thay vì ALB
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
        }
    }
}
