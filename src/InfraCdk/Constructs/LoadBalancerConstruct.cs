using Amazon.CDK.AWS.CertificateManager;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ElasticLoadBalancingV2;
using Amazon.CDK.AWS.Route53;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.SecretsManager;
using Amazon.CDK.AWS.WAFv2;
using Constructs;

namespace InfraCdk.Constructs
{
    public class LoadBalancerConstructProps
    {
        public Vpc Vpc { get; set; }
        public ISubnet PublicSubnet1 { get; set; }
        public ISubnet PublicSubnet2 { get; set; }
        public SecurityGroup AlbSg { get; set; }
        public Bucket AlbLogBucket { get; set; }
        public ApplicationTargetGroup TargetGroup { get; set; }
        public IHostedZone HostedZone { get; set; }
        public string DomainName { get; set; }
    }

    /// <summary>
    /// Triển khai ALB, ACM Certificate, HTTP→HTTPS redirect listener,
    /// HTTPS listener với CloudFront header authentication, và WAF (REGIONAL).
    /// </summary>
    public class LoadBalancerConstruct : Construct
    {
        public ApplicationLoadBalancer Alb { get; }
        public Certificate Certificate { get; }

        // Header bí mật giữa CloudFront và ALB — ALB từ chối request không có header này
        public string CustomHeaderName { get; } = "X-Origin-Verify";

        // TODO (Security): UnsafeUnwrap() nhúng secret vào CloudFormation template dưới dạng plaintext.
        // Xem xét dùng giải pháp khác như CloudFront Origin Access Control thay vì custom header.
        public string CustomHeaderValue { get; }

        public LoadBalancerConstruct(Construct scope, string id, LoadBalancerConstructProps props)
            : base(scope, id)
        {
            // --- ACM Certificate (DNS validation qua Route53) ---
            Certificate = new Certificate(
                this,
                "SiteCertificate",
                new CertificateProps
                {
                    DomainName = props.DomainName,
                    SubjectAlternativeNames = new[] { $"www.{props.DomainName}" },
                    Validation = CertificateValidation.FromDns(props.HostedZone),
                }
            );

            // --- CloudFront → ALB Shared Secret ---
            var headerSecret = new Secret(
                this,
                "HeaderSecret",
                new SecretProps
                {
                    GenerateSecretString = new SecretStringGenerator
                    {
                        ExcludePunctuation = true,
                        IncludeSpace = false,
                        PasswordLength = 64,
                    },
                }
            );
            CustomHeaderValue = headerSecret.SecretValue.UnsafeUnwrap();

            // --- Application Load Balancer ---
            Alb = new ApplicationLoadBalancer(
                this,
                "MyALB",
                new ApplicationLoadBalancerProps
                {
                    Vpc = props.Vpc,
                    InternetFacing = true,
                    LoadBalancerName = "MyALB",
                    SecurityGroup = props.AlbSg,
                    VpcSubnets = new SubnetSelection
                    {
                        Subnets = new ISubnet[] { props.PublicSubnet1, props.PublicSubnet2 },
                    },
                }
            );

            Alb.LogAccessLogs(props.AlbLogBucket);

            // --- Listener 1: HTTP → HTTPS redirect ---
            Alb.AddListener(
                "HttpListener",
                new BaseApplicationListenerProps
                {
                    Port = 80,
                    Open = true,
                    DefaultAction = ListenerAction.Redirect(
                        new RedirectOptions
                        {
                            Protocol = "HTTPS",
                            Port = "443",
                            Permanent = true,
                        }
                    ),
                }
            );

            // --- Listener 2: HTTPS với CloudFront header check ---
            // Default action: 403 (từ chối request không đến từ CloudFront)
            var httpsListener = Alb.AddListener(
                "HttpsListener",
                new BaseApplicationListenerProps
                {
                    Port = 443,
                    Certificates = new[]
                    {
                        ListenerCertificate.FromCertificateManager(Certificate),
                    },
                    Open = true,
                    DefaultAction = ListenerAction.FixedResponse(
                        403,
                        new FixedResponseOptions
                        {
                            ContentType = "text/plain",
                            MessageBody = "Access Denied",
                        }
                    ),
                }
            );

            // Chỉ forward vào Target Group nếu có header bí mật hợp lệ từ CloudFront
            httpsListener.AddTargetGroups(
                "AppTarget",
                new AddApplicationTargetGroupsProps
                {
                    TargetGroups = new[] { props.TargetGroup },
                    Priority = 1,
                    Conditions = new[]
                    {
                        ListenerCondition.HttpHeader(CustomHeaderName, new[] { CustomHeaderValue }),
                    },
                }
            );

            // --- WAF — bảo vệ ALB khỏi tấn công web phổ biến ---
            var webAcl = new CfnWebACL(
                this,
                "WebACL",
                new CfnWebACLProps
                {
                    DefaultAction = new CfnWebACL.DefaultActionProperty
                    {
                        Allow = new CfnWebACL.AllowActionProperty(),
                    },
                    Scope = "REGIONAL",
                    VisibilityConfig = new CfnWebACL.VisibilityConfigProperty
                    {
                        SampledRequestsEnabled = true,
                        CloudWatchMetricsEnabled = true,
                        MetricName = "WebACLMetric",
                    },
                    Rules = new object[]
                    {
                        new CfnWebACL.RuleProperty
                        {
                            Name = "AWS-AWSManagedRulesCommonRuleSet",
                            Priority = 1,
                            OverrideAction = new CfnWebACL.OverrideActionProperty
                            {
                                None = new object(),
                            },
                            Statement = new CfnWebACL.StatementProperty
                            {
                                ManagedRuleGroupStatement =
                                    new CfnWebACL.ManagedRuleGroupStatementProperty
                                    {
                                        VendorName = "AWS",
                                        Name = "AWSManagedRulesCommonRuleSet",
                                    },
                            },
                            VisibilityConfig = new CfnWebACL.VisibilityConfigProperty
                            {
                                SampledRequestsEnabled = true,
                                CloudWatchMetricsEnabled = true,
                                MetricName = "AWSManagedRulesCommonRuleSetMetric",
                            },
                        },
                        // TODO: Thêm AWSManagedRulesKnownBadInputsRuleSet và AWSManagedRulesAmazonIpReputationList
                    },
                }
            );

            // Gắn WAF vào ALB
            new CfnWebACLAssociation(
                this,
                "WebACLAssociation",
                new CfnWebACLAssociationProps
                {
                    ResourceArn = Alb.LoadBalancerArn,
                    WebAclArn = webAcl.AttrArn,
                }
            );
        }
    }
}
