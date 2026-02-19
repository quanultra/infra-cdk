using System.Linq;
using Amazon.CDK;
using Amazon.CDK.AWS.ApplicationAutoScaling;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using Constructs;

namespace InfraCdk
{
    public class InfraCdkStack : Stack
    {
        internal InfraCdkStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
            // Tạo VPC (Virtual Private Cloud) với CIDR 10.0.0.0/16, không tạo subnet tự động, không tạo NAT Gateway tự động
            var vpc = new Vpc(this, "MyVPC", new VpcProps
            {
                IpAddresses = IpAddresses.Cidr("10.0.0.0/16"),
                MaxAzs = 2,
                SubnetConfiguration = new SubnetConfiguration[] { }, // Không tạo subnet tự động
                NatGateways = 0 // Không tạo NAT Gateway tự động
            });

            // Tạo 2 public subnet thủ công trên 2 AZ (mỗi subnet ở một Availability Zone, cho phép gán public IP khi launch EC2)
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

            // Tạo Internet Gateway (IGW) thủ công và gắn vào VPC để các public subnet có thể truy cập Internet
            var igw = new CfnInternetGateway(this, "MyIGW");
            new CfnVPCGatewayAttachment(this, "IGWAttachment", new CfnVPCGatewayAttachmentProps
            {
                VpcId = vpc.VpcId,
                InternetGatewayId = igw.Ref
            });

            // Tạo Route Table cho public subnet, thêm route mặc định ra Internet và gắn vào từng public subnet
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

            // Tạo 2 private subnet thủ công trên 2 AZs (mỗi subnet ở một Availability Zone, không gán public IP)
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

            // Tạo Elastic IP (EIP) để gán cho NAT Gateway (giúp private subnet truy cập Internet)
            var natEip = new CfnEIP(this, "NatEIP", new CfnEIPProps
            {
                Domain = "vpc"
            });

            // Tạo NAT Gateway thủ công trong public subnet 1, dùng EIP ở trên
            var natGateway = new CfnNatGateway(this, "NatGateway", new CfnNatGatewayProps
            {
                SubnetId = publicSubnet1.SubnetId,
                AllocationId = natEip.AttrAllocationId
            });

            // Tạo Route Table cho private subnet, thêm route mặc định ra Internet qua NAT Gateway và gắn vào từng private subnet
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

            // Tạo Security Group cho Application Load Balancer (ALB), cho phép HTTP từ mọi nơi và outbound HTTPS
            var albSecurityGroup = new SecurityGroup(this, "ALBSecurityGroup", new SecurityGroupProps
            {
                Vpc = vpc,
                AllowAllOutbound = true,
                Description = "Security group for Application Load Balancer"
            });
            albSecurityGroup.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(80), "Allow HTTP traffic from anywhere");
            albSecurityGroup.AddEgressRule(Peer.AnyIpv4(), Port.Tcp(443), "Allow HTTPS traffic to anywhere");

            // Tạo Application Load Balancer (ALB) trong public subnet, gắn security group ở trên, tạo listener HTTP
            var alb = new ApplicationLoadBalancer(this, "MyALB", new ApplicationLoadBalancerProps
            {
                Vpc = vpc,
                InternetFacing = true,
                LoadBalancerName = "MyALB",
                SecurityGroup = albSecurityGroup,
                VpcSubnets = new SubnetSelection
                {
                    Subnets = new ISubnet[] { publicSubnet1, publicSubnet2 }
                }
            });
            // Tạo listener cho ALB
            var listener = alb.AddListener("Listener", new BaseApplicationListenerProps
            {
                Port = 80,
                Open = true
            });

            // Tạo Security Group cho ECS Fargate, chỉ cho phép nhận HTTP từ ALB
            var ecsSecurityGroup = new SecurityGroup(this, "ECSSecurityGroup", new SecurityGroupProps
            {
                Vpc = vpc,
                AllowAllOutbound = true,
                Description = "Security group for ECS Fargate tasks"
            });
            ecsSecurityGroup.AddIngressRule(albSecurityGroup, Port.Tcp(80), "Allow HTTP traffic from ALB");

            // Tạo Security Group cho RDS, chỉ cho phép nhận MySQL traffic từ ECS Fargate
            var rdsSecurityGroup = new SecurityGroup(this, "RDSSecurityGroup", new SecurityGroupProps
            {
                Vpc = vpc,
                AllowAllOutbound = true,
                Description = "Security group for RDS instance"
            });
            rdsSecurityGroup.AddIngressRule(ecsSecurityGroup, Port.Tcp(3306), "Allow MySQL traffic from ECS tasks");

            // Tạo ECS Cluster để quản lý các Fargate Service, gắn vào VPC
            var ecsCluster = new Cluster(this, "ECSCluster", new ClusterProps
            {
                Vpc = vpc,
                ClusterName = "ECSCluster"
            });

            // Định nghĩa Task cho Fargate Service (chỉ định CPU, RAM, image, port mapping cho container)
            var taskDefinition = new FargateTaskDefinition(this, "FargateTaskDef", new FargateTaskDefinitionProps
            {
                Cpu = 256,
                MemoryLimitMiB = 512
            });
            taskDefinition.AddContainer("AppContainer", new ContainerDefinitionOptions
            {
                Image = ContainerImage.FromRegistry("amazon/amazon-ecs-sample"),
                PortMappings = new[] { new PortMapping { ContainerPort = 80 } }
            });

            // Tạo Fargate Service chạy trong private subnet, gắn security group, chỉ định số lượng task mong muốn
            var fargateService = new FargateService(this, "FargateService", new FargateServiceProps
            {
                Cluster = ecsCluster,
                ServiceName = "MyFargateService",
                TaskDefinition = taskDefinition,
                AssignPublicIp = false,
                DesiredCount = 2,
                SecurityGroups = new[] { ecsSecurityGroup },
                VpcSubnets = new SubnetSelection
                {
                    Subnets = new ISubnet[] { privateSubnet1, privateSubnet2 }
                }
            });

            // Tạo Target Group cho ALB, gắn Fargate Service vào target group để nhận traffic từ ALB
            var targetGroup = new ApplicationTargetGroup(this, "FargateTargetGroup", new ApplicationTargetGroupProps
            {
                Vpc = vpc,
                Port = 80,
                Protocol = ApplicationProtocol.HTTP,
                TargetType = TargetType.IP
            });

            fargateService.AttachToApplicationTargetGroup(targetGroup);
            listener.AddTargetGroups("DefaultTargetGroup", new AddApplicationTargetGroupsProps
            {
                TargetGroups = new[] { targetGroup }
            });

            // Thiết lập Auto Scaling cho Fargate Service dựa trên CPU utilization (tự động scale in/out task)
            var scaling = fargateService.AutoScaleTaskCount(new EnableScalingProps
            {
                MinCapacity = 2,
                MaxCapacity = 8
            });
            scaling.ScaleOnCpuUtilization("CpuScaling", new CpuUtilizationScalingProps
            {
                TargetUtilizationPercent = 50,
                ScaleInCooldown = Duration.Seconds(60),
                ScaleOutCooldown = Duration.Seconds(60)
            });
        }
    }
}
