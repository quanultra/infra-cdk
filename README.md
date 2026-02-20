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
    ├── SecurityGroupsConstruct.cs  # ALB / ECS / RDS Security Groups
    ├── StorageConstruct.cs         # S3 Buckets + Lifecycle Rules
    ├── EcsConstruct.cs             # ECS Cluster, Fargate, Auto Scaling
    ├── DatabaseConstruct.cs        # Aurora MySQL, RDS Proxy, Password Rotation
    ├── LoadBalancerConstruct.cs    # ALB, ACM Certificate, Listeners
    └── CloudFrontConstruct.cs      # CloudFront Distribution + Route53
```

---

## 🔒 Security

| Tầng | Cơ chế bảo vệ |
|------|--------------|
| **WAF (CloudFront Edge)** | 3 managed rule groups: CommonRuleSet, IpReputationList, KnownBadInputs |
| **CloudFront → ALB** | Custom header `X-Origin-Verify` — ALB từ chối request không có header |
| **ALB → ECS** | Security Group — chỉ nhận traffic từ ALB SG |
| **ECS → RDS** | Security Group — chỉ nhận MySQL từ ECS SG |
| **Database** | Credentials lưu Secrets Manager, tự xoay vòng mỗi 30 ngày |
| **RDS Proxy** | RequireTLS = true |

---

## 💰 Tối ưu chi phí

- **Không có NAT Gateway** (~$32/tháng) — thay bằng VPC Endpoints
- **ECS tắt ban đêm** — schedule scale-down 22:00 VN (15:00 UTC), bật lại 07:00 VN
- **S3 Lifecycle Rules** — ALB logs tự động chuyển S3-IA → Glacier → xóa sau 1 năm

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

### 1. Build

```bash
dotnet restore src/InfraCdk.sln
dotnet build src/InfraCdk.sln
```

### 2. Bootstrap CDK (chỉ cần chạy lần đầu)

CloudFront WAF bắt buộc ở `us-east-1`, nên cần bootstrap **cả 2 region**:

```bash
# Bootstrap region chính (VD: ap-northeast-1)
cdk bootstrap aws://$CDK_DEFAULT_ACCOUNT/$CDK_DEFAULT_REGION

# Bootstrap us-east-1 (bắt buộc cho WafStack)
cdk bootstrap aws://$CDK_DEFAULT_ACCOUNT/us-east-1
```

### 3. Synthesize CloudFormation templates

```bash
cdk synth
```

### 4. Deploy

> ⚠️ **Quan trọng**: `WafStack` **phải deploy trước** `InfraCdkStack` vì InfraCdkStack cần WAF ARN từ WafStack.

```bash
# Bước 1: Deploy WAF Stack (luôn ở us-east-1)
cdk deploy WafStack

# Bước 2: Deploy Main Stack (region chính)
cdk deploy InfraCdkStack
```

Hoặc deploy cả hai cùng lúc (CDK tự xử lý thứ tự dependency):

```bash
cdk deploy --all
```

### 5. Xem trạng thái và so sánh thay đổi

```bash
# Xem sự khác biệt trước khi deploy
cdk diff WafStack
cdk diff InfraCdkStack

# Xem tất cả stacks
cdk list
```

### 6. Xóa infrastructure

> ⚠️ **Quan trọng**: Xóa `InfraCdkStack` trước, sau đó mới xóa `WafStack`.

```bash
# Bước 1: Xóa Main Stack trước
cdk destroy InfraCdkStack

# Bước 2: Xóa WAF Stack sau
cdk destroy WafStack
```

---

## ⚙️ Biến môi trường & Cấu hình

| Biến | Mô tả | Ví dụ |
|------|-------|--------|
| `CDK_DEFAULT_ACCOUNT` | AWS Account ID | `123456789012` |
| `CDK_DEFAULT_REGION` | Region chính deploy | `ap-northeast-1` |

Domain name được cấu hình trong `InfraCdkStack.cs`:

```csharp
const string domainName = "example.com"; // ← Đổi thành domain của bạn
```

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

---

## 📊 Monitoring & Alerting (CloudWatch)

### Danh sách CloudWatch Alarms

| Alarm | Điều kiện | Nguyên nhân thường gặp |
|-------|-----------|------------------------|
| `ECS-CPU-High` | CPU > 80% × 15 phút | Traffic tăng đột biến, code không efficient |
| `ECS-Memory-High` | Memory > 80% × 15 phút | Memory leak, Task Memory quá nhỏ |
| `ALB-5XX-Errors` | > 10 lỗi 5XX / 5 phút | App crash, unhandled exception |
| `ALB-High-Response-Time` | p99 > 2s × 10 phút | DB query chậm, N+1 query |
| `ALB-Unhealthy-Hosts` | Unhealthy host > 0 × 2 phút | ECS task fail health check |
| `Aurora-CPU-High` | CPU > 80% × 15 phút | Heavy query, thiếu index |
| `Aurora-Connections-High` | Connections > 100 × 10 phút | Connection leak, pool không đủ |
| `Aurora-Low-Freeable-Memory` | < 200 MB × 10 phút | Instance type quá nhỏ |

> Khi alarm TRIGGER → SNS gửi email. Khi về lại OK → email thông báo resolved (trừ 5XX và Connections).

### Cấu hình email nhận Alert

Có 2 cách:

**Cách 1 — Truyền qua CLI khi deploy:**

```bash
cdk deploy InfraCdkStack --context notificationEmail=admin@example.com
```

**Cách 2 — Cấu hình cố định trong `cdk.json`:**

```json
{
  "context": {
    "notificationEmail": "admin@example.com"
  }
}
```

> ⚠️ **Sau deploy**, AWS sẽ gửi email `"AWS Notification - Subscription Confirmation"` đến địa chỉ trên.
> **Phải click "Confirm subscription"** trong email đó thì mới nhận được alarm notifications.

### Xem CloudWatch Dashboard

Dashboard `InfraOverview` được tạo tự động sau khi deploy. Gồm 9 biểu đồ:

```text
Row 1 — ECS Fargate:
  [CPU Utilization %]      [Memory Utilization %]

Row 2 — Application Load Balancer:
  [4XX/5XX Error Counts]   [Response Time p50/p99]

Row 3 — Aurora MySQL:
  [CPU Utilization %]  [DB Connections]  [Freeable Memory]
```

Truy cập nhanh:

```bash
# Lấy URL Dashboard từ CloudFormation Output
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
# Lấy Instance ID từ CloudFormation Output
INSTANCE_ID=$(aws cloudformation describe-stacks \
  --stack-name InfraCdkStack \
  --query "Stacks[0].Outputs[?OutputKey=='BastionInstanceId'].OutputValue" \
  --output text)

echo "Bastion Instance ID: $INSTANCE_ID"

# Start instance
aws ec2 start-instances --instance-ids $INSTANCE_ID

# Chờ instance ready (~30 giây)
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

Lệnh này tạo tunnel: `localhost:13306` → `RDS Proxy:3306` qua Bastion.

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

> Terminal này sẽ giữ kết nối tunnel. **Mở terminal mới** để thực hiện bước tiếp theo.

### Bước 5: Lấy DB Password từ Secrets Manager

```bash
# Lấy Secret ARN
SECRET_ARN=$(aws secretsmanager list-secrets \
  --query "SecretList[?contains(Name, 'MyAuroraCluster')].ARN" \
  --output text)

# Lấy password
DB_PASSWORD=$(aws secretsmanager get-secret-value \
  --secret-id $SECRET_ARN \
  --query SecretString \
  --output text | python3 -c "import sys,json; print(json.load(sys.stdin)['password'])")

echo "DB Password: $DB_PASSWORD"
```

### Bước 6: Kết nối MySQL

```bash
# Kết nối qua tunnel local port 13306
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

### Bước 7: STOP Bastion sau khi dùng xong (tiết kiệm chi phí)

> ⚠️ `t3.micro` tốn ~$0.013/giờ → ~$9.4/tháng nếu để chạy liên tục.
> **Hãy STOP instance ngay sau khi không dùng nữa.**

```bash
aws ec2 stop-instances --instance-ids $INSTANCE_ID
```
