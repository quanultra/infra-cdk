# AWS Infrastructure CDK Project

Project này định nghĩa infrastructure AWS production-ready sử dụng AWS CDK với C# (.NET 8.0).

---

## 🏗️ Kiến trúc tổng quan

```text
Internet
   │
   ▼
[WAF – CloudFront Edge]        ← WafStack (us-east-1), lọc attack trước khi vào VPC
   │  3 managed rule groups
   ▼
CloudFront Distribution        ← Cache, HTTPS, gắn custom header bí mật
   │  HTTPS + X-Origin-Verify header
   ▼
Application Load Balancer      ← Public Subnet, kiểm tra X-Origin-Verify header
   │  HTTP 80 → redirect HTTPS 443
   ▼
ECS Fargate Service            ← Private Subnet, Auto Scaling 2–8 tasks
   │
   ▼
RDS Proxy                      ← Connection pooling, TLS bắt buộc
   │
   ▼
Aurora MySQL Cluster           ← Private Subnet, 1 Writer + 1 Reader
```

---

## 📦 Cấu trúc project

```text
src/InfraCdk/
├── Program.cs                      # Entry point — khởi tạo WafStack & InfraCdkStack
├── WafStack.cs                     # WAF riêng (CLOUDFRONT scope, us-east-1)
├── InfraCdkStack.cs                # Main stack — orchestrate tất cả Constructs
└── Constructs/
    ├── NetworkingConstruct.cs      # VPC, Subnets, IGW, Route Tables, VPC Endpoints
    ├── SecurityGroupsConstruct.cs  # ALB / ECS / RDS Security Groups (Least Privilege)
    ├── StorageConstruct.cs         # S3 Buckets + Lifecycle Rules
    ├── EcsConstruct.cs             # ECS Cluster, Fargate, Auto Scaling, Circuit Breaker
    ├── DatabaseConstruct.cs        # Aurora MySQL, RDS Proxy, Password Rotation
    ├── LoadBalancerConstruct.cs    # ALB, ACM Certificate, Listeners, Deletion Protection
    ├── CloudFrontConstruct.cs      # CloudFront Distribution + Route53
    ├── BastionConstruct.cs         # Bastion Host (SSM Session Manager)
    └── MonitoringConstruct.cs      # CloudWatch Alarms + Dashboard + SNS
```

---

## ⚙️ Cấu hình dự án (CDK Context)

Toàn bộ cấu hình được quản lý qua **CDK Context** trong `cdk.json`.
**Không hardcode** bất kỳ giá trị nào vào code.

### Các context key quan trọng

| Key | Mô tả | Bắt buộc | Mặc định |
|-----|-------|----------|---------|
| `domainName` | Domain chính (Route53, ACM, CloudFront) | ✅ Bắt buộc | — |
| `environment` | `production` hoặc `development` | Không | `development` |
| `staticBucketName` | Tên S3 bucket static assets. Nếu bỏ trống CDK tự sinh | Không | CDK auto |
| `notificationEmail` | Email nhận CloudWatch Alarm | Không | — |

### Cách thay đổi cấu hình

**Cố định trong `cdk.json`** (dùng cho dev — dùng thường xuyên):

```json
{
  "context": {
    "domainName": "example.com",
    "staticBucketName": "my-static-resources-bucket",
    "environment": "development",
    "notificationEmail": ""
  }
}
```

**Override khi deploy** (dùng cho production — không muốn commit vào git):

```bash
cdk deploy --all \
  --context domainName=myapp.com \
  --context staticBucketName=myapp-static-prod \
  --context environment=production \
  --context notificationEmail=alert@myapp.com
```

> ⚠️ **Lưu ý**: Nếu `domainName` không được set, CDK sẽ ném lỗi rõ ràng khi `cdk synth`.

---

## 🔒 Security

### Security Groups — Least Privilege Outbound

Tất cả Security Groups được cấu hình `AllowAllOutbound = false`.
Chỉ mở đúng port cần thiết:

```
ALB SG:  inbound  443,80 ← Internet
         outbound 80 → ECS SG

ECS SG:  inbound  80 ← ALB SG
         outbound 3306 → RDS SG       (MySQL qua RDS Proxy)
         outbound 443  → VPC CIDR     (ECR pull image, CloudWatch Logs, SecretsManager)

RDS SG:  inbound  3306 ← ECS SG, Bastion SG
         outbound KHÔNG CÓ
```

### DB Credentials — ECS Native Injection

ECS Task **không dùng AWS SDK** để đọc secret. Thay vào đó, CDK cấu hình ECS Agent
tự inject credentials trước khi container khởi động:

```
CDK synth → Task Execution Role được grant secretsmanager:GetSecretValue tự động
Deploy    → ECS Agent fetch từng field của secret → inject làm Environment Variable
Container → chỉ cần đọc Environment.GetEnvironmentVariable()
```

Container nhận các env vars sau khi start:

| Env Var | Nguồn | Mô tả |
|---------|-------|-------|
| `DB_USERNAME` | Secrets Manager (field: `username`) | DB user |
| `DB_PASSWORD` | Secrets Manager (field: `password`) | DB password |
| `DB_HOST` | RDS Proxy Endpoint | Kết nối qua proxy, không direct |
| `DB_PORT` | Hardcode `3306` | MySQL port |
| `DB_NAME` | Hardcode `mydatabase` | DB name |

### WAF + CloudFront → ALB Header

| Tầng | Cơ chế bảo vệ |
|------|--------------|
| **WAF (CloudFront Edge)** | 3 managed rule groups: CommonRuleSet, IpReputationList, KnownBadInputs |
| **CloudFront → ALB** | Custom header `X-Origin-Verify` — ALB từ chối request không có header |
| **ALB → ECS** | Security Group — chỉ nhận traffic từ ALB SG |
| **ECS → RDS** | Security Group — chỉ nhận MySQL từ ECS SG và Bastion SG |
| **Database** | Credentials lưu Secrets Manager, tự xoay vòng mỗi 30 ngày |
| **RDS Proxy** | RequireTLS = true |

---

## 🌍 Environment: Development vs Production

Truyền `--context environment=production` để bật chế độ bảo vệ production.

| Tính năng | Development (default) | Production |
|-----------|----------------------|------------|
| **Aurora RemovalPolicy** | `DESTROY` — xóa DB khi `cdk destroy` | `SNAPSHOT` — tạo final snapshot |
| **S3 Static Bucket** | `DESTROY` + files bị xóa | `RETAIN` — bucket giữ nguyên |
| **ALB Deletion Protection** | `false` — `cdk destroy` hoạt động | `true` — phải tắt thủ công trước |
| **ECS scale-down ban đêm** | `0 tasks` — tắt hoàn toàn | `1 task` — luôn còn 1 task |
| **Aurora RemovalPolicy** | Xóa sạch | Snapshot + preserve |

### ECS Scheduled Scaling

```
22:00 VN (15:00 UTC)  →  Scale DOWN
  - Dev:        0 tasks  (tiết kiệm 100% Fargate cost ban đêm)
  - Production: 1 task   (giữ 1 task để xử lý emergency request)

07:00 VN (00:00 UTC)  →  Scale UP
  - Cả hai:     2 tasks minimum, 8 tasks maximum
```

> ⚠️ **Quan trọng (Production)**: HealthCheckGracePeriod = 60s được áp dụng để tránh
> Circuit Breaker trigger khi tasks khởi động sau scale-up từ trạng thái thấp.

### ECS Deployment Circuit Breaker

Khi deploy image lỗi (container crash liên tục):

- **Không có Circuit Breaker** → ECS retry mãi → downtime kéo dài
- **Có Circuit Breaker** (`Rollback = true`) → ECS tự động rollback về task definition cũ trong vài phút

---

## 💰 Tối ưu chi phí

- **Không có NAT Gateway** (~$32/tháng) — thay bằng VPC Endpoints
- **ECS tắt ban đêm** — Dev: 0 tasks | Production: 1 task (22:00–07:00 VN)
- **S3 Lifecycle Rules**:
  - ALB logs: S3-IA (30d) → Glacier Instant (90d) → xóa (365d)
  - Static assets current: S3-IA sau 90d
  - Static assets non-current: giữ 3 version, xóa sau 90d
- **DeregistrationDelay = 30s** (default 300s) — tasks scale-in nhanh hơn

---

## 🚀 Build & Deploy

### Yêu cầu

- AWS CLI đã cấu hình credentials
- .NET 8.0 SDK
- Node.js & AWS CDK Toolkit: `npm install -g aws-cdk`
- Route 53 Hosted Zone cho domain đang dùng

### 0. Cấu hình AWS Credentials

```bash
aws configure
```

Nhập thông tin:

- **AWS Access Key ID**
- **AWS Secret Access Key**
- **Default region**: region chính của bạn (VD: `ap-northeast-1`)
- **Default output format**: `json`

Thiết lập biến môi trường (cần cho CDK):

```bash
export CDK_DEFAULT_ACCOUNT=$(aws sts get-caller-identity --query Account --output text)
export CDK_DEFAULT_REGION=$(aws configure get region)
```

### 1. Cập nhật cdk.json trước khi deploy

```json
{
  "context": {
    "domainName": "yourdomain.com",
    "staticBucketName": "your-app-static-assets",
    "environment": "development",
    "notificationEmail": "your@email.com"
  }
}
```

> `domainName` **bắt buộc phải set** — CDK sẽ báo lỗi nếu thiếu.

### 2. Build

```bash
dotnet restore src/InfraCdk.sln
dotnet build src/InfraCdk.sln
```

### 3. Bootstrap CDK (chỉ cần chạy lần đầu)

CloudFront WAF bắt buộc ở `us-east-1`, nên cần bootstrap **cả 2 region**:

```bash
# Bootstrap region chính (VD: ap-northeast-1)
cdk bootstrap aws://$CDK_DEFAULT_ACCOUNT/$CDK_DEFAULT_REGION

# Bootstrap us-east-1 (bắt buộc cho WafStack)
cdk bootstrap aws://$CDK_DEFAULT_ACCOUNT/us-east-1
```

### 4. Synthesize CloudFormation templates

```bash
cdk synth
```

### 5. Deploy

> ⚠️ **Quan trọng**: `WafStack` **phải deploy trước** `InfraCdkStack` vì InfraCdkStack cần WAF ARN từ WafStack.

**Development:**

```bash
cdk deploy --all
```

**Production:**

```bash
cdk deploy --all \
  --context domainName=myapp.com \
  --context staticBucketName=myapp-static-prod \
  --context environment=production \
  --context notificationEmail=alert@myapp.com
```

Hoặc deploy từng stack theo thứ tự:

```bash
cdk deploy WafStack
cdk deploy InfraCdkStack
```

### 6. Xem trạng thái và so sánh thay đổi

```bash
cdk diff WafStack
cdk diff InfraCdkStack
cdk list
```

### 7. Xóa infrastructure

> ⚠️ **Production**: Trước khi `cdk destroy`, phải **tắt ALB Deletion Protection** thủ công:
> AWS Console → EC2 → Load Balancers → MyALB → Edit attributes → tắt Deletion Protection.

```bash
# Bước 1: Xóa Main Stack trước
cdk destroy InfraCdkStack

# Bước 2: Xóa WAF Stack sau
cdk destroy WafStack
```

> **Sau destroy ở Production:**
>
> - Aurora snapshot vẫn còn (xóa thủ công trong RDS Snapshots nếu cần)
> - S3 Static Bucket vẫn còn (xóa thủ công trong S3 Console nếu cần)

---

## 📝 Ghi chú vận hành

| Việc cần làm | Lệnh / Link |
|---|---|
| Xem ECS logs | AWS Console → CloudWatch → Log Groups → `/ecs/fargate-service-logs` |
| Xem WAF metrics | AWS Console → WAF & Shield → WebACLs → `CloudFrontWebACL` |
| Xem ALB access logs | AWS Console → S3 → `ALBLogBucket` |
| Rotate DB password ngay | AWS Console → Secrets Manager → chọn secret → Rotate immediately |
| Xem Dashboard | CloudFormation Output `DashboardUrl` |
| Stop Bastion (tiết kiệm tiền) | `aws ec2 stop-instances --instance-ids <ID>` |
| Force scale ECS thủ công | `aws ecs update-service --cluster ECSCluster --service MyFargateService --desired-count 2` |

---

## 📊 Monitoring & Alerting (CloudWatch)

### Danh sách CloudWatch Alarms

| Alarm | Điều kiện | Nguyên nhân thường gặp |
|-------|-----------|------------------------|
| `ECS-CPU-High` | CPU > 80% × 15 phút | Traffic tăng đột biến, code không efficient |
| `ECS-Memory-High` | Memory > 80% × 15 phút | Memory leak, Task Memory quá nhỏ |
| `ALB-5XX-Errors` | > 10 lỗi 5XX / 5 phút | App crash, unhandled exception |
| `ALB-High-Response-Time` | p99 > 2s × 10 phút | DB query chậm, N+1 query |
| `ALB-Unhealthy-Hosts` | Unhealthy host > 0 × 2 phút | ECS task fail health check `/health` |
| `Aurora-CPU-High` | CPU > 80% × 15 phút | Heavy query, thiếu index |
| `Aurora-Connections-High` | Connections > 100 × 10 phút | Connection leak, pool không đủ |
| `Aurora-Low-Freeable-Memory` | < 200 MB × 10 phút | Instance type quá nhỏ |

> Khi alarm TRIGGER → SNS gửi email. Khi về lại OK → email thông báo resolved.

### ALB Health Check

ECS containers được kiểm tra sức khỏe qua ALB tại endpoint `/health`:

```
Path:                GET /health
Expected HTTP Code:  200
Healthy Threshold:   2 lần liên tiếp → Healthy
Unhealthy Threshold: 3 lần liên tiếp → Unhealthy (task bị replace)
Timeout:             5 giây
Interval:            30 giây
Grace Period:        60 giây (sau khi task register vào ALB mới bắt đầu check)
```

> ⚠️ **Bắt buộc**: App **phải implement** endpoint `GET /health` trả về `HTTP 200` khi ready.
> Nếu thiếu, tất cả tasks sẽ bị ALB đánh dấu Unhealthy → vòng lặp replace liên tục.

### Cấu hình email nhận Alert

**Cách 1 — Truyền qua CLI:**

```bash
cdk deploy InfraCdkStack --context notificationEmail=admin@example.com
```

**Cách 2 — Cố định trong `cdk.json`:**

```json
{
  "context": {
    "notificationEmail": "admin@example.com"
  }
}
```

> ⚠️ Sau deploy, AWS gửi email `"AWS Notification - Subscription Confirmation"`.
> **Phải click "Confirm subscription"** thì mới nhận được alarm notifications.

### Xem CloudWatch Dashboard

```bash
aws cloudformation describe-stacks \
  --stack-name InfraCdkStack \
  --query "Stacks[0].Outputs[?OutputKey=='DashboardUrl'].OutputValue" \
  --output text
```

---

## 🗄️ Kết nối DB từ máy local (SSM Port Forwarding)

Aurora nằm trong Private Subnet, không có public endpoint. Để kết nối từ máy local,
dùng **Bastion Host qua SSM Session Manager** — không cần SSH key, không cần mở port 22.

```text
Local Machine ──→ AWS SSM ──→ DBBastionHost (EC2) ──→ RDS Proxy ──→ Aurora MySQL
  :13306 (local)                 (Public Subnet)          :3306
```

### Bước 1: Cài Session Manager Plugin

```bash
# macOS
brew install session-manager-plugin

# Linux
curl "https://s3.amazonaws.com/session-manager-downloads/plugin/latest/ubuntu_64bit/session-manager-plugin.deb" -o plugin.deb
sudo dpkg -i plugin.deb
```

### Bước 2: Start Bastion Instance (nếu đang STOPPED)

```bash
INSTANCE_ID=$(aws cloudformation describe-stacks \
  --stack-name InfraCdkStack \
  --query "Stacks[0].Outputs[?OutputKey=='BastionInstanceId'].OutputValue" \
  --output text)

echo "Bastion Instance ID: $INSTANCE_ID"
aws ec2 start-instances --instance-ids $INSTANCE_ID
aws ec2 wait instance-running --instance-ids $INSTANCE_ID
```

### Bước 3: Lấy RDS Proxy Endpoint

```bash
RDS_PROXY_ENDPOINT=$(aws cloudformation describe-stacks \
  --stack-name InfraCdkStack \
  --query "Stacks[0].Outputs[?OutputKey=='RDSProxyEndpoint'].OutputValue" \
  --output text)

echo "RDS Proxy Endpoint: $RDS_PROXY_ENDPOINT"
```

### Bước 4: Tạo SSM Port Forwarding Tunnel

```bash
aws ssm start-session \
  --target $INSTANCE_ID \
  --document-name AWS-StartPortForwardingSessionToRemoteHost \
  --parameters "{
    \"host\": [\"$RDS_PROXY_ENDPOINT\"],
    \"portNumber\": [\"3306\"],
    \"localPortNumber\": [\"13306\"]
  }"
```

> Terminal này giữ kết nối tunnel. **Mở terminal mới** để làm bước tiếp theo.

### Bước 5: Lấy DB Credentials từ Secrets Manager

```bash
SECRET_ARN=$(aws secretsmanager list-secrets \
  --query "SecretList[?contains(Name, 'MyAuroraCluster')].ARN" \
  --output text)

DB_PASSWORD=$(aws secretsmanager get-secret-value \
  --secret-id $SECRET_ARN \
  --query SecretString \
  --output text | python3 -c "import sys,json; print(json.load(sys.stdin)['password'])")

echo "DB Password: $DB_PASSWORD"
```

### Bước 6: Kết nối MySQL

```bash
mysql -h 127.0.0.1 -P 13306 -u sysadmin -p"$DB_PASSWORD" mydatabase
```

Hoặc dùng MySQL Workbench / DBeaver:

| Trường | Giá trị |
|--------|---------|
| **Host** | `127.0.0.1` |
| **Port** | `13306` |
| **User** | `sysadmin` |
| **Password** | (lấy từ Bước 5) |
| **Database** | `mydatabase` |

### Bước 7: STOP Bastion sau khi dùng xong

> ⚠️ `t3.micro` tốn ~$0.013/giờ → ~$9.4/tháng nếu để chạy liên tục.

```bash
aws ec2 stop-instances --instance-ids $INSTANCE_ID
```

---

## 🛡️ Known Issues & Limitations

| # | Issue | Trạng thái | Ghi chú |
|---|-------|-----------|---------|
| **#1** | `UnsafeUnwrap()` trên CloudFront-ALB header secret | ⚠️ Pending | Secret bị nhúng plaintext vào CF template. Cần dùng CloudFront OAC hoặc giải pháp khác |
| **#10** | VPC Endpoints vs NAT Gateway cost | 📋 Review | 4 Interface Endpoints × 2 AZ = ~$58/tháng. Chỉ rẻ hơn NAT nếu data transfer > $26/tháng |
| **#12** | Chưa có multi-environment strategy | 📋 Backlog | Hiện dùng context flag `environment` — đủ cho 1–2 env |
| **#13** | Aurora `t3.medium` thay vì Serverless v2 | 📋 Review | Serverless v2 rẻ hơn khi idle. Cân nhắc cho dev environment |
| **#16** | Chưa có X-Ray distributed tracing | 📋 Backlog | Cần thêm X-Ray daemon sidecar vào ECS task |
| **#17** | ECR Repository chưa được tạo trong CDK | 📋 Backlog | Image hiện dùng `ContainerImage.FromAsset()` |
