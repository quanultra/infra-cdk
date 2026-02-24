using Amazon.CDK.AWS.EC2;
using Constructs;
using InfraCdk;

namespace InfraCdk.Constructs
{
    /// <summary>
    /// Tập trung toàn bộ Security Groups: ALB, ECS Fargate, RDS.
    /// Tuân thủ nguyên tắc Least Privilege: mỗi SG chỉ mở đúng port cần thiết,
    /// cả INBOUND lẫn OUTBOUND.
    ///
    /// Luồng traffic được mô hình hóa:
    ///
    ///   Internet → [ALB:443/80] → [ECS:80] → [RDS:3306]
    ///                                ↓
    ///                          [VPC Endpoints:443]
    ///                     (ECR, CloudWatch, SecretsManager)
    /// </summary>
    public class SecurityGroupsConstruct : Construct
    {
        public SecurityGroup AlbSg { get; }
        public SecurityGroup EcsSg { get; }
        public SecurityGroup RdsSg { get; }

        public SecurityGroupsConstruct(
            Construct scope,
            string id,
            Vpc vpc,
            EnvironmentConfig envConfig
        )
            : base(scope, id)
        {
            // ─────────────────────────────────────────────────────────────────
            // ALB Security Group
            // Inbound:  Internet → 80 (HTTP redirect), 443 (HTTPS)
            // Outbound: ALB → ECS tasks trên port 80
            // ─────────────────────────────────────────────────────────────────
            AlbSg = new SecurityGroup(
                this,
                "ALBSecurityGroup",
                new SecurityGroupProps
                {
                    Vpc = vpc,
                    // #4: AllowAllOutbound = false — chỉ mở outbound về ECS port 80 (xem bên dưới)
                    AllowAllOutbound = false,
                    Description = "ALB: nhận HTTPS từ Internet, forward HTTP đến ECS",
                }
            );
            // Inbound
            AlbSg.AddIngressRule(
                Peer.AnyIpv4(),
                Port.Tcp(80),
                "Internet → ALB HTTP (sẽ redirect sang HTTPS)"
            );
            AlbSg.AddIngressRule(Peer.AnyIpv4(), Port.Tcp(443), "Internet → ALB HTTPS");

            // ─────────────────────────────────────────────────────────────────
            // ECS Security Group
            // Inbound:  ALB → ECS port 80
            // Outbound: ECS → RDS port 3306
            //           ECS → VPC CIDR port 443 (VPC Endpoints: ECR, Logs, SecretsManager)
            // ─────────────────────────────────────────────────────────────────
            EcsSg = new SecurityGroup(
                this,
                "ECSSecurityGroup",
                new SecurityGroupProps
                {
                    Vpc = vpc,
                    // #4: AllowAllOutbound = false — chỉ mở 3306 (RDS) và 443 (VPC Endpoints)
                    AllowAllOutbound = false,
                    Description = "ECS Fargate: nhận từ ALB, kết nối DB và VPC Endpoints",
                }
            );
            // Inbound
            EcsSg.AddIngressRule(AlbSg, Port.Tcp(80), "ALB → ECS HTTP");

            // ─────────────────────────────────────────────────────────────────
            // RDS Security Group
            // Inbound:  ECS → RDS port 3306
            //           Bastion → RDS port 3306 (được thêm trong InfraCdkStack)
            // Outbound: KHÔNG có — RDS không cần khởi tạo kết nối ra ngoài
            // ─────────────────────────────────────────────────────────────────
            RdsSg = new SecurityGroup(
                this,
                "RDSSecurityGroup",
                new SecurityGroupProps
                {
                    Vpc = vpc,
                    // #4: AllowAllOutbound = false — RDS không cần outbound gì cả
                    AllowAllOutbound = false,
                    Description = "RDS Aurora: chỉ nhận MySQL từ ECS và Bastion, không có outbound",
                }
            );
            // Inbound
            RdsSg.AddIngressRule(EcsSg, Port.Tcp(3306), "ECS → RDS MySQL (qua RDS Proxy)");

            // ─────────────────────────────────────────────────────────────────
            // #4: Egress rules — thêm SAU khi tất cả SG đã được tạo
            // Thứ tự quan trọng vì các rule tham chiếu lẫn nhau.
            // ─────────────────────────────────────────────────────────────────

            // ALB outbound → ECS port 80 (forward HTTP)
            AlbSg.AddEgressRule(EcsSg, Port.Tcp(80), "ALB → ECS HTTP forward");

            // ECS outbound → RDS port 3306 (MySQL qua RDS Proxy)
            EcsSg.AddEgressRule(RdsSg, Port.Tcp(3306), "ECS → RDS MySQL");

            if (envConfig.UseVpcEndpoints)
            {
                // VPC Endpoints mode (Stg/Prod): chỉ allow HTTPS đến VPC CIDR
                // — Least privilege: ECS chỉ reach được Interface Endpoints trong VPC
                EcsSg.AddEgressRule(
                    Peer.Ipv4(vpc.VpcCidrBlock),
                    Port.Tcp(443),
                    "ECS → VPC Endpoints (ECR, CloudWatch Logs, SecretsManager)"
                );
            }
            else
            {
                // NAT Gateway mode (Dev): allow HTTPS ra internet
                // — Traffic đi qua NAT GW để reach ECR/CloudWatch/SM trên public internet
                EcsSg.AddEgressRule(
                    Peer.AnyIpv4(),
                    Port.Tcp(443),
                    "ECS → Internet qua NAT GW (ECR, CloudWatch Logs, SecretsManager)"
                );
            }
        }
    }
}
