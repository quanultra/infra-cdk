using Amazon.CDK;

namespace InfraCdk
{
    sealed class Program
    {
        public static void Main(string[] args)
        {
            var app = new App();

            var account = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_ACCOUNT");
            var region = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_REGION");

            // ── WafStack (us-east-1) ───────────────────────────────────────────
            // CloudFront WAF BẮT BUỘC phải deploy tại us-east-1, bất kể main stack
            // deploy ở region nào. WafStack luôn được deploy tại us-east-1.
            var wafStack = new WafStack(
                app,
                "WafStack",
                new StackProps
                {
                    Env = new Amazon.CDK.Environment
                    {
                        Account = account,
                        Region = "us-east-1", // Cố định — không thay đổi
                    },
                    // Cho phép tham chiếu cross-region qua SSM Parameter Store
                    CrossRegionReferences = true,
                    Description = "WAF Stack (CLOUDFRONT scope) — phải ở us-east-1",
                }
            );

            // ── InfraCdkStack (main region) ────────────────────────────────────
            // Deploy ở region chính (lấy từ CDK_DEFAULT_REGION hoặc chỉ định tường minh).
            // Nhận WafArn từ WafStack qua CrossRegionReferences.
            new InfraCdkStack(
                app,
                "InfraCdkStack",
                new InfraCdkStackProps
                {
                    Env = new Amazon.CDK.Environment
                    {
                        Account = account,
                        Region = region, // VD: "ap-northeast-1"
                    },
                    // Nhận WAF ARN từ WafStack — CDK tự động tạo SSM Parameter để bridge cross-region
                    WafArn = wafStack.WebAclArn,
                    CrossRegionReferences = true,
                    Description = "Main infrastructure stack — ECS, ALB, Aurora, CloudFront",
                }
            );

            app.Synth();
        }
    }
}
