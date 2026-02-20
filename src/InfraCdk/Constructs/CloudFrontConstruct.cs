using System.Collections.Generic;
using Amazon.CDK.AWS.CertificateManager;
using Amazon.CDK.AWS.CloudFront;
using Amazon.CDK.AWS.CloudFront.Origins;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using Amazon.CDK.AWS.Route53;
using Amazon.CDK.AWS.Route53.Targets;
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
    }

    /// <summary>
    /// Triển khai CloudFront Distribution phía trước ALB.
    /// CloudFront gắn custom header bí mật vào mọi request để ALB
    /// có thể xác minh traffic đến từ CloudFront, không phải từ Internet trực tiếp.
    /// Tạo Route53 A Record trỏ domain về CloudFront.
    /// </summary>
    public class CloudFrontConstruct : Construct
    {
        public Distribution Distribution { get; }

        public CloudFrontConstruct(Construct scope, string id, CloudFrontConstructProps props)
            : base(scope, id)
        {
            // --- CloudFront Distribution ---
            // Origin là ALB HTTPS endpoint, gắn header bí mật để ALB xác thực
            Distribution = new Distribution(
                this,
                "SiteDistribution",
                new DistributionProps
                {
                    DefaultBehavior = new BehaviorOptions
                    {
                        Origin = new HttpOrigin(
                            props.Alb.LoadBalancerDnsName,
                            new HttpOriginProps
                            {
                                ProtocolPolicy = OriginProtocolPolicy.HTTPS_ONLY,
                                CustomHeaders = new Dictionary<string, string>
                                {
                                    { props.CustomHeaderName, props.CustomHeaderValue },
                                },
                            }
                        ),
                        ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
                        AllowedMethods = AllowedMethods.ALLOW_ALL,
                        Compress = true,
                    },
                    DomainNames = new[] { props.DomainName, $"www.{props.DomainName}" },
                    Certificate = props.Certificate,
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
