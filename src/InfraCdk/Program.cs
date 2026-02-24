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

            // ── Tạo EnvironmentConfig từ context ──────────────────────────────
            // Đọc context "environment" để chọn config preset:
            //   "development" (mặc định) → EnvironmentConfig.Development()
            //   "staging"               → EnvironmentConfig.Staging()
            //   "production"            → EnvironmentConfig.Production()
            //
            // Cach set:
            //   cdk.json:  "environment": "staging"
            //   CLI:       cdk deploy --context environment=production
            var envName = app.Node.TryGetContext("environment") as string;
            var envConfig = EnvironmentConfig.FromName(envName);

            // Stack names bao gồm suffix môi trường để deploy nhiều env vào cùng account:
            //   Dev:  Dev-WafStack  /  Dev-InfraCdkStack
            //   Stg:  Stg-WafStack  /  Stg-InfraCdkStack
            //   Prod: Prod-WafStack /  Prod-InfraCdkStack
            var wafStackName = $"{envConfig.Suffix}-WafStack";
            var infraStackName = $"{envConfig.Suffix}-InfraCdkStack";

            // ── WafStack (us-east-1) ───────────────────────────────────────────
            // CloudFront WAF BẮT BUỘC phải deploy tại us-east-1, bất kể main stack
            // deploy ở region nào. WafStack luôn được deploy tại us-east-1.
            var wafStack = new WafStack(
                app,
                wafStackName,
                new StackProps
                {
                    Env = new Amazon.CDK.Environment
                    {
                        Account = account,
                        Region = "us-east-1", // Cố định — không thay đổi
                    },
                    // Cho phép tham chiếu cross-region qua SSM Parameter Store
                    CrossRegionReferences = true,
                    Description =
                        $"[{envConfig.Name}] WAF Stack (CLOUDFRONT scope) — phải ở us-east-1",
                }
            );

            // ── InfraCdkStack (main region) ────────────────────────────────────
            // Deploy ở region chính (lấy từ CDK_DEFAULT_REGION hoặc chỉ định tường minh).
            // Nhận WafArn từ WafStack qua CrossRegionReferences.
            // Nhận EnvConfig để cấu hình mọi environment-specific behavior.
            new InfraCdkStack(
                app,
                infraStackName,
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
                    Description =
                        $"[{envConfig.Name}] Main infrastructure stack — ECS, ALB, Aurora, CloudFront",
                    // Truyền EnvironmentConfig — chứa toàn bộ settings khác nhau theo env
                    EnvConfig = envConfig,
                }
            );

            app.Synth();
        }
    }
}
