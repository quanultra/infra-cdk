using Amazon.CDK;
using Amazon.CDK.AWS.Route53;
using Constructs;
using InfraCdk.Constructs;

namespace InfraCdk
{
    /// <summary>
    /// InfraCdkStack — Entry point của toàn bộ infrastructure.
    ///
    /// Kiến trúc tổng quan:
    ///   Internet → CloudFront → ALB (Public Subnet) → ECS Fargate (Private Subnet) → Aurora (Private Subnet)
    ///
    /// Mỗi tầng được tách ra thành Construct riêng biệt để dễ bảo trì:
    ///   1. NetworkingConstruct   — VPC, Subnets, IGW, Route Tables, VPC Endpoints
    ///   2. SecurityGroupsConstruct — ALB / ECS / RDS Security Groups
    ///   3. StorageConstruct      — S3 Buckets (ALB logs, Static assets)
    ///   4. EcsConstruct          — ECS Cluster, Fargate Service, Target Group, Auto Scaling
    ///   5. DatabaseConstruct     — Aurora MySQL, RDS Proxy, Password Rotation
    ///   6. LoadBalancerConstruct — ALB, ACM Certificate, Listeners, WAF
    ///   7. CloudFrontConstruct   — CloudFront Distribution, Route53 Records
    /// </summary>
    public class InfraCdkStack : Stack
    {
        internal InfraCdkStack(Construct scope, string id, IStackProps props = null)
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
            // Lookup HostedZone từ Route53 — yêu cầu stack có Env (Account + Region)
            var hostedZone = HostedZone.FromLookup(
                this,
                "HostedZone",
                new HostedZoneProviderProps { DomainName = domainName }
            );

            // ── 7. Load Balancer (ALB + Certificate + WAF + Listeners) ─────────
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

            // ── 8. CloudFront Distribution ─────────────────────────────────────
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
                }
            );

            // Ngăn compiler cảnh báo unused variable
            _ = database;
            _ = cloudFront;
        }
    }
}
