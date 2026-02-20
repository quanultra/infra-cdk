using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.Route53;
using Constructs;
using InfraCdk.Constructs;

namespace InfraCdk
{
    /// <summary>
    /// Props cho InfraCdkStack — mở rộng StackProps để nhận WAF ARN
    /// từ WafStack (deploy ở us-east-1) qua cơ chế CrossRegionReferences.
    /// </summary>
    public class InfraCdkStackProps : StackProps
    {
        /// <summary>
        /// ARN của CloudFront WAF từ WafStack.
        /// Được truyền vào đây qua CrossRegionReferences (CDK dùng SSM Parameter Store).
        /// </summary>
        public string WafArn { get; set; }
    }

    /// <summary>
    /// InfraCdkStack — Entry point của toàn bộ infrastructure.
    ///
    /// Kiến trúc tổng quan:
    ///   Internet → [WAF CloudFront Edge] → CloudFront → ALB (Public Subnet)
    ///           → ECS Fargate (Private Subnet) → Aurora (Private Subnet)
    ///
    /// Kết nối DB từ local:
    ///   Local machine → SSM → Bastion Host (Public Subnet) → RDS Proxy → Aurora
    ///
    /// Mỗi tầng được tách ra thành Construct riêng biệt để dễ bảo trì:
    ///   1. NetworkingConstruct    — VPC, Subnets, IGW, Route Tables, VPC Endpoints
    ///   2. SecurityGroupsConstruct — ALB / ECS / RDS Security Groups
    ///   3. StorageConstruct       — S3 Buckets (ALB logs, Static assets)
    ///   4. EcsConstruct           — ECS Cluster, Fargate Service, Target Group, Auto Scaling
    ///   5. DatabaseConstruct      — Aurora MySQL, RDS Proxy, Password Rotation
    ///   6. LoadBalancerConstruct  — ALB, ACM Certificate, Listeners
    ///   7. CloudFrontConstruct    — CloudFront Distribution + WAF (từ WafStack), Route53
    ///   8. BastionConstruct       — EC2 Bastion cho SSM Port Forwarding vào DB
    ///
    /// WAF được deploy riêng tại WafStack (us-east-1) vì CloudFront WAF
    /// bắt buộc phải ở us-east-1 bất kể main stack ở region nào.
    /// </summary>
    public class InfraCdkStack : Stack
    {
        internal InfraCdkStack(Construct scope, string id, InfraCdkStackProps props = null)
            : base(scope, id, props)
        {
            const string domainName = "example.com";

            // ── 1. Networking ─────────────────────────────────────────────────
            var networking = new NetworkingConstruct(this, "Networking");

            // ── 2. Security Groups ────────────────────────────────────────────
            var securityGroups = new SecurityGroupsConstruct(
                this,
                "SecurityGroups",
                networking.Vpc
            );

            // ── 3. Storage ────────────────────────────────────────────────────
            var storage = new StorageConstruct(this, "Storage");

            // ── 4. ECS (Cluster + Fargate Service + Target Group + Auto Scaling)
            var ecs = new EcsConstruct(
                this,
                "Ecs",
                new EcsConstructProps
                {
                    Vpc = networking.Vpc,
                    PrivateSubnet1 = networking.PrivateSubnet1,
                    PrivateSubnet2 = networking.PrivateSubnet2,
                    EcsSg = securityGroups.EcsSg,
                }
            );

            // ── 5. Database (Aurora + RDS Proxy) ──────────────────────────────
            var database = new DatabaseConstruct(
                this,
                "Database",
                new DatabaseConstructProps
                {
                    Vpc = networking.Vpc,
                    PrivateSubnet1 = networking.PrivateSubnet1,
                    PrivateSubnet2 = networking.PrivateSubnet2,
                    RdsSg = securityGroups.RdsSg,
                }
            );

            // ── 6. Route53 Hosted Zone ─────────────────────────────────────────
            var hostedZone = HostedZone.FromLookup(
                this,
                "HostedZone",
                new HostedZoneProviderProps { DomainName = domainName }
            );

            // ── 7. Load Balancer (ALB + Certificate + Listeners) ───────────────
            var loadBalancer = new LoadBalancerConstruct(
                this,
                "LoadBalancer",
                new LoadBalancerConstructProps
                {
                    Vpc = networking.Vpc,
                    PublicSubnet1 = networking.PublicSubnet1,
                    PublicSubnet2 = networking.PublicSubnet2,
                    AlbSg = securityGroups.AlbSg,
                    AlbLogBucket = storage.AlbLogBucket,
                    TargetGroup = ecs.TargetGroup,
                    HostedZone = hostedZone,
                    DomainName = domainName,
                }
            );

            // ── 8. CloudFront Distribution + WAF (từ WafStack us-east-1) ───────
            var cloudFront = new CloudFrontConstruct(
                this,
                "CloudFront",
                new CloudFrontConstructProps
                {
                    Alb = loadBalancer.Alb,
                    Certificate = loadBalancer.Certificate,
                    DomainName = domainName,
                    HostedZone = hostedZone,
                    CustomHeaderName = loadBalancer.CustomHeaderName,
                    CustomHeaderValue = loadBalancer.CustomHeaderValue,
                    // WAF ARN được truyền từ WafStack qua CrossRegionReferences
                    WafArn = props?.WafArn,
                }
            );

            // ── 9. Bastion Host (kết nối DB từ local qua SSM Port Forwarding) ───
            var bastion = new BastionConstruct(
                this,
                "Bastion",
                new BastionConstructProps
                {
                    Vpc = networking.Vpc,
                    PublicSubnet = networking.PublicSubnet1,
                }
            );

            // Cho phép Bastion kết nối vào RDS (qua RDS Proxy port 3306)
            securityGroups.RdsSg.AddIngressRule(
                bastion.SecurityGroup,
                Port.Tcp(3306),
                "Allow MySQL from Bastion Host (SSM Port Forwarding)"
            );

            // Ngăn compiler cảnh báo unused variable
            _ = database;
            _ = cloudFront;
            _ = bastion;
        }
    }
}
