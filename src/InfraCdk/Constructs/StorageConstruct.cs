using Amazon.CDK;
using Amazon.CDK.AWS.S3;
using Constructs;

namespace InfraCdk.Constructs
{
    /// <summary>
    /// Quản lý tất cả S3 Buckets: bucket lưu Access Logs của ALB
    /// và bucket chứa Static Assets của ứng dụng.
    /// </summary>
    public class StorageConstruct : Construct
    {
        public Bucket AlbLogBucket { get; }
        public Bucket StaticBucket { get; }

        public StorageConstruct(Construct scope, string id)
            : base(scope, id)
        {
            // --- ALB Access Log Bucket ---
            // Lưu trữ log truy cập ALB để phục vụ audit và debug
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
                }
            );

            // --- Static Assets Bucket ---
            // Lưu trữ tài nguyên tĩnh (images, CSS, JS) của ứng dụng
            // TODO: Bỏ BucketName cứng để tránh conflict khi deploy nhiều môi trường
            StaticBucket = new Bucket(
                this,
                "StaticBucket",
                new BucketProps
                {
                    BucketName = "my-static-resources-bucket",
                    Versioned = true,
                    RemovalPolicy = RemovalPolicy.DESTROY, // Chỉ dùng cho dev/test — đổi thành RETAIN cho production
                    Encryption = BucketEncryption.S3_MANAGED,
                    BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,
                    EnforceSSL = true,
                }
            );
        }
    }
}
