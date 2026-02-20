# AWS Infrastructure CDK Project

This project defines a comprehensive, production-ready AWS infrastructure using AWS Cloud Development Kit (CDK) with C# (.NET 8.0).

## 🏗️ Architecture Overview

The infrastructure includes the following resources, designed for high availability, security, and scalability:

### 1. Networking (VPC)

* **VPC**: Custom VPC with CIDR `10.0.0.0/16`.
* **Subnets**:
  * 2 Public Subnets (for Load Balancer, NAT Gateway).
  * 2 Private Subnets (for ECS Fargate, RDS).
* **Gateways**: Internet Gateway (IGW) and NAT Gateway (with Elastic IP).
* **Endpoints**: VPC Gateway Endpoint for S3 (secure internal access).

### 2. Computing (Compute)

* **ECS Fargate**: Serverless container orchestration.
  * Cluster: `ECSCluster`.
  * Service: Runs in Private Subnets with Auto Scaling (2-8 tasks based on CPU).
  * Task Definition: CPU 256, Memory 512MiB.

### 3. Database (Storage)

* **Amazon Aurora MySQL**:
  * Engine: Aurora MySQL 3.04.0.
  * Topology: 1 Writer + 1 Reader instance in Private Subnets.
* **RDS Proxy**:
  * Manages connection pooling for better performance and scalability.
  * Secure access via IAM Role.

### 4. Load Balancing & Delivery (CDN)

* **Application Load Balancer (ALB)**:
  * Public facing, listens on HTTP (80) and HTTPS (443).
  * Redirects HTTP to HTTPS.
* **Amazon CloudFront (CDN)**:
  * Caches static content at Edge locations.
  * Secure origin connection (HTTPS) to ALB.
* **Route 53 & ACM**:
  * Hosted Zone management.
  * SSL/TLS Certificate via AWS Certificate Manager (ACM).
  * DNS Alias records pointing to CloudFront/ALB.

### 5. Security (Security)

* **AWS WAF (Web Application Firewall)**:
  * Attached to ALB to protect against common web exploits (AWSManagedRulesCommonRuleSet).
* **Security Groups**:
  * Strict inbound/outbound rules (ALB -> ECS -> RDS).

### 6. Monitoring & Operations (Ops)

* **CloudWatch Logs**: Centralized logging for ECS Fargate.
* **CloudWatch Alarms**:
  * Alert on High CPU (>80%).
  * Alert on 5XX Errors.
* **SNS Topic**: Sends email notifications for alarms.

## 🚀 Build & Deploy

### Prerequisites

* AWS CLI configured with appropriate credentials.
* .NET 8.0 SDK installed.
* Node.js & AWS CDK Toolkit installed (`npm install -g aws-cdk`).
### 0. Configure AWS Credentials

Before running the project, you must configure your AWS credentials so the CDK can query your account (e.g., for Route 53 lookups).

```bash
aws configure
```

You will be prompted to enter the following information:
*   **AWS Access Key ID**: Your IAM user access key.
*   **AWS Secret Access Key**: Your IAM user secret key.
*   **Default region name**: The region where your Route 53 Hosted Zone is located (e.g., `ap-southeast-1` or `us-east-1`).
*   **Default output format**: `json` (optional).

**Note:** Ensure your IAM user has sufficient permissions (e.g., `AdministratorAccess` or specific policies for EC2, ECS, RDS, Route53, etc.).

```bash
dotnet restore src/InfraCdk.sln
dotnet build src/InfraCdk.sln
```

### 2. Synthesize CloudFormation template

```bash
cdk synth
```

### 3. Deploy to AWS

```bash
cdk deploy
```

### 4. Maintenance

* `cdk diff`: Compare deployed stack with current state.
* `cdk destroy`: Delete the deployed stack from AWS.
