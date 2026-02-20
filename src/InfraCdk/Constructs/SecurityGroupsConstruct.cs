using Amazon.CDK.AWS.EC2;
using Constructs;

namespace InfraCdk.Constructs
{
    /// <summary>
    /// Tập trung toàn bộ Security Groups: ALB, ECS Fargate, và RDS.
    /// Giúp quản lý inbound/outbound rules ở một nơi duy nhất.
    /// </summary>
    public class SecurityGroupsConstruct : Construct
    {
        public SecurityGroup AlbSg { get; }
        public SecurityGroup EcsSg { get; }
        public SecurityGroup RdsSg { get; }

        public SecurityGroupsConstruct(Construct scope, string id, Vpc vpc)
            : base(scope, id)
        {
            // --- ALB Security Group ---
            // Nhận HTTP(80) và HTTPS(443) từ Internet
            AlbSg = new SecurityGroup(
                this,
                "ALBSecurityGroup",
                new SecurityGroupProps
                {
                    Vpc = vpc,
                    AllowAllOutbound = true,
                    Description = "Security group for Application Load Balancer",
                }
            );
            AlbSg.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(80), "Allow HTTP from anywhere");
            AlbSg.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(443), "Allow HTTPS from anywhere");

            // --- ECS Security Group ---
            // Chỉ nhận traffic từ ALB, không nhận từ Internet trực tiếp
            EcsSg = new SecurityGroup(
                this,
                "ECSSecurityGroup",
                new SecurityGroupProps
                {
                    Vpc = vpc,
                    AllowAllOutbound = true,
                    Description = "Security group for ECS Fargate tasks",
                }
            );
            EcsSg.AddIngressRule(AlbSg, Port.Tcp(80), "Allow HTTP from ALB only");

            // --- RDS Security Group ---
            // Chỉ nhận kết nối MySQL từ ECS Fargate tasks
            RdsSg = new SecurityGroup(
                this,
                "RDSSecurityGroup",
                new SecurityGroupProps
                {
                    Vpc = vpc,
                    AllowAllOutbound = true,
                    Description = "Security group for RDS Aurora cluster",
                }
            );
            RdsSg.AddIngressRule(EcsSg, Port.Tcp(3306), "Allow MySQL from ECS tasks only");
        }
    }
}
