using Amazon.CDK.AWS.EC2;
using Constructs;

namespace InfraCdk.Constructs
{
    /// <summary>
    /// Tạo toàn bộ tầng mạng: VPC, Public/Private Subnets, IGW,
    /// Route Tables, và VPC Endpoints (ECR, CloudWatch Logs, Secrets Manager, S3).
    /// </summary>
    public class NetworkingConstruct : Construct
    {
        public Vpc Vpc { get; }
        public Subnet PublicSubnet1 { get; }
        public Subnet PublicSubnet2 { get; }
        public Subnet PrivateSubnet1 { get; }
        public Subnet PrivateSubnet2 { get; }

        public NetworkingConstruct(Construct scope, string id)
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

            // --- VPC Endpoint Security Group ---
            // Thay thế NAT Gateway bằng VPC Endpoints để tiết kiệm chi phí
            var vpcEndpointSg = new SecurityGroup(
                this,
                "VpcEndpointSG",
                new SecurityGroupProps
                {
                    Vpc = Vpc,
                    AllowAllOutbound = true,
                    Description = "Security Group for VPC Endpoints",
                }
            );
            vpcEndpointSg.AddIngressRule(
                Peer.Ipv4(Vpc.VpcCidrBlock),
                Port.Tcp(443),
                "Allow HTTPS from within VPC"
            );

            var privateSubnets = new SubnetSelection
            {
                Subnets = new ISubnet[] { PrivateSubnet1, PrivateSubnet2 },
            };

            // Interface Endpoints — để Fargate kéo image & ghi log mà không cần NAT
            Vpc.AddInterfaceEndpoint(
                "EcrDockerEndpoint",
                new InterfaceVpcEndpointOptions
                {
                    Service = InterfaceVpcEndpointAwsService.ECR_DOCKER,
                    SecurityGroups = new[] { vpcEndpointSg },
                    Subnets = privateSubnets,
                }
            );
            Vpc.AddInterfaceEndpoint(
                "EcrApiEndpoint",
                new InterfaceVpcEndpointOptions
                {
                    Service = InterfaceVpcEndpointAwsService.ECR,
                    SecurityGroups = new[] { vpcEndpointSg },
                    Subnets = privateSubnets,
                }
            );
            Vpc.AddInterfaceEndpoint(
                "LogsEndpoint",
                new InterfaceVpcEndpointOptions
                {
                    Service = InterfaceVpcEndpointAwsService.CLOUDWATCH_LOGS,
                    SecurityGroups = new[] { vpcEndpointSg },
                    Subnets = privateSubnets,
                }
            );
            Vpc.AddInterfaceEndpoint(
                "SecretsManagerEndpoint",
                new InterfaceVpcEndpointOptions
                {
                    Service = InterfaceVpcEndpointAwsService.SECRETS_MANAGER,
                    SecurityGroups = new[] { vpcEndpointSg },
                    Subnets = privateSubnets,
                }
            );

            // Gateway Endpoint cho S3 — miễn phí, không cần Interface Endpoint
            Vpc.AddGatewayEndpoint(
                "S3Endpoint",
                new GatewayVpcEndpointOptions
                {
                    Service = GatewayVpcEndpointAwsService.S3,
                    Subnets = new[] { privateSubnets },
                }
            );
        }
    }
}
