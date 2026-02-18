using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Constructs;

namespace InfraCdk
{
    public class InfraCdkStack : Stack
    {
        internal InfraCdkStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
            // Tạo VPC không tạo subnet tự động
            var vpc = new Vpc(this, "MyVPC", new VpcProps
            {
                IpAddresses = IpAddresses.Cidr("10.0.0.0/16"),
                MaxAzs = 2,
                SubnetConfiguration = new SubnetConfiguration[] { }, // Không tạo subnet tự động
                NatGateways = 0 // Không tạo NAT Gateway tự động
            });

            // Tạo 2 public subnet thủ công trên 2 AZ
            var publicSubnet1 = new Subnet(this, "PublicSubnet1", new SubnetProps
            {
                VpcId = vpc.VpcId,
                AvailabilityZone = vpc.AvailabilityZones[0],
                CidrBlock = "10.0.1.0/24",
                MapPublicIpOnLaunch = true
            });
            var publicSubnet2 = new Subnet(this, "PublicSubnet2", new SubnetProps
            {
                VpcId = vpc.VpcId,
                AvailabilityZone = vpc.AvailabilityZones[1],
                CidrBlock = "10.0.2.0/24",
                MapPublicIpOnLaunch = true
            });

            // Tạo Internet Gateway thủ công
            var igw = new CfnInternetGateway(this, "MyIGW");
            new CfnVPCGatewayAttachment(this, "IGWAttachment", new CfnVPCGatewayAttachmentProps
            {
                VpcId = vpc.VpcId,
                InternetGatewayId = igw.Ref
            });

            // Tạo Route Table cho public subnet
            var publicRouteTable = new CfnRouteTable(this, "PublicRouteTable", new CfnRouteTableProps
            {
                VpcId = vpc.VpcId
            });
            // Route mặc định ra Internet
            new CfnRoute(this, "DefaultRouteToInternet", new CfnRouteProps
            {
                RouteTableId = publicRouteTable.Ref,
                DestinationCidrBlock = "0.0.0.0/0",
                GatewayId = igw.Ref
            });
            // Gắn route table vào từng public subnet
            new CfnSubnetRouteTableAssociation(this, "PublicSubnet1RouteTableAssoc", new CfnSubnetRouteTableAssociationProps
            {
                SubnetId = publicSubnet1.SubnetId,
                RouteTableId = publicRouteTable.Ref
            });
            new CfnSubnetRouteTableAssociation(this, "PublicSubnet2RouteTableAssoc", new CfnSubnetRouteTableAssociationProps
            {
                SubnetId = publicSubnet2.SubnetId,
                RouteTableId = publicRouteTable.Ref
            });

            // Tạo 2 private subnet thủ công trên 2 AZs.
            var privateSubnet1 = new Subnet(this, "PrivateSubnet1", new SubnetProps
            {
                VpcId = vpc.VpcId,
                AvailabilityZone = vpc.AvailabilityZones[0],
                CidrBlock = "10.0.11.0/24",
                MapPublicIpOnLaunch = false
            });
            var privateSubnet2 = new Subnet(this, "PrivateSubnet2", new SubnetProps
            {
                VpcId = vpc.VpcId,
                AvailabilityZone = vpc.AvailabilityZones[1],
                CidrBlock = "10.0.12.0/24",
                MapPublicIpOnLaunch = false
            });

            // Tạo Elastic IP cho Nat Gateway
            var natEip = new CfnEIP(this, "NatEIP", new CfnEIPProps
            {
                Domain = "vpc"
            });

            // Tạo Nat Gateway thủ công trong public subnet 1
            var natGateway = new CfnNatGateway(this, "NatGateway", new CfnNatGatewayProps
            {
                SubnetId = publicSubnet1.SubnetId,
                AllocationId = natEip.AttrAllocationId
            });

            // Route mặc định của private subnet ra Internet qua Nat Gateway
            var privateRouteTable = new CfnRouteTable(this, "PrivateRouteTable", new CfnRouteTableProps
            {
                VpcId = vpc.VpcId
            });
            new CfnRoute(this, "PrivateDefaultRouteToNatGateway", new CfnRouteProps
            {
                RouteTableId = privateRouteTable.Ref,
                DestinationCidrBlock = "0.0.0.0/0",
                NatGatewayId = natGateway.Ref
            });
            new CfnSubnetRouteTableAssociation(this, "PrivateSubnet1RouteTableAssoc", new CfnSubnetRouteTableAssociationProps
            {
                SubnetId = privateSubnet1.SubnetId,
                RouteTableId = privateRouteTable.Ref
            });
            new CfnSubnetRouteTableAssociation(this, "PrivateSubnet2RouteTableAssoc", new
            CfnSubnetRouteTableAssociationProps
            {
                SubnetId = privateSubnet2.SubnetId,
                RouteTableId = privateRouteTable.Ref
            });
        }
    }
}
