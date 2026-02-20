using Amazon.CDK;
using Amazon.CDK.AWS.S3;
using Constructs;

namespace InfraCdk.Constructs
{
    /// <summary>
    /// Quản lý tất cả S3 Buckets: bucket lưu Access Logs của ALB
    /// và bucket chứa Static Assets của ứng dụng, kèm Lifecycle Rules
    /// để tối ưu chi phí lưu trữ theo thời gian.
    /// </summary>
    public class StorageConstruct : Construct
    {
        public Bucket AlbLogBucket { get; }
        public Bucket StaticBucket { get; }

        public StorageConstruct(Construct scope, string id)
            : base(scope, id)
        {
            // ── ALB Access Log Bucket ──────────────────────────────────────────
            // Lifecycle: Log → S3-IA (30d) → Glacier Instant Retrieval (90d) → Xóa (365d)
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
                            // Bước 1: Sau 30 ngày → chuyển sang S3 Standard-IA (ít truy cập hơn)
                            Transitions = new[]
                            {
                                new Transition
                                {
                                    StorageClass = StorageClass.INFREQUENT_ACCESS,
                                    TransitionAfter = Duration.Days(30),
                                },
                                // Bước 2: Sau 90 ngày → chuyển sang Glacier Instant Retrieval (~80% rẻ hơn S3)
                                new Transition
                                {
                                    StorageClass = StorageClass.GLACIER_INSTANT_RETRIEVAL,
                                    TransitionAfter = Duration.Days(90),
                                },
                            },
                            // Bước 3: Sau 365 ngày → xóa hoàn toàn (log quá cũ không còn giá trị)
                            Expiration = Duration.Days(365),
                            // Dọn dẹp multipart upload bị treo sau 7 ngày
                            AbortIncompleteMultipartUploadAfter = Duration.Days(7),
                        },
                    },
                }
            );

            // ── Static Assets Bucket ──────────────────────────────────────────
            // Lifecycle: Versioning bật → quản lý non-current version để tránh tốn phí lưu trữ
            StaticBucket = new Bucket(
                this,
                "StaticBucket",
                new BucketProps
                {
                    BucketName = "my-static-resources-bucket",
                    Versioned = true,
                    RemovalPolicy = RemovalPolicy.DESTROY, // TODO: Đổi thành RETAIN cho production
                    Encryption = BucketEncryption.S3_MANAGED,
                    BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,
                    EnforceSSL = true,
                    LifecycleRules = new[]
                    {
                        new LifecycleRule
                        {
                            Id = "StaticAssetCurrentVersionLifecycle",
                            Enabled = true,
                            // Current version: chuyển sang S3-IA sau 90 ngày không truy cập
                            Transitions = new[]
                            {
                                new Transition
                                {
                                    StorageClass = StorageClass.INFREQUENT_ACCESS,
                                    TransitionAfter = Duration.Days(90),
                                },
                            },
                            // Dọn dẹp multipart upload bị treo sau 7 ngày
                            AbortIncompleteMultipartUploadAfter = Duration.Days(7),
                        },
                        new LifecycleRule
                        {
                            Id = "StaticAssetNonCurrentVersionLifecycle",
                            Enabled = true,
                            // Non-current version (version cũ sau khi bị ghi đè):
                            // Chuyển sang S3-IA sau 30 ngày
                            NoncurrentVersionTransitions = new[]
                            {
                                new NoncurrentVersionTransition
                                {
                                    StorageClass = StorageClass.INFREQUENT_ACCESS,
                                    TransitionAfter = Duration.Days(30),
                                },
                            },
                            // Xóa non-current version sau 90 ngày — không cần giữ lâu
                            NoncurrentVersionExpiration = Duration.Days(90),
                            // Chỉ giữ tối đa 3 version gần nhất, xóa version cũ hơn ngay lập tức
                            NoncurrentVersionsToRetain = 3,
                        },
                    },
                }
            );
        }
    }
}
