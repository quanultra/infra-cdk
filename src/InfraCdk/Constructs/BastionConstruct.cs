using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Constructs;

namespace InfraCdk.Constructs
{
    public class BastionConstructProps
    {
        public Vpc Vpc { get; set; }

        /// <summary>
        /// Đặt Bastion ở Public Subnet để SSM Agent có thể kết nối ra ngoài
        /// qua IGW mà không cần VPC Endpoint cho SSM (tiết kiệm chi phí).
        /// </summary>
        public ISubnet PublicSubnet { get; set; }
    }

    /// <summary>
    /// EC2 Bastion Host dùng để kết nối Aurora từ máy local qua SSM Port Forwarding.
    ///
    /// Cơ chế kết nối (không cần SSH, không cần mở port 22):
    ///   Local machine → AWS SSM → EC2 Bastion → RDS Proxy → Aurora
    ///
    /// Xem README.md phần "Kết nối DB từ máy local" để biết chi tiết lệnh.
    ///
    /// ⚠️ Chi phí: t3.micro ~$0.013/giờ. Hãy STOP instance khi không dùng:
    ///   aws ec2 stop-instances --instance-ids &lt;INSTANCE_ID&gt;
    /// </summary>
    public class BastionConstruct : Construct
    {
        public BastionHostLinux Host { get; }

        /// <summary>Security Group của Bastion — được dùng để mở ingress rule vào RDS SG.</summary>
        public ISecurityGroup SecurityGroup { get; }

        public BastionConstruct(Construct scope, string id, BastionConstructProps props)
            : base(scope, id)
        {
            // Security Group cho Bastion — không cần inbound rules vì SSM không dùng SSH
            SecurityGroup = new SecurityGroup(
                this,
                "BastionSG",
                new SecurityGroupProps
                {
                    Vpc = props.Vpc,
                    AllowAllOutbound = true, // SSM Agent cần outbound HTTPS để kết nối SSM endpoint
                    Description =
                        "Security Group for DB Bastion Host — SSM only, no inbound ports required",
                }
            );

            // BastionHostLinux: CDK tự tạo IAM Role với SSM permissions và cài SSM Agent
            Host = new BastionHostLinux(
                this,
                "BastionHost",
                new BastionHostLinuxProps
                {
                    Vpc = props.Vpc,
                    SubnetSelection = new SubnetSelection
                    {
                        Subnets = new ISubnet[] { props.PublicSubnet },
                    },
                    InstanceName = "DBBastionHost",
                    // t3.micro đủ dùng cho tunnel SSH — không cần instance mạnh
                    InstanceType = InstanceType.Of(InstanceClass.T3, InstanceSize.MICRO),
                    MachineImage = MachineImage.LatestAmazonLinux2023(),
                    SecurityGroup = SecurityGroup,
                    // Block device với gp3 và encrypt
                    BlockDevices = new[]
                    {
                        new BlockDevice
                        {
                            DeviceName = "/dev/xvda",
                            Volume = BlockDeviceVolume.Ebs(
                                20,
                                new EbsDeviceOptions
                                {
                                    VolumeType = EbsDeviceVolumeType.GP3,
                                    Encrypted = true,
                                    DeleteOnTermination = true,
                                }
                            ),
                        },
                    },
                }
            );

            // Output Instance ID để dễ dàng tìm khi chạy lệnh SSM
            new CfnOutput(
                this,
                "BastionInstanceId",
                new CfnOutputProps
                {
                    Value = Host.InstanceId,
                    Description =
                        "Bastion Host Instance ID — dùng trong lệnh: aws ssm start-session --target <ID>",
                    ExportName = "BastionInstanceId",
                }
            );
        }
    }
}
