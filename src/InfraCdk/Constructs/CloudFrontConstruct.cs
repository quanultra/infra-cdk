using System.Collections.Generic;
using Amazon.CDK.AWS.CertificateManager;
using Amazon.CDK.AWS.CloudFront;
using Amazon.CDK.AWS.CloudFront.Origins;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using Amazon.CDK.AWS.Route53;
using Amazon.CDK.AWS.Route53.Targets;
using Amazon.CDK.AWS.S3;
using Constructs;

namespace InfraCdk.Constructs
{
    public class CloudFrontConstructProps
    {
        public ApplicationLoadBalancer Alb { get; set; }
        public ICertificate Certificate { get; set; }
        public string DomainName { get; set; }
        public IHostedZone HostedZone { get; set; }
        public string CustomHeaderName { get; set; }
        public string CustomHeaderValue { get; set; }

        /// <summary>
        /// ARN của CloudFront WAF (WafStack.WebAclArn).
        /// WAF phải có Scope = "CLOUDFRONT" và được deploy tại us-east-1.
        /// </summary>
        public string WafArn { get; set; }

        /// <summary>
        /// S3 Bucket chứa static assets (CSS, JS, images...).
        /// CloudFront tạo behavior riêng cho path /static/* trỏ về bucket này.
        /// </summary>
        public IBucket StaticBucket { get; set; }
    }

    /// <summary>
    /// Triển khai CloudFront Distribution phía trước ALB, gắn WAF (từ WafStack),
    /// và tạo Route53 A Record cho apex domain và www subdomain.
    /// WAF được lọc tại CloudFront edge trước khi traffic vào VPC.
    /// </summary>
    public class CloudFrontConstruct : Construct
    {
        public Distribution Distribution { get; }

        public CloudFrontConstruct(Construct scope, string id, CloudFrontConstructProps props)
            : base(scope, id)
        {
            // --- CloudFront Distribution ---
            // Origin là ALB HTTPS endpoint, gắn header bí mật để ALB xác thực
            var albOrigin = new HttpOrigin(
                props.Alb.LoadBalancerDnsName,
                new HttpOriginProps
                {
                    ProtocolPolicy = OriginProtocolPolicy.HTTPS_ONLY,
                    CustomHeaders = new Dictionary<string, string>
                    {
                        { props.CustomHeaderName, props.CustomHeaderValue },
                    },
                }
            );

            // #10: S3 origin cho static assets — S3BucketOrigin tự tạo OAC (thay S3Origin obsolete)
            // S3BucketOrigin dùng Origin Access Control (OAC) thay vì OAI (deprecated)
            var s3Origin = S3BucketOrigin.WithOriginAccessControl(props.StaticBucket);

            Distribution = new Distribution(
                this,
                "SiteDistribution",
                new DistributionProps
                {
                    DefaultBehavior = new BehaviorOptions
                    {
                        Origin = albOrigin,
                        ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
                        AllowedMethods = AllowedMethods.ALLOW_ALL,
                        Compress = true,
                        // #10: Explicit CACHING_DISABLED cho dynamic API content.
                        // Nếu không set, CloudFront có thể cache API response sai.
                        CachePolicy = CachePolicy.CACHING_DISABLED,
                        // Forward tất cả headers/cookies/query strings về ALB để app xử lý.
                        OriginRequestPolicy = OriginRequestPolicy.ALL_VIEWER,
                    },
                    // #10: Behavior riêng cho /static/* — serve từ S3, cache dài hạn.
                    // Không tốn tài nguyên ECS cho static assets.
                    AdditionalBehaviors = new Dictionary<string, IBehaviorOptions>
                    {
                        {
                            "/static/*",
                            new BehaviorOptions
                            {
                                Origin = s3Origin,
                                ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
                                AllowedMethods = AllowedMethods.ALLOW_GET_HEAD,
                                CachePolicy = CachePolicy.CACHING_OPTIMIZED,
                                Compress = true,
                            }
                        },
                    },
                    DomainNames = new[] { props.DomainName, $"www.{props.DomainName}" },
                    Certificate = props.Certificate,
                    // WAF được lọc tại CloudFront edge (trước khi vào VPC)
                    WebAclId = props.WafArn,
                }
            );

            // --- Route53: A Record trỏ domain về CloudFront ---
            new ARecord(
                this,
                "AliasRecordCF",
                new ARecordProps
                {
                    Zone = props.HostedZone,
                    Target = RecordTarget.FromAlias(new CloudFrontTarget(Distribution)),
                    RecordName = props.DomainName,
                }
            );

            // --- Route53: www subdomain cũng trỏ về CloudFront ---
            new ARecord(
                this,
                "WwwAliasRecordCF",
                new ARecordProps
                {
                    Zone = props.HostedZone,
                    Target = RecordTarget.FromAlias(new CloudFrontTarget(Distribution)),
                    RecordName = $"www.{props.DomainName}",
                }
            );
        }
    }
}
