using Amazon.CDK;
using Amazon.CDK.AWS.WAFv2;
using Constructs;

namespace InfraCdk
{
    /// <summary>
    /// Stack riêng chứa WAF cho CloudFront.
    ///
    /// ⚠️ QUAN TRỌNG: CloudFront WAF (Scope = "CLOUDFRONT") BẮT BUỘC phải deploy
    /// tại region us-east-1, bất kể main stack deploy ở region nào.
    ///
    /// Stack này export WebAclArn để InfraCdkStack nhận qua CrossRegionReferences.
    /// CDK sẽ tự động dùng SSM Parameter Store để truyền ARN giữa các region.
    /// </summary>
    public class WafStack : Stack
    {
        /// <summary>ARN của CloudFront WAF — truyền sang InfraCdkStack để gắn vào Distribution.</summary>
        public string WebAclArn { get; }

        internal WafStack(Construct scope, string id, IStackProps props = null)
            : base(scope, id, props)
        {
            var webAcl = new CfnWebACL(
                this,
                "CloudFrontWebACL",
                new CfnWebACLProps
                {
                    DefaultAction = new CfnWebACL.DefaultActionProperty
                    {
                        Allow = new CfnWebACL.AllowActionProperty(),
                    },
                    // CLOUDFRONT scope — chỉ hoạt động khi stack deploy tại us-east-1
                    Scope = "CLOUDFRONT",
                    VisibilityConfig = new CfnWebACL.VisibilityConfigProperty
                    {
                        SampledRequestsEnabled = true,
                        CloudWatchMetricsEnabled = true,
                        MetricName = "CloudFrontWebACLMetric",
                    },
                    Rules = new object[]
                    {
                        // Rule 1: Chặn các pattern tấn công web phổ biến (XSS, path traversal, ...)
                        new CfnWebACL.RuleProperty
                        {
                            Name = "AWSManagedRulesCommonRuleSet",
                            Priority = 1,
                            OverrideAction = new CfnWebACL.OverrideActionProperty
                            {
                                None = new object(), // Dùng action mặc định của rule group (Block)
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
                                MetricName = "CommonRuleSetMetric",
                            },
                        },
                        // Rule 2: Chặn IP có tiếng xấu (bots, scanners, TOR exit nodes)
                        new CfnWebACL.RuleProperty
                        {
                            Name = "AWSManagedRulesAmazonIpReputationList",
                            Priority = 2,
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
                                        Name = "AWSManagedRulesAmazonIpReputationList",
                                    },
                            },
                            VisibilityConfig = new CfnWebACL.VisibilityConfigProperty
                            {
                                SampledRequestsEnabled = true,
                                CloudWatchMetricsEnabled = true,
                                MetricName = "IpReputationMetric",
                            },
                        },
                        // Rule 3: Chặn các input nguy hiểm (Log4Shell, SSRF, ...)
                        new CfnWebACL.RuleProperty
                        {
                            Name = "AWSManagedRulesKnownBadInputsRuleSet",
                            Priority = 3,
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
                                        Name = "AWSManagedRulesKnownBadInputsRuleSet",
                                    },
                            },
                            VisibilityConfig = new CfnWebACL.VisibilityConfigProperty
                            {
                                SampledRequestsEnabled = true,
                                CloudWatchMetricsEnabled = true,
                                MetricName = "KnownBadInputsMetric",
                            },
                        },
                    },
                }
            );

            WebAclArn = webAcl.AttrArn;

            // Export ARN để tham chiếu từ InfraCdkStack (cross-region qua SSM)
            new CfnOutput(
                this,
                "WebAclArn",
                new CfnOutputProps
                {
                    Value = WebAclArn,
                    Description = "ARN của CloudFront WAF — dùng trong InfraCdkStack",
                    ExportName = "CloudFrontWebAclArn",
                }
            );
        }
    }
}
