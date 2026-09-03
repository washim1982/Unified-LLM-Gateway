# Billing & Cost Governance Architecture

**Project:** Universal AI LLM Gateway  
**Document:** Billing & Cost Governance Technical Architecture Specification  
**Version:** 1.0.0  
**Status:** Approved & Implemented  

---

## 1. Overview & Objective

The **Billing & Cost Governance Subsystem** provides real-time financial tracking, token usage metering, customizable rate cards, and organization-wide chargeback analytics for all LLM workloads routed through the Unified LLM Gateway.

### Core Capabilities:
1. **Per-Application Dual-Rate Metering:** Separate configurable pricing for **Input Prompt Tokens** and **Output Generation Tokens** expressed in dollars per one million tokens (`$ / 1,000,000 tokens`).
2. **Standard Reference Catalog Pricing:** Out-of-the-box reference pricing auto-populated from foundation model catalogs (Claude 3.5 Sonnet/Haiku, Nova Pro/Lite, Llama 3.2, and Local models at $0.00).
3. **Custom & Negotiated Rate Overrides:** Fully editable text boxes allow enterprise teams to configure negotiated enterprise discounts or customized internal chargeback rates.
4. **Real-Time Financial KPI Cards:** Instant visibility into Total Organization Spend, Total Input Cost, Total Output Cost, and Top Spending Application.
5. **Per-Application Breakdown with Spend Share:** Real-time visibility into token volumes, configured rate cards, dollar amounts, and visual spend share progress bars.
6. **1-Click CSV Financial Export:** Instant export of full billing tables to CSV for accounting, finance, and ERP ingestion.
7. **Zero-Downtime Rate Modifications:** Update rates on running applications at any time; changes take effect immediately across all reporting surfaces.

---

## 2. End-to-End Billing & Cost Governance Architecture

```mermaid
flowchart TD
    subgraph ClientLayer["1. Client & Application Ingestion Layer"]
        AppA["Invoice Analyzer Microservice<br/>(Configured Rate: $3.00 in / $15.00 out)"]
        AppB["Customer Support Bot<br/>(Configured Rate: $0.80 in / $3.20 out)"]
        AppC["Internal Local Llama Worker<br/>(Configured Rate: $0.00 in / $0.00 out)"]
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

---

## 3. Token Pricing Model & Catalog Auto-Resolution

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

---

## 4. Runtime Invocation & Token Metering Sequence

```mermaid
sequenceDiagram
    autonumber
    actor Client as Client Microservice
    participant Gateway as Unified LLM Gateway
    participant Guardrails as Bidirectional Guardrails
    participant Router as Model Router (Bedrock / Local)
    participant Bedrock as AWS Bedrock / Local Engine
    participant Registry as Application Registry Service
    participant Audit as Async Audit Channel (.jsonl)

    Client->>Gateway: POST /gateway/{appId}/invoke (Prompt Payload)
    Gateway->>Guardrails: Inbound Security & Safety Scan
    Guardrails->>Router: Forward Sanitized Prompt
    Router->>Bedrock: Invoke Model (SigV4 Signed)
    Bedrock-->>Router: Response (Text Output + Input/Output Tokens)
    Router->>Guardrails: Outbound Leakage Scan
    Guardrails-->>Gateway: Sanitized Response Output
    Gateway-->>Client: 200 OK (Response Payload + Token Metadata)

    par Asynchronous Accounting & Metering
        Gateway->>Registry: RecordMetricAsync(RequestLogEntry: inTokens, outTokens)
        Registry->>Registry: Buffer In-Memory Log Ring (Recent Invocations)
        Gateway->>Audit: Stream AuditLogRecord(InputTokens, OutputTokens, AppId)
        Audit->>Audit: Append to ./data/audit_logs/audit_YYYYMMDD.jsonl
    end
```

---

## 5. Financial Calculation & Aggregation Data Flow

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

## 6. Mathematical Formulas & Rounding Rules

1. **Input Prompt Cost Calculation:**
   $$\text{InputCostUsd} = \text{round}\left( \frac{\text{InputTokens}}{1,000,000} \times \text{InputCostPerMillion}, 6 \right)$$

2. **Output Generation Cost Calculation:**
   $$\text{OutputCostUsd} = \text{round}\left( \frac{\text{OutputTokens}}{1,000,000} \times \text{OutputCostPerMillion}, 6 \right)$$

3. **Per-Application Total Cost:**
   $$\text{TotalCostUsd} = \text{InputCostUsd} + \text{OutputCostUsd}$$

4. **Organization Aggregate Spend:**
   $$\text{TotalSpendUsd} = \sum_{i=1}^{N} \text{TotalCostUsd}_i$$

5. **Spend Share Percentage:**
   $$\text{CostSharePercentage} = \begin{cases} \text{round}\left( \frac{\text{TotalCostUsd}}{\text{TotalSpendUsd}} \times 100, 2 \right), & \text{if } \text{TotalSpendUsd} > 0 \\ 0.0, & \text{otherwise} \end{cases}$$

---

## 7. Data Models & Interface Contracts

### 7.1 `AppBillingSummary` Model
```csharp
public sealed class AppBillingSummary
{
    public string AppId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public long TotalRequests { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens => InputTokens + OutputTokens;
    public double InputCostPerMillion { get; set; }
    public double OutputCostPerMillion { get; set; }
    public decimal InputCostUsd { get; set; }
    public decimal OutputCostUsd { get; set; }
    public decimal TotalCostUsd { get; set; }
    public double CostSharePercentage { get; set; }
    public DateTimeOffset? LastInvokedAt { get; set; }
    public bool IsActive { get; set; } = true;
}
```

### 7.2 `OrganizationBillingReport` Model
```csharp
public sealed class OrganizationBillingReport
{
    public decimal TotalSpendUsd { get; set; }
    public decimal TotalInputCostUsd { get; set; }
    public decimal TotalOutputCostUsd { get; set; }
    public long TotalTokens { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public long TotalRequests { get; set; }
    public string HighestSpendingAppId { get; set; } = string.Empty;
    public string HighestSpendingAppName { get; set; } = string.Empty;
    public decimal HighestSpendingAppAmountUsd { get; set; }
    public List<AppBillingSummary> AppBills { get; set; } = new();
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

---

## 8. REST API Endpoints

| Method | Route | Description | Response Content |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/billing` | Retrieves the complete organization billing report and per-app cost breakdown. | `application/json` (`OrganizationBillingReport`) |
| `GET` | `/api/billing/export` | Downloads the current billing report as a standard formatted CSV attachment. | `text/csv` (`billing_report_YYYYMMDD_HHmmss.csv`) |
| `POST` | `/api/apps` | Registers a new application with custom `InputCostPerMillion` and `OutputCostPerMillion`. | `application/json` (`AppRegistrationResponse`) |
| `PUT` | `/api/apps/{appId}` | Updates configuration and token pricing rates for an existing registered application. | `application/json` (`AppConfig`) |
