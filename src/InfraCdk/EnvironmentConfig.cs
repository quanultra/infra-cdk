using Amazon.CDK;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.Logs;

namespace InfraCdk
{
    /// <summary>
    /// Tập trung toàn bộ cấu hình thay đổi theo environment vào một class duy nhất.
    /// Mỗi environment có một preset riêng, được chọn tự động từ CDK context key "environment".
    ///
    /// Cách sử dụng trong cdk.json:
    ///   "environment": "development"   → EnvironmentConfig.Development()
    ///   "environment": "staging"       → EnvironmentConfig.Staging()
    ///   "environment": "production"    → EnvironmentConfig.Production()
    ///
    /// Hoặc override qua CLI:
    ///   cdk deploy --context environment=production
    ///
    /// Stack names tự động thêm suffix theo environment:
    ///   Dev-WafStack / Dev-InfraCdkStack
    ///   Stg-WafStack / Stg-InfraCdkStack
    ///   Prod-WafStack / Prod-InfraCdkStack
    /// </summary>
    public record EnvironmentConfig
    {
        // ── Identity ─────────────────────────────────────────────────────────
        /// <summary>Tên đầy đủ của environment ("development", "staging", "production")</summary>
        public string Name { get; init; }

        /// <summary>Suffix ngắn dùng để đặt tên stack ("Dev", "Stg", "Prod")</summary>
        public string Suffix { get; init; }

        /// <summary>true nếu đây là production — ảnh hưởng đến nhiều behavior bảo vệ dữ liệu</summary>
        public bool IsProduction { get; init; }

        // ── Aurora ───────────────────────────────────────────────────────────
        /// <summary>Instance class cho Aurora Writer + Reader. Dev dùng SMALL để tiết kiệm chi phí.</summary>
        public InstanceClass AuroraInstanceClass { get; init; }

        /// <summary>Instance size cho Aurora Writer + Reader.</summary>
        public InstanceSize AuroraInstanceSize { get; init; }

        /// <summary>
        /// DESTROY: xóa sạch khi cdk destroy (dev) |
        /// SNAPSHOT: tạo final snapshot trước khi xóa (stg, prod)
        /// </summary>
        public RemovalPolicy DbRemovalPolicy { get; init; }

        // ── S3 ───────────────────────────────────────────────────────────────
        /// <summary>
        /// DESTROY: xóa bucket + tất cả files (dev) |
        /// RETAIN: giữ lại bucket sau khi stack bị xóa (stg, prod)
        /// </summary>
        public RemovalPolicy StaticBucketRemovalPolicy { get; init; }

        /// <summary>true = CDK tạo Lambda để xóa hết object trước khi xóa bucket (chỉ dùng với DESTROY)</summary>
        public bool StaticBucketAutoDelete { get; init; }

        // ── ALB ──────────────────────────────────────────────────────────────
        /// <summary>
        /// true = production: ngăn xóa ALB qua Console/CLI/cdk destroy |
        /// false = dev/stg: cho phép cdk destroy hoạt động bình thường
        /// </summary>
        public bool AlbDeletionProtection { get; init; }

        // ── ECS Scaling ──────────────────────────────────────────────────────
        /// <summary>Số task mặc định khi deploy. Dev=1, Stg/Prod=2</summary>
        public int EcsDesiredCount { get; init; }

        /// <summary>Minimum tasks khi đang hoạt động ban ngày</summary>
        public int EcsMinCapacity { get; init; }

        /// <summary>Maximum tasks khi traffic peak. Dev/Stg=4, Prod=8</summary>
        public int EcsMaxCapacity { get; init; }

        /// <summary>Minimum tasks ban đêm (scheduled scale-down). Dev/Stg=0, Prod=1</summary>
        public int EcsNightMinCapacity { get; init; }

        /// <summary>Maximum tasks ban đêm. Dev/Stg=0, Prod=1</summary>
        public int EcsNightMaxCapacity { get; init; }

        // ── Logging ──────────────────────────────────────────────────────────
        /// <summary>
        /// Thời gian giữ log ECS CloudWatch. Dev=1 tuần, Stg=2 tuần, Prod=1 tháng.
        /// </summary>
        public RetentionDays LogRetentionDays { get; init; }

        // ── Scheduled Scaling ─────────────────────────────────────────────────
        /// <summary>
        /// Giờ UTC để scale-down ban đêm. Default "15" = 22:00 VN (UTC+7).
        /// Override khi deploy sang region khác timezone.
        /// </summary>
        public string ScaleDownHourUtc { get; init; }

        /// <summary>
        /// Giờ UTC để scale-up buổi sáng. Default "0" = 07:00 VN (UTC+7).
        /// </summary>
        public string ScaleUpHourUtc { get; init; }

        // ── ECR ──────────────────────────────────────────────────────────────
        /// <summary>
        /// DESTROY: xóa ECR repo khi cdk destroy (dev) |
        /// RETAIN: giữ lại images sau khi stack bị xóa (stg, prod)
        /// </summary>
        public RemovalPolicy EcrRemovalPolicy { get; init; }

        // ── Database (Reader) ─────────────────────────────────────────────────
        /// <summary>
        /// Số lượng Aurora Reader instances.
        /// Dev=0: không tạo reader (tiết kiệm ~$29/tháng, Writer cũng serve reads).
        /// Stg=1, Prod=1: tăng khả năng đọc và failover tự động.
        /// </summary>
        public int AuroraReaderCount { get; init; }

        // ── Networking ────────────────────────────────────────────────────────
        /// <summary>
        /// Khi true: dùng VPC Interface Endpoints (Stg/Prod — an toàn, không qua internet).
        /// Khi false: dùng 1 NAT Gateway (Dev — rẻ hơn khi traffic thấp).
        /// Chi phí so sánh (ap-northeast-1):
        ///   4 Interface Endpoints × 2 AZ ≈ $58/tháng (fixed hourly)
        ///   1 NAT Gateway ≈ $32/tháng + data transfer
        /// </summary>
        public bool UseVpcEndpoints { get; init; }

        // ── Bastion ───────────────────────────────────────────────────────────
        /// <summary>
        /// Khi true: tạo EC2 Bastion Host để kết nối DB từ local qua SSM.
        /// Dev/Stg=false: không cần Bastion, giảm attack surface và chi phí EC2.
        /// Prod=true: DBA cần Bastion để truy cập DB production an toàn.
        /// </summary>
        public bool BastionEnabled { get; init; }

        // ── Factory Methods ──────────────────────────────────────────────────

        /// <summary>
        /// Chọn preset dựa trên tên environment.
        /// Trả về Development() nếu tên không khớp bất kỳ preset nào.
        /// </summary>
        public static EnvironmentConfig FromName(string name) =>
            name?.ToLower() switch
            {
                "production" or "prod" => Production(),
                "staging" or "stg" => Staging(),
                _ => Development(),
            };

        /// <summary>
        /// Development preset — tối ưu cho vòng lặp dev nhanh và chi phí thấp nhất.
        ///
        /// Aurora:    t3.small  — rẻ hơn t3.medium ~50%
        /// DB:        DESTROY   — xóa sạch khi cdk destroy, không cần snapshot
        /// S3:        DESTROY   — files không có giá trị lâu dài ở dev
        /// ALB:       không bảo vệ — có thể cdk destroy bất kỳ lúc nào
        /// ECS tasks: 1 task ban ngày, 0 ban đêm (tắt hoàn toàn để tiết kiệm)
        /// </summary>
        public static EnvironmentConfig Development() =>
            new()
            {
                Name = "development",
                Suffix = "Dev",
                IsProduction = false,
                AuroraInstanceClass = InstanceClass.BURSTABLE3,
                AuroraInstanceSize = InstanceSize.SMALL, // t3.small — rẻ hơn t3.medium 50%
                DbRemovalPolicy = RemovalPolicy.DESTROY,
                StaticBucketRemovalPolicy = RemovalPolicy.DESTROY,
                StaticBucketAutoDelete = true,
                AlbDeletionProtection = false,
                EcsDesiredCount = 1, // 1 task đủ để test
                EcsMinCapacity = 1,
                EcsMaxCapacity = 4, // Giới hạn thấp để tránh chạy quá nhiều task
                EcsNightMinCapacity = 0, // Tắt hoàn toàn ban đêm
                EcsNightMaxCapacity = 0,
                LogRetentionDays = RetentionDays.ONE_WEEK,
                ScaleDownHourUtc = "15", // 22:00 VN (UTC+7)
                ScaleUpHourUtc = "0", // 07:00 VN (UTC+7)
                EcrRemovalPolicy = RemovalPolicy.DESTROY,
                // #6: Dev không cần Reader — không có read workload, tiết kiệm ~$29/tháng
                AuroraReaderCount = 0,
                // #4: Dev dùng NAT Gateway 1 AZ (rẻ hơn 4 Interface Endpoints khi traffic thấp)
                UseVpcEndpoints = false,
                // #5: Dev không cần Bastion — dùng ECS Exec hoặc port-forward trực tiếp
                BastionEnabled = false,
            };

        /// <summary>
        /// Staging preset — môi trường kiểm thử trước production.
        /// Gần giống production nhưng cho phép teardown dễ dàng.
        ///
        /// Aurora:    t3.small   — tiết kiệm chi phí staging
        /// DB:        SNAPSHOT   — snapshot trước khi xóa (debug production issues)
        /// S3:        RETAIN     — giữ test assets phục vụ debugging
        /// ALB:       không bảo vệ — team có thể teardown staging khi không cần
        /// ECS tasks: 1 task ban ngày, 0 ban đêm (staging không phục vụ user thật)
        /// </summary>
        public static EnvironmentConfig Staging() =>
            new()
            {
                Name = "staging",
                Suffix = "Stg",
                IsProduction = false,
                AuroraInstanceClass = InstanceClass.BURSTABLE3,
                AuroraInstanceSize = InstanceSize.SMALL, // t3.small để tiết kiệm
                DbRemovalPolicy = RemovalPolicy.SNAPSHOT, // Snapshot để debug prod issues
                StaticBucketRemovalPolicy = RemovalPolicy.RETAIN,
                StaticBucketAutoDelete = false,
                AlbDeletionProtection = false, // Cho phép teardown staging
                EcsDesiredCount = 1,
                EcsMinCapacity = 1,
                EcsMaxCapacity = 4,
                EcsNightMinCapacity = 0, // Staging tắt ban đêm
                EcsNightMaxCapacity = 0,
                LogRetentionDays = RetentionDays.TWO_WEEKS,
                ScaleDownHourUtc = "15",
                ScaleUpHourUtc = "0",
                EcrRemovalPolicy = RemovalPolicy.RETAIN,
                // #6: Staging có Reader để test failover trước khi lên Prod
                AuroraReaderCount = 1,
                // #4: Staging dùng VPC Endpoints để mirror sát môi trường Prod
                UseVpcEndpoints = true,
                // #5: Staging không cần Bastion trong tự động hóa CI/CD
                BastionEnabled = false,
            };

        /// <summary>
        /// Production preset — bảo vệ tối đa dữ liệu và availability.
        ///
        /// Aurora:    t3.medium  — đủ mạnh cho production traffic
        /// DB:        SNAPSHOT   — KHÔNG BAO GIỜ xóa DB mà không có snapshot
        /// S3:        RETAIN     — giữ assets sales/marketing quan trọng
        /// ALB:       bảo vệ    — phải tắt DeletionProtection thủ công trước khi destroy
        /// ECS tasks: 2 tasks ban ngày (HA), 1 task ban đêm (luôn có task ready)
        /// </summary>
        public static EnvironmentConfig Production() =>
            new()
            {
                Name = "production",
                Suffix = "Prod",
                IsProduction = true,
                AuroraInstanceClass = InstanceClass.BURSTABLE3,
                AuroraInstanceSize = InstanceSize.MEDIUM, // t3.medium
                DbRemovalPolicy = RemovalPolicy.SNAPSHOT,
                StaticBucketRemovalPolicy = RemovalPolicy.RETAIN,
                StaticBucketAutoDelete = false,
                AlbDeletionProtection = true,
                EcsDesiredCount = 2, // High availability: 2 tasks min
                EcsMinCapacity = 2,
                EcsMaxCapacity = 8, // Scale up đến 8 khi peak
                EcsNightMinCapacity = 1, // Production: KHÔNG BAO GIỜ về 0
                EcsNightMaxCapacity = 1,
                LogRetentionDays = RetentionDays.ONE_MONTH,
                ScaleDownHourUtc = "15",
                ScaleUpHourUtc = "0",
                EcrRemovalPolicy = RemovalPolicy.RETAIN,
                // #6: Prod cần Reader để tăng khả năng đọc và failover tự động
                AuroraReaderCount = 1,
                // #4: Prod dùng VPC Endpoints — an toàn, không qua internet
                UseVpcEndpoints = true,
                // #5: Prod cần Bastion để DBA truy cập DB an toàn qua SSM
                BastionEnabled = true,
            };
    }
}
