using Amazon.CDK;
using Amazon.CDK.AWS.S3;
using Constructs;

namespace InfraCdk.Constructs
{
    public class StorageConstructProps
    {
        /// <summary>
        /// Cấu hình theo environment — xác định RemovalPolicy và AutoDelete cho S3 bucket.
        /// Được tạo từ EnvironmentConfig.FromName() trong InfraCdkStack.
        /// </summary>
        public EnvironmentConfig EnvConfig { get; set; }

        /// <summary>
        /// Tên cụ thể cho S3 Static Bucket. Nếu null/trống, CDK tự sinh tên unique (an toàn hơn).
        /// ⚠̃  Bucket name phải unique toàn cầu — chỉ nên set nếu cần tên cố định.
        /// Set qua cdk.json: "staticBucketName": "my-app-static-prod"
        /// hoặc CLI:        cdk deploy --context staticBucketName=my-app-static-prod
        /// </summary>
        public string StaticBucketName { get; set; } = null;
    }

    /// <summary>
    /// Quản lý tất cả S3 Buckets: bucket lưu Access Logs của ALB
    /// và bucket chứa Static Assets của ứng dụng, kèm Lifecycle Rules
    /// để tối ưu chi phí lưu trữ theo thời gian.
    ///
    /// RemovalPolicy:
    ///   ALB Log Bucket  → luôn DESTROY (log không có giá trị lâu dài)
    ///   Static Bucket   → production: RETAIN | dev: DESTROY
    /// </summary>
    public class StorageConstruct : Construct
    {
        public Bucket AlbLogBucket { get; }
        public Bucket StaticBucket { get; }

        public StorageConstruct(Construct scope, string id, StorageConstructProps props = null)
            : base(scope, id)
        {
            props ??= new StorageConstructProps();

            // ── ALB Access Log Bucket ──────────────────────────────────────────
            // Log không có giá trị lâu dài → luôn DESTROY để không tạo orphan bucket
            AlbLogBucket = new Bucket(
                this,
                "ALBLogBucket",
                new BucketProps
                {
                    RemovalPolicy = RemovalPolicy.DESTROY,
                    AutoDeleteObjects = true,
                    Encryption = BucketEncryption.S3_MANAGED,
                    BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,
                    EnforceSSL = true,
                    LifecycleRules = new[]
                    {
                        new LifecycleRule
                        {
                            Id = "ALBLogLifecycle",
                            Enabled = true,
                            Transitions = new[]
                            {
                                // 30d → S3 Standard-IA
                                new Transition
                                {
                                    StorageClass = StorageClass.INFREQUENT_ACCESS,
                                    TransitionAfter = Duration.Days(30),
                                },
                                // 90d → Glacier Instant Retrieval (~80% rẻ hơn S3 Standard)
                                new Transition
                                {
                                    StorageClass = StorageClass.GLACIER_INSTANT_RETRIEVAL,
                                    TransitionAfter = Duration.Days(90),
                                },
                            },
                            Expiration = Duration.Days(365), // 1 năm → xóa
                            AbortIncompleteMultipartUploadAfter = Duration.Days(7),
                        },
                    },
                }
            );

            // ── Static Assets Bucket ──────────────────────────────────────────
            // Dev:        DESTROY + AutoDelete — xóa sạch khi cdk destroy
            // Staging:    RETAIN  + no AutoDelete — giữ bucket cho debugging
            // Production: RETAIN  + no AutoDelete — KHÔNG BAO GIỜ xóa nhầm assets
            StaticBucket = new Bucket(
                this,
                "StaticBucket",
                new BucketProps
                {
                    // StaticBucketName từ CDK context: null → CDK sinh tên unique (an toàn hơn)
                    BucketName = string.IsNullOrWhiteSpace(props.StaticBucketName)
                        ? null
                        : props.StaticBucketName,
                    Versioned = true,
                    Encryption = BucketEncryption.S3_MANAGED,
                    BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,
                    EnforceSSL = true,
                    RemovalPolicy = props.EnvConfig.StaticBucketRemovalPolicy,
                    AutoDeleteObjects = props.EnvConfig.StaticBucketAutoDelete,
                    LifecycleRules = new[]
                    {
                        // Current version: sau 90 ngày → S3-IA
                        new LifecycleRule
                        {
                            Id = "StaticAssetCurrentVersionLifecycle",
                            Enabled = true,
                            Transitions = new[]
                            {
                                new Transition
                                {
                                    StorageClass = StorageClass.INFREQUENT_ACCESS,
                                    TransitionAfter = Duration.Days(90),
                                },
                            },
                            AbortIncompleteMultipartUploadAfter = Duration.Days(7),
                        },
                        // Non-current (old) version: giữ 3 bản gần nhất, xóa sau 90 ngày
                        new LifecycleRule
                        {
                            Id = "StaticAssetNonCurrentVersionLifecycle",
                            Enabled = true,
                            NoncurrentVersionTransitions = new[]
                            {
                                new NoncurrentVersionTransition
                                {
                                    StorageClass = StorageClass.INFREQUENT_ACCESS,
                                    TransitionAfter = Duration.Days(30),
                                },
                            },
                            NoncurrentVersionExpiration = Duration.Days(90),
                            NoncurrentVersionsToRetain = 3,
                        },
                    },
                }
            );
        }
    }
}
