using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.Logs;
using Constructs;
using InfraCdk;

namespace InfraCdk.Constructs
{
    public class NetworkingConstructProps
    {
        /// <summary>
        /// Cấu hình environment — xác định UseVpcEndpoints vs NAT Gateway.
        /// </summary>
        public EnvironmentConfig EnvConfig { get; set; }
    }

    /// <summary>
    /// Tạo VPC cơ bản gồm public/private subnets, IGW, và kết nối ra ngoài.
    ///<br/>
    /// <b>Chế độ VPC Endpoints (Stg/Prod, UseVpcEndpoints=true):</b><br/>
    ///   Private subnet kết nối ECR/CloudWatch/SecretsManager qua Interface Endpoints.<br/>
    ///   Không có NAT Gateway — traffic không bao giờ ra internet.<br/>
    ///<br/>
    /// <b>Chế độ NAT Gateway (Dev, UseVpcEndpoints=false):</b><br/>
    ///   Private subnet ra internet qua 1 NAT Gateway ở PublicSubnet1.<br/>
    ///   Rẻ hơn khi traffic thấp (~$32/th vs ~$58/th cho 4 endpoints × 2 AZ).
    /// </summary>
    public class NetworkingConstruct : Construct
    {
        public Vpc Vpc { get; }
        public Subnet PublicSubnet1 { get; }
        public Subnet PublicSubnet2 { get; }
        public Subnet PrivateSubnet1 { get; }
        public Subnet PrivateSubnet2 { get; }

        public NetworkingConstruct(Construct scope, string id, NetworkingConstructProps props)
            : base(scope, id)
        {
            // --- VPC ---
            Vpc = new Vpc(
                this,
                "MyVPC",
                new VpcProps
                {
                    IpAddresses = IpAddresses.Cidr("10.0.0.0/16"),
                    MaxAzs = 2,
                    SubnetConfiguration = new SubnetConfiguration[] { },
                    NatGateways = 0,
                }
            );

            // --- Public Subnets ---
            PublicSubnet1 = new Subnet(
                this,
                "PublicSubnet1",
                new SubnetProps
                {
                    VpcId = Vpc.VpcId,
                    AvailabilityZone = Vpc.AvailabilityZones[0],
                    CidrBlock = "10.0.1.0/24",
                    MapPublicIpOnLaunch = true,
                }
            );
            PublicSubnet2 = new Subnet(
                this,
                "PublicSubnet2",
                new SubnetProps
                {
                    VpcId = Vpc.VpcId,
                    AvailabilityZone = Vpc.AvailabilityZones[1],
                    CidrBlock = "10.0.2.0/24",
                    MapPublicIpOnLaunch = true,
                }
            );

            // --- Internet Gateway & Public Route Table ---
            var igw = new CfnInternetGateway(this, "MyIGW");
            new CfnVPCGatewayAttachment(
                this,
                "IGWAttachment",
                new CfnVPCGatewayAttachmentProps { VpcId = Vpc.VpcId, InternetGatewayId = igw.Ref }
            );

            var publicRouteTable = new CfnRouteTable(
                this,
                "PublicRouteTable",
                new CfnRouteTableProps { VpcId = Vpc.VpcId }
            );
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
            new CfnSubnetRouteTableAssociation(
                this,
                "PublicSubnet1RouteTableAssoc",
                new CfnSubnetRouteTableAssociationProps
                {
                    SubnetId = PublicSubnet1.SubnetId,
                    RouteTableId = publicRouteTable.Ref,
                }
            );
            new CfnSubnetRouteTableAssociation(
                this,
                "PublicSubnet2RouteTableAssoc",
                new CfnSubnetRouteTableAssociationProps
                {
                    SubnetId = PublicSubnet2.SubnetId,
                    RouteTableId = publicRouteTable.Ref,
                }
            );

            // --- Private Subnets (Isolated — không có NAT Gateway) ---
            PrivateSubnet1 = new Subnet(
                this,
                "PrivateSubnet1",
                new SubnetProps
                {
                    VpcId = Vpc.VpcId,
                    AvailabilityZone = Vpc.AvailabilityZones[0],
                    CidrBlock = "10.0.11.0/24",
                    MapPublicIpOnLaunch = false,
                }
            );
            PrivateSubnet2 = new Subnet(
                this,
                "PrivateSubnet2",
                new SubnetProps
                {
                    VpcId = Vpc.VpcId,
                    AvailabilityZone = Vpc.AvailabilityZones[1],
                    CidrBlock = "10.0.12.0/24",
                    MapPublicIpOnLaunch = false,
                }
            );

            // --- Private Route Table (Isolated, không có 0.0.0.0/0) ---
            var privateRouteTable = new CfnRouteTable(
                this,
                "PrivateRouteTable",
                new CfnRouteTableProps { VpcId = Vpc.VpcId }
            );
            new CfnSubnetRouteTableAssociation(
                this,
                "PrivateSubnet1RouteTableAssoc",
                new CfnSubnetRouteTableAssociationProps
                {
                    SubnetId = PrivateSubnet1.SubnetId,
                    RouteTableId = privateRouteTable.Ref,
                }
            );
            new CfnSubnetRouteTableAssociation(
                this,
                "PrivateSubnet2RouteTableAssoc",
                new CfnSubnetRouteTableAssociationProps
                {
                    SubnetId = PrivateSubnet2.SubnetId,
                    RouteTableId = privateRouteTable.Ref,
                }
            );

            var privateSubnets = new SubnetSelection
            {
                Subnets = new ISubnet[] { PrivateSubnet1, PrivateSubnet2 },
            };

            if (props.EnvConfig.UseVpcEndpoints)
            {
                // ─── Chế độ VPC Endpoints (Stg/Prod) ────────────────────────────
                // #4: Private subnet kết nối AWS services qua Interface Endpoints.
                // Không có NAT Gateway → traffic không ra internet, tăng bảo mật.
                // Chi phí: ~$58/tháng (4 endpoints × 2 AZ × $0.013/h × 730h)
                var vpcEndpointSg = new SecurityGroup(
                    this,
                    "VpcEndpointSG",
                    new SecurityGroupProps
                    {
                        Vpc = Vpc,
                        AllowAllOutbound = true,
                        Description = "Security Group for VPC Interface Endpoints (ECR, Logs, SM)",
                    }
                );
                vpcEndpointSg.AddIngressRule(
                    Peer.Ipv4(Vpc.VpcCidrBlock),
                    Port.Tcp(443),
                    "Allow HTTPS from within VPC"
                );

                // ECR Docker — Fargate cần để pull layer (data plane)
                Vpc.AddInterfaceEndpoint(
                    "EcrDockerEndpoint",
                    new InterfaceVpcEndpointOptions
                    {
                        Service = InterfaceVpcEndpointAwsService.ECR_DOCKER,
                        SecurityGroups = new[] { vpcEndpointSg },
                        Subnets = privateSubnets,
                    }
                );
                // ECR API — Fargate cần để authenticate và lấy manifest (control plane)
                Vpc.AddInterfaceEndpoint(
                    "EcrApiEndpoint",
                    new InterfaceVpcEndpointOptions
                    {
                        Service = InterfaceVpcEndpointAwsService.ECR,
                        SecurityGroups = new[] { vpcEndpointSg },
                        Subnets = privateSubnets,
                    }
                );
                // CloudWatch Logs — ECS task ghi log
                Vpc.AddInterfaceEndpoint(
                    "LogsEndpoint",
                    new InterfaceVpcEndpointOptions
                    {
                        Service = InterfaceVpcEndpointAwsService.CLOUDWATCH_LOGS,
                        SecurityGroups = new[] { vpcEndpointSg },
                        Subnets = privateSubnets,
                    }
                );
                // Secrets Manager — ECS rotation Lambda và RDS Proxy đọc secret
                Vpc.AddInterfaceEndpoint(
                    "SecretsManagerEndpoint",
                    new InterfaceVpcEndpointOptions
                    {
                        Service = InterfaceVpcEndpointAwsService.SECRETS_MANAGER,
                        SecurityGroups = new[] { vpcEndpointSg },
                        Subnets = privateSubnets,
                    }
                );
            }
            else
            {
                // ─── Chế độ NAT Gateway (Dev) ─────────────────────────────────
                // #4: Private subnet ra internet qua 1 NAT Gateway ở PublicSubnet1.
                // Chi phí: ~$32/tháng + data transfer (rẻ hơn khi traffic thấp)
                // Nhược điểm: traffic AWS services đi qua internet (không vấn đề với Dev)
                var natEip = new CfnEIP(this, "NatGatewayEIP", new CfnEIPProps { Domain = "vpc" });

                var natGateway = new CfnNatGateway(
                    this,
                    "NatGateway",
                    new CfnNatGatewayProps
                    {
                        SubnetId = PublicSubnet1.SubnetId,
                        AllocationId = natEip.AttrAllocationId,
                    }
                );

                // Private subnets ra internet qua NAT GW
                new CfnRoute(
                    this,
                    "PrivateNatRoute",
                    new CfnRouteProps
                    {
                        RouteTableId = privateRouteTable.Ref,
                        DestinationCidrBlock = "0.0.0.0/0",
                        NatGatewayId = natGateway.Ref,
                    }
                );
            }

            // Gateway Endpoint cho S3 — MIỄN PHÍ, luôn tạo bất kể chế độ nào.
            // Giúp Fargate kéo image layers (lưu trên S3) nhanh hơn và không tốn data transfer fee.

            Vpc.AddGatewayEndpoint(
                "S3Endpoint",
                new GatewayVpcEndpointOptions
                {
                    Service = GatewayVpcEndpointAwsService.S3,
                    Subnets = new[] { privateSubnets },
                }
            );

            // --- VPC Flow Logs ---
            // #12: Log network traffic để audit security incidents và troubleshoot network issues.
            // Chỉ log REJECT traffic để tiết kiệm chi phí — đủ để phát hiện lateral movement.
            var flowLogGroup = new LogGroup(
                this,
                "VpcFlowLogGroup",
                new LogGroupProps
                {
                    LogGroupName = "/vpc/flow-logs",
                    Retention = RetentionDays.ONE_MONTH,
                    RemovalPolicy = RemovalPolicy.DESTROY,
                }
            );

            Vpc.AddFlowLog(
                "VpcFlowLog",
                new FlowLogOptions
                {
                    // Chỉ log REJECT để giảm volume log và chi phí
                    // ACCEPT logs rất nhiều → tốn kém nếu traffic cao
                    TrafficType = FlowLogTrafficType.REJECT,
                    Destination = FlowLogDestination.ToCloudWatchLogs(flowLogGroup),
                }
            );
        }
    }
}
