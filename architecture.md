# Enterprise Architecture & Data Flow Specification

**Project:** Universal AI LLM Gateway  
**Runtime:** .NET 8 Minimal API (C#)  
**Target Environments:** Development, Test/Staging, Production (Hybrid On-Premises & AWS Bedrock)  
**Document Version:** 2.5.0  
**Status:** Enterprise Multi-Environment, Host Network Trust & Billing Governance Approved  

---

## 1. Executive Summary & Core Security Principles

The **Universal AI LLM Gateway** provides a hardened, resilient, 100% self-contained enterprise abstraction layer mediating communication between client microservices and Large Language Model (LLM) providers (AWS Bedrock Runtime and Local engines like Ollama, LM Studio, and llama.cpp).

### 1.1 Core Architecture Capabilities

1. **Standalone Built-in Operation:** 100% self-contained .NET 8 service with built-in web management UI, in-memory telemetry, and persistent daily compliance audit logs without any required external monitoring servers.
2. **Dynamic AWS Bedrock Model Discovery & Custom Model ARNs:** Live dynamic foundation models querying via `AmazonBedrockClient.ListFoundationModelsAsync` with curated fallback and support for custom fine-tuned model ARNs.
3. **Multi-Environment Isolation (DEV vs. TEST vs. PROD):**
   - **DEV:** 100% Local storage (`./data/audit_logs`), Developer AWS named profiles (`~/.aws/config`, `default`), **$0/month cloud cost**.
   - **TEST:** On-Premises **AWS IAM Roles Anywhere** X.509 PKI authentication, **Local + AWS S3** cold storage, and AWS KMS Test CMK.
   - **PROD:** On-Premises **AWS IAM Roles Anywhere** X.509 PKI authentication, **Local + AWS S3 Glacier** 365-day compliance archival, and AWS KMS Prod CMK.
4. **Secretless On-Premises Execution:** In TEST and PROD, no permanent AWS access keys (`AKIA...`) are stored on servers; all authentication is mediated via short-lived, auto-refreshing AWS STS session credentials (`sts:AssumeRole`).
5. **Host Network Trust (Source IP / CIDR Whitelisting):** Cryptographic and network binding per registered application; only requests originating from registered server host IPs or CIDR subnets (e.g., `10.240.12.50/32`, `192.168.1.0/24`) are permitted to mint STS tokens or invoke model endpoints. Unauthorized hosts receive **`403 Forbidden: HOST_IP_NOT_AUTHORIZED`**.
6. **Billing & Cost Governance:** Configurable Input and Output Token Cost per 1M tokens per application, automated model catalog pricing synchronization, real-time financial KPI cards, per-application spend share analytics, and 1-click CSV export.
7. **Bidirectional Enterprise Guardrails:** Pre-execution inspection (PCI with Luhn validation, PII, Secrets, Prompt Injection) on inbound prompts and post-execution inspection (Credential/Secret leakage) on outbound model responses.
8. **Zero-Downtime Dual-Key Rotation:** Primary + Secondary key lifecycle with configurable grace period (e.g. 7 days) and emergency instant revocation.
9. **Abuse Protection & Safety Clamps:** 50,000 character prompt limit (`413 Payload Too Large`) and 4,096 token output ceiling clamp.
10. **Persistent Compliance Audit Trail:** Non-blocking async channel writer persisting structured records to daily rolling JSONL files with query and CSV export.

---

## 2. Multi-Environment Topology Architecture

```mermaid
flowchart TD
    subgraph DEV_ENV["1. Development Environment (Local Machine / VM)"]
        DevUser["Developer Workstation"] --> DevGW["Unified Gateway (DEV)"]
        DevGW --> DevStorage["Local Disk Storage<br/>(./data/audit_logs/*.jsonl)"]
        DevGW --> DevProfile["AWS Named Profile<br/>(~/.aws/config, 'default')"]
        DevProfile --> BedrockDev["AWS Bedrock (DEV)"]
    end

    subgraph TEST_ENV["2. Test / Staging Environment (On-Premises Hybrid)"]
        TestApps["Staging Microservices<br/>(Authorized Host IPs)"] --> TestGW["Unified Gateway (TEST)"]
        TestGW --> TestLocal["Local Buffer Storage"]
        TestLocal --> TestS3["AWS S3 Test Bucket<br/>(corp-audit-test)"]
        TestGW --> TestPKI["Enterprise X.509 Certificate<br/>(/etc/pki/gateway/test-client.crt)"]
        TestPKI --> TestRA["AWS IAM Roles Anywhere"]
        TestRA --> TestSTS["Temporary STS Credentials"]
        TestSTS --> BedrockTest["AWS Bedrock (TEST)"]
        TestGW --> TestKMS["AWS KMS Test CMK"]
    end

    subgraph PROD_ENV["3. Production Environment (Hardened On-Premises HA)"]
        ProdApps["Production Microservices<br/>(Authorized Host IPs / Subnets)"] --> ProdGW["Unified Gateway (PROD)"]
        ProdGW --> ProdLocal["Local Buffer Storage"]
        ProdLocal --> ProdS3["AWS S3 Glacier Archive<br/>(corp-audit-prod)"]
        ProdGW --> ProdPKI["Enterprise X.509 Certificate / HSM<br/>(/etc/pki/gateway/prod-client.crt)"]
        ProdPKI --> ProdRA["AWS IAM Roles Anywhere"]
        ProdRA --> ProdSTS["Temporary STS Credentials"]
        ProdSTS --> BedrockProd["AWS Bedrock (PROD)"]
        ProdGW --> ProdKMS["AWS KMS Prod CMK (Multi-Region)"]
    end
```

---

## 3. Host Network Trust & Source IP/CIDR Whitelist Architecture

```mermaid
flowchart TD
    subgraph ClientLayer["1. Incoming Client Request"]
        HostA["Authorized Host A<br/>IP: 10.240.12.50"]
        HostB["Authorized Microservice Subnet<br/>IP: 192.168.1.100 (in 192.168.1.0/24)"]
        Attacker["Unauthorized Host / Rogue Machine<br/>IP: 203.0.113.88"]
    end

    subgraph GatewayBoundary["2. Unified LLM Gateway (/gateway/sts/token & /gateway/{appId}/invoke)"]
        IpExtractor["Extract Client IP<br/>(RemoteIpAddress / X-Forwarded-For)"]
        AppLookup["Lookup AppConfig by appId<br/>Retrieve registered 'AllowedCidrs'"]
        CidrMatcher{"CIDR Matcher<br/>Is Client IP in AllowedCidrs?"}
        
        AuthPipeline["Key / STS Authenticator<br/>(Validate API Key Hash & Expiry)"]
        GuardrailPipeline["Bidirectional Guardrails & Model Router"]
    end

    subgraph Outcomes["3. Authorization Outcomes"]
        Success["200 OK: Process Request & Issue STS Token"]
        Forbidden["403 Forbidden: HOST_IP_NOT_AUTHORIZED<br/>Log Security Violation to Audit Trail"]
    end

    HostA -->|Request with X-API-Key| IpExtractor
    HostB -->|Request with X-API-Key| IpExtractor
    Attacker -->|Stolen X-API-Key| IpExtractor

    IpExtractor --> AppLookup
    AppLookup --> CidrMatcher

    CidrMatcher -->|Allowed: IP in Range| AuthPipeline
    CidrMatcher -->|Allowed: AllowedCidrs Empty (Any Host)| AuthPipeline
    CidrMatcher -->|Denied: IP not in Range| Forbidden

    AuthPipeline --> GuardrailPipeline
    GuardrailPipeline --> Success
```

---

## 4. End-to-End System Architecture & Trust Boundaries

```mermaid
flowchart TD
    subgraph TB_EXT["TRUST BOUNDARY 1: External / Client Zone (Untrusted)"]
        ClientAppA["Enterprise Application A\n(Customer Support Agent)"]
        ClientAppB["Enterprise Application B\n(Finance & Invoicing)"]
        AdminUser["Platform Administrator\n(Dashboard UI)"]
        MobileClient["Frontend / Mobile Worker\n(Short-Term STS Session)"]
    end

    subgraph TB_DMZ["TRUST BOUNDARY 2: Gateway DMZ / Ingress Controller"]
        Ingress["TLS / Ingress Termination\n(HTTPS Port 443 / 8080)"]
        RateLimiter["Rate Limiting & CORS Filter\n(GatewayCorsPolicy)"]
    end

    subgraph TB_GW["TRUST BOUNDARY 3: Gateway Processing Boundary (.NET 8 Core)"]
        subgraph HostTrustLayer["Host Network Trust Layer"]
            HostFilter["IpNetworkHelper & Host CIDR Validator\n- Validates Client IP vs AllowedCidrs\n- Returns 403 Forbidden on Mismatch"]
        end

        subgraph AbuseLayer["Abuse & Safety Guardrails"]
            InputClamp["Payload Character Clamp\n(MaxInputCharacters: 50,000)"]
            TokenClamp["Max Tokens Ceiling Clamp\n(MaxOutputTokensClamp: 4,096)"]
        end

        subgraph AuthSubsystem["Authentication & Key Lifecycle Subsystem"]
            AuthEngine["Key Authenticator\n- Fixed-Time SHA-256 Hash Comparison\n- Dual-Key Grace Period Validator\n- Stateless HMAC-SHA256 STS Validator"]
            SecService["ISecurityService & ISTSService\n- Key Pair Generator\n- App STS Token Minting & Inspection\n- Data Protection AES-256 Encryption"]
        end

        subgraph InboundGuardrails["Inbound Prompt Guardrails Engine"]
            InGuard["IGuardrailService (Prompt Evaluation)\n- PCI: Visa/MC/Amex/Discover (Luhn Check), IBAN, CVV\n- PII: US SSN, Emails, Phones, Passports\n- Secrets: AWS AKIA, Private Keys, JWTs, API Keys\n- Prompt Injection: System Overrides & DAN Jailbreaks\n- Action: Redact / Block / AuditOnly"]
        end

        subgraph OrchestrationCore["Routing & Fallback Orchestration Engine"]
            Router["IModelRouter\n- Primary Dispatcher\n- Resilient Fallback Orchestrator"]
            AppReg["IApplicationRegistryService\n- Dynamic App Configuration & Host CIDRs\n- Version History Snapshots\n- In-Memory KPI Cache"]
            AwsWorker["AwsCredentialBackgroundService\n(Proactive STS Token Refresh)"]
        end

        subgraph OutboundGuardrails["Outbound Response Guardrails Engine"]
            OutGuard["IGuardrailService (Output Evaluation)\n- Scan LLM Output for Leaked Credentials\n- Mask Leaked AWS Keys, JWTs & Cards\n- Action: Redact / Block / AuditOnly"]
        end

        subgraph AuditPipeline["Compliance & Persistent Audit Subsystem"]
            AuditChannel["Channel<AuditLogRecord>\n(High-Throughput Non-Blocking Queue)"]
            AuditWorker["AuditLogService Background Consumer\n- Daily JSONL Rolling Writer\n- Query & CSV Export Engine"]
        end
    end

    subgraph TB_AWS["TRUST BOUNDARY 4: AWS Cloud & Bedrock Zone"]
        AWS_RolesAnywhere["AWS IAM Roles Anywhere\n(X.509 Certificate-based Auth)"]
        AWS_STS["AWS Security Token Service (STS)\n(sts:AssumeRole)"]
        AWS_IAM["Target Execution IAM Role\n(arn:aws:iam::*:role/OnPremBedrockExecutionRole)"]
        AWS_Bedrock["AWS Bedrock Runtime\n- Claude 3.5 Sonnet / Haiku / Opus\n- Meta Llama 3 70B\n- Mistral / Amazon Titan"]
    end

    subgraph TB_LOCAL["TRUST BOUNDARY 5: Private VPC / Local Inference Zone"]
        Local_Ollama["Ollama Engine\n(http://localhost:11434)"]
        Local_LM["LM Studio Engine\n(http://localhost:1234)"]
        Local_LlamaCpp["llama.cpp Engine\n(http://localhost:8080)"]
    end

    subgraph TB_STORAGE["TRUST BOUNDARY 6: Persistent Storage Boundary"]
        DiskAudit["./data/audit_logs/audit_YYYYMMDD.jsonl\n(Immutable Audit Records)"]
        S3Audit["AWS S3 / Glacier Bucket\n(corp-unified-gateway-audit-prod)"]
        DiskRegistry["./data/app_registry.json\n(Application Metadata & Key Hashes)"]
        DiskKeys["./data/dataprotection-keys\n(ASP.NET Data Protection XML Keys)"]
    end

    %% Flow Connections
    ClientAppA -->|"HTTPS POST /gateway/{appId}/invoke\nX-API-Key: ug_live_..."| Ingress
    MobileClient -->|"HTTPS POST /gateway/{appId}/invoke\nAuthorization: Bearer ug_sts_..."| Ingress
    AdminUser -->|"HTTPS POST /gateway/sts/token\nExchange key for temporary token"| Ingress

    Ingress --> RateLimiter
    RateLimiter --> HostFilter
    HostFilter -->|"IP Authorized / Whitelist Passed"| InputClamp
    HostFilter -.->|"IP Rejected: Abort 403"| AuditChannel
    InputClamp --> TokenClamp
    TokenClamp --> AuthEngine
    AuthEngine --> SecService

    AuthEngine -->|"Authenticated (Primary / Secondary / STS)"| InGuard
    InGuard -.->|"If Inbound Blocked: Abort 422"| AuditChannel

    InGuard -->|"Sanitized / Verified Prompt"| Router
    Router --> AppReg
    AppReg -.-> DiskRegistry

    AwsWorker --> AWS_RolesAnywhere
    AWS_RolesAnywhere --> AWS_STS
    AWS_STS --> AWS_IAM
    Router -->|"SigV4 AWS Call (SigV4 Signed)"| AWS_Bedrock

    Router -->|"Local Backend Call"| Local_Ollama
    Router -->|"Local Backend Call"| Local_LM
    Router -->|"Local Backend Call"| Local_LlamaCpp

    AWS_Bedrock -->|"Raw Output"| OutGuard
    Local_Ollama -->|"Raw Output"| OutGuard
    Local_LM -->|"Raw Output"| OutGuard
    Local_LlamaCpp -->|"Raw Output"| OutGuard

    OutGuard -.->|"If Output Leaks Secrets (Block): Abort 422"| AuditChannel
    OutGuard -->|"Clean / Sanitized Response"| Ingress

    Router --> AuditChannel
    AuditChannel --> AuditWorker
    AuditWorker --> DiskAudit
    AuditWorker -.-> S3Audit
    SecService -.-> DiskKeys
```

---

## 5. Billing & Cost Governance Architecture

The Billing & Cost Governance subsystem calculates real-time token expenditures and financial chargeback analytics across all applications.

### 5.1 End-to-End Billing & Cost Governance Flow

```mermaid
flowchart TD
    subgraph ClientLayer["1. Client & Application Ingestion Layer"]
        AppA["Invoice Analyzer Microservice<br/>(Rate: $3.00 in / $15.00 out)"]
        AppB["Customer Support Bot<br/>(Rate: $0.80 in / $3.20 out)"]
        AppC["Internal Local Llama Worker<br/>(Rate: $0.00 in / $0.00 out)"]
        AdminUser["Cloud FinOps / Gateway Administrator"]
    end

    subgraph GatewayBoundary["2. Unified LLM Gateway (.NET 8 Core Runtime — Port 64011)"]
        Ingress["HTTP/HTTPS Ingress Router"]
        AuthModule["Host Network Trust & Key Validator"]
        ExecutionRouter["Model Router (Bedrock / Local)"]
        
        subgraph CostAccountingModule["Financial & Cost Governance Module"]
            PricingCatalog["ModelPricingCatalog<br/>(Standard Industry Reference Rates)"]
            AppRegService["IApplicationRegistryService<br/>- Stores Custom Input/Output $/1M Rates<br/>- In-Memory Request Accounting Ring"]
            BillingEngine["Billing Calculation Engine<br/>- Computes In/Out/Total Dollar Costs<br/>- Computes Spend Share Percentages"]
            AuditWriter["Async Audit Stream Channel"]
        end

        subgraph DashboardSubsystem["Built-In Admin Management UI"]
            BillingTab["Billing & Cost Governance Tab (#pane-billing)"]
            KPISummary["Spend KPI Cards (Total, In, Out, Top App)"]
            BreakdownView["Per-App Table with Visual Spend Bars"]
            CsvGenerator["CSV Report Exporter (/api/billing/export)"]
        end
    end

    subgraph ProvidersStorage["3. Providers & Persistent Storage"]
        Bedrock["AWS Bedrock (Claude 3.5, Nova, Llama)"]
        LocalEngines["Local Engines (Ollama, LM Studio)"]
        RegistryFile["./data/app_registry.json"]
        AuditDisk["./data/audit_logs/audit_YYYYMMDD.jsonl"]
    end

    AppA & AppB & AppC --> Ingress
    AdminUser -->|"View Financials / Export CSV"| DashboardSubsystem
    AdminUser -->|"Configure $/1M Rates"| Ingress

    Ingress --> AuthModule --> ExecutionRouter
    ExecutionRouter --> Bedrock & LocalEngines
    ExecutionRouter -->|"Track Token Usage"| AppRegService & AuditWriter

    AuditWriter --> AuditDisk
    AppRegService --> RegistryFile
    PricingCatalog --> AppRegService
    AppRegService --> BillingEngine
    BillingEngine --> BillingTab
    BillingTab --> KPISummary & BreakdownView & CsvGenerator
```

### 5.2 Token Pricing & Catalog Auto-Resolution

```mermaid
flowchart TD
    subgraph UI["1. Web Management Console & REST API"]
        RegisterModal["Register Application Modal"]
        EditModal["Edit Application Pricing Modal"]
        ApiRequest["REST API (POST/PUT /api/apps)"]
    end

    subgraph Catalog["2. Pricing Catalog Service (ModelPricingCatalog.cs)"]
        CatalogLookup["Lookup Provider & Model ID"]
        ClaudeRates["Anthropic Claude 3.5 Sonnet: $3.00 In / $15.00 Out ($/1M)"]
        NovaRates["Amazon Nova Pro: $0.80 In / $3.20 Out ($/1M)"]
        HaikuRates["Anthropic Claude 3.5 Haiku: $0.80 In / $4.00 Out ($/1M)"]
        LlamaRates["Meta Llama 3.2 90B: $0.72 In / $0.72 Out ($/1M)"]
        LocalRates["Local Engines (Ollama/LM Studio): $0.00 In / $0.00 Out ($/1M)"]
        CustomOverride["Custom / Negotiated Rate Card Input"]
    end

    subgraph RegistryStore["3. Application Registry & Version Snapshots"]
        AppConfig["AppConfig Entity (./data/app_registry.json)"]
        Snapshot["AppConfigSnapshot in VersionHistory (Audit Trails)"]
    end

    RegisterModal -->|"Select Model"| CatalogLookup
    CatalogLookup --> ClaudeRates & NovaRates & HaikuRates & LlamaRates & LocalRates
    ClaudeRates & NovaRates & HaikuRates & LlamaRates & LocalRates -->|"Auto-populate Default Rates"| RegisterModal
    RegisterModal -->|"Custom Input or Override"| CustomOverride
    CustomOverride --> ApiRequest
    EditModal -->|"Modify Rates"| ApiRequest
    ApiRequest -->|"Persist InputCostPerMillion & OutputCostPerMillion"| AppConfig
    AppConfig -->|"Capture Version Snapshot"| Snapshot
```

### 5.3 Financial Calculation & Aggregation Data Flow

```mermaid
flowchart TD
    subgraph RawUsage["1. Token Consumption Data Sources"]
        LogRing["In-Memory Request Logs (_recentLogs)"]
        AuditFiles["Persistent Audit Logs (audit_YYYYMMDD.jsonl)"]
        AppRegistry["Registered Applications (_apps)"]
    end

    subgraph CalculationEngine["2. Billing Engine (GetBillingSummaryAsync)"]
        InCostFormula["Input Cost Calculation:<br/><b>InputCostUsd = (InputTokens / 1,000,000) * InputCostPerMillion</b>"]
        OutCostFormula["Output Cost Calculation:<br/><b>OutputCostUsd = (OutputTokens / 1,000,000) * OutputCostPerMillion</b>"]
        TotalAppCost["Total App Spend:<br/><b>TotalCostUsd = InputCostUsd + OutputCostUsd</b>"]
        OrgAggregation["Organization Aggregation:<br/>- TotalSpendUsd = Σ TotalCostUsd<br/>- TotalInputCostUsd = Σ InputCostUsd<br/>- TotalOutputCostUsd = Σ OutputCostUsd"]
        ShareFormula["Cost Share %:<br/><b>CostSharePercentage = (TotalCostUsd / TotalSpendUsd) * 100</b>"]
        TopSpender["Identify Top Spending Application"]
    end

    subgraph Presentation["3. Financial Analytics & Reporting Presentation"]
        ReportModel["OrganizationBillingReport DTO"]
        KPICards["Dashboard Financial KPI Cards<br/>(Total Spend, In/Out Spend, Top App)"]
        TableUI["Per-App Billing Breakdown Table & Progress Bars"]
        CSVExport["Downloadable CSV Spreadsheet (/api/billing/export)"]
    end

    LogRing & AuditFiles --> InCostFormula & OutCostFormula
    AppRegistry --> InCostFormula & OutCostFormula
    InCostFormula & OutCostFormula --> TotalAppCost
    TotalAppCost --> OrgAggregation --> ShareFormula --> TopSpender
    TopSpender --> ReportModel
    ReportModel --> KPICards & TableUI & CSVExport
```

---

## 6. Verification Evidence

| Verification Area | Status | Test / Artifact Location |
| :--- | :--- | :--- |
| **Token Pricing & Admin Billing Governance** | **PASSED** | [`BillingTests.cs`](file:///c:/Users/wasim/workspace/Projects/Unified-LLM-Gateway/UnifiedGateway.Tests/BillingTests.cs), [`ApplicationRegistryService.cs`](file:///c:/Users/wasim/workspace/Projects/Unified-LLM-Gateway/Services/ApplicationRegistryService.cs) |
| **Host Network Trust & CIDR Whitelisting** | **PASSED** | [`HostNetworkTrustTests.cs`](file:///c:/Users/wasim/workspace/Projects/Unified-LLM-Gateway/UnifiedGateway.Tests/HostNetworkTrustTests.cs), [`IpNetworkHelper.cs`](file:///c:/Users/wasim/workspace/Projects/Unified-LLM-Gateway/Services/IpNetworkHelper.cs) |
| **Multi-Environment Configuration Binding** | **PASSED** | [`MultiEnvironmentTests.cs`](file:///c:/Users/wasim/workspace/Projects/Unified-LLM-Gateway/UnifiedGateway.Tests/MultiEnvironmentTests.cs) |
| **AWS IAM Roles Anywhere & Profile Auth Resolution** | **PASSED** | [`STSService.cs`](file:///c:/Users/wasim/workspace/Projects/Unified-LLM-Gateway/Services/STSService.cs), [`MultiEnvironmentTests.cs`](file:///c:/Users/wasim/workspace/Projects/Unified-LLM-Gateway/UnifiedGateway.Tests/MultiEnvironmentTests.cs) |
| **Bidirectional Guardrails (PCI/PII/Secrets/Output)** | **PASSED** | [`GuardrailServiceTests.cs`](file:///c:/Users/wasim/workspace/Projects/Unified-LLM-Gateway/UnifiedGateway.Tests/GuardrailServiceTests.cs) |
| **Full Automated Unit Test Suite** | **PASSED** | **65/65 tests passing** in `UnifiedGateway.Tests`. |
