// Universal AI Gateway - Web Dashboard Frontend
document.addEventListener('DOMContentLoaded', () => {
  let allApps = [];
  let availableModels = { bedrock: [], local: [] };
  let activeLang = 'curl';
  let activeAuthMode = 'sts'; // 'sts' or 'key'
  let appStsCache = {}; // appId -> { token, expiresAt }
  let selectedAppForStsModal = null;
  let selectedAppForRotateModal = null;

  // DOM Elements
  const navButtons = document.querySelectorAll('.nav-btn');
  const tabPanes = document.querySelectorAll('.tab-pane');
  const pageHeading = document.getElementById('page-heading');
  const pageSubheading = document.getElementById('page-subheading');

  // STS status
  const stsIndicator = document.getElementById('sts-indicator');
  const stsDetail = document.getElementById('sts-detail-text');

  // Modals
  const createModal = document.getElementById('create-modal');
  const keyModal = document.getElementById('key-modal');
  const stsModal = document.getElementById('sts-modal');
  const rotateKeyModal = document.getElementById('rotate-key-modal');
  const btnOpenCreateModal = document.getElementById('btn-open-create-modal');
  const btnCloseCreateModal = document.getElementById('btn-close-create-modal');
  const btnCancelCreate = document.getElementById('btn-cancel-create');
  const btnCloseKeyModal = document.getElementById('btn-close-key-modal');
  const btnDoneKey = document.getElementById('btn-done-key');
  const btnCloseStsModal = document.getElementById('btn-close-sts-modal');
  const btnCloseStsModalBottom = document.getElementById('btn-close-sts-modal-bottom');
  const btnMintStsModal = document.getElementById('btn-mint-sts-modal');
  const btnCopyStsModalToken = document.getElementById('btn-copy-sts-modal-token');
  const btnCloseRotateModal = document.getElementById('btn-close-rotate-modal');
  const btnCloseRotateModalBottom = document.getElementById('btn-close-rotate-modal-bottom');
  const btnConfirmRotateKey = document.getElementById('btn-confirm-rotate-key');
  const btnCopyRotatedKey = document.getElementById('btn-copy-rotated-key');
  const btnRevokeSecondaryKey = document.getElementById('btn-revoke-secondary-key');
  const createAppForm = document.getElementById('create-app-form');
  const btnRefreshStatus = document.getElementById('btn-refresh-status');

  // Persistent Audit Log Elements
  const auditFilterApp = document.getElementById('audit-filter-app');
  const auditFilterStatus = document.getElementById('audit-filter-status');
  const btnRefreshAudit = document.getElementById('btn-refresh-audit');
  const btnExportAudit = document.getElementById('btn-export-audit');

  // Guardrails Elements
  const guardrailsForm = document.getElementById('guardrails-form');
  const btnRunGrTest = document.getElementById('btn-run-gr-test');

  // Generator & Tester
  const genAppSelect = document.getElementById('gen-app-select');
  const genAppDetails = document.getElementById('gen-app-details');
  const genEndpointUrl = document.getElementById('gen-endpoint-url');
  const genCodeSnippet = document.getElementById('gen-code-snippet');
  const codeTabBtns = document.querySelectorAll('.code-tab-btn');
  const btnRunAppTest = document.getElementById('btn-run-app-test');
  const btnModeSts = document.getElementById('btn-mode-sts');
  const btnModeKey = document.getElementById('btn-mode-key');
  const genAuthBadge = document.getElementById('gen-auth-badge');
  const genStsControlsPanel = document.getElementById('gen-sts-controls-panel');
  const btnMintGenSts = document.getElementById('btn-mint-gen-sts');
  const genStsTtlSelect = document.getElementById('gen-sts-ttl-select');
  const genCurrentStsInput = document.getElementById('gen-current-sts-input');
  const btnCopyGenSts = document.getElementById('btn-copy-gen-sts');
  const genStsExpiryText = document.getElementById('gen-sts-expiry-text');

  // Universal Router
  const univApiKey = document.getElementById('univ-api-key');
  const btnToggleUnivKey = document.getElementById('btn-toggle-univ-key');
  const univProvider = document.getElementById('univ-provider');
  const univModel = document.getElementById('univ-model');
  const btnRunUnivTest = document.getElementById('btn-run-univ-test');

  // Initialize stored Universal API Key
  const storedUnivKey = localStorage.getItem('ug_universal_admin_key') || 'ug-dev-admin-key';
  if (univApiKey) {
    univApiKey.value = storedUnivKey;
    univApiKey.addEventListener('input', () => {
      localStorage.setItem('ug_universal_admin_key', univApiKey.value.trim());
    });
  }

  if (btnToggleUnivKey && univApiKey) {
    btnToggleUnivKey.addEventListener('click', () => {
      if (univApiKey.type === 'password') {
        univApiKey.type = 'text';
        btnToggleUnivKey.textContent = 'Hide';
      } else {
        univApiKey.type = 'password';
        btnToggleUnivKey.textContent = 'Show';
      }
    });
  }

  // Navigation Logic
  navButtons.forEach(btn => {
    btn.addEventListener('click', () => {
      const tab = btn.dataset.tab;
      navButtons.forEach(b => b.classList.remove('active'));
      tabPanes.forEach(p => p.classList.remove('active'));

      btn.classList.add('active');
      document.getElementById(`pane-${tab}`).classList.add('active');

      updatePageHeader(tab);
      if (tab === 'telemetry') {
        loadMetrics();
      } else if (tab === 'guardrails') {
        loadGuardrailConfig();
      }
    });
  });

  function updatePageHeader(tab) {
    const titles = {
      apps: { title: 'Application Registry', sub: 'Manage per-application AI routing endpoints and system prompts' },
      guardrails: { title: 'Guardrails & Data Safety', sub: 'Admin-level PCI, PII, Secrets, and Prompt Injection policies for all requests' },
      generator: { title: 'API Generator & Test Console', sub: 'Generated REST endpoints with sample SDK code and interactive sandbox' },
      universal: { title: 'Universal Router', sub: 'Direct normalized schema invocation across Bedrock and Local engines' },
      telemetry: { title: 'Telemetry & Observability', sub: 'Real-time throughput, token analytics, latency percentiles, and request logs' }
    };
    pageHeading.textContent = titles[tab]?.title || 'Dashboard';
    pageSubheading.textContent = titles[tab]?.sub || '';
  }

  // Fetch STS Status
  async function loadStsStatus() {
    try {
      const res = await fetch('/api/credentials/status');
      if (!res.ok) throw new Error('Status check failed');
      const data = await res.json();

      if (data.isInitialized) {
        stsIndicator.classList.add('online');
        const role = data.isAssumedRole ? 'STS Assumed' : (data.profileUsed ? `Profile: ${data.profileUsed}` : 'Direct AWS');
        const region = data.region || 'us-east-1';
        stsDetail.textContent = `${role} (${region})`;
        stsDetail.title = `AWS Connected. Auth: ${data.authenticationType}`;
      } else {
        stsIndicator.classList.remove('online');
        stsDetail.textContent = 'Not Configured (DEV)';
        stsDetail.title = data.lastError || 'No AWS CLI profile or credentials found. Local offline models are available.';
      }
    } catch (e) {
      stsIndicator.classList.remove('online');
      stsDetail.textContent = 'Service Unreachable';
    }
  }

  // Guardrail Configuration & Sandbox
  async function loadGuardrailConfig() {
    try {
      const res = await fetch('/api/guardrails/config');
      if (!res.ok) return;
      const config = await res.json();

      document.getElementById('gr-enabled').value = config.enabled ? "true" : "false";
      document.getElementById('gr-mode').value = config.mode !== undefined ? config.mode : 0;
      if (document.getElementById('gr-scan-outputs')) {
        document.getElementById('gr-scan-outputs').value = config.scanOutputs ? "true" : "false";
      }
      if (document.getElementById('gr-output-mode')) {
        document.getElementById('gr-output-mode').value = config.outputMode !== undefined ? config.outputMode : 0;
      }

      // PCI
      document.getElementById('gr-pci-cc').checked = config.pci?.maskCreditCards ?? true;
      document.getElementById('gr-pci-iban').checked = config.pci?.maskIban ?? true;
      document.getElementById('gr-pci-cvv').checked = config.pci?.maskCvv ?? true;

      // PII
      document.getElementById('gr-pii-ssn').checked = config.pii?.maskSsn ?? true;
      document.getElementById('gr-pii-email').checked = config.pii?.maskEmails ?? true;
      document.getElementById('gr-pii-phone').checked = config.pii?.maskPhoneNumbers ?? true;
      document.getElementById('gr-pii-passport').checked = config.pii?.maskPassports ?? true;

      // Secrets
      document.getElementById('gr-sec-aws').checked = config.secrets?.maskAwsKeys ?? true;
      document.getElementById('gr-sec-privkey').checked = config.secrets?.maskPrivateKeys ?? true;
      document.getElementById('gr-sec-jwt').checked = config.secrets?.maskJwtTokens ?? true;
      document.getElementById('gr-sec-keys').checked = config.secrets?.maskGenericApiKeys ?? true;

      // Injection
      document.getElementById('gr-inj-override').checked = config.promptInjection?.blockSystemOverrides ?? true;
      document.getElementById('gr-inj-jailbreak').checked = config.promptInjection?.blockJailbreaks ?? true;
    } catch (e) {
      console.error('Failed to load guardrail config', e);
    }
  }

  guardrailsForm.addEventListener('submit', async (e) => {
    e.preventDefault();

    const payload = {
      enabled: document.getElementById('gr-enabled').value === "true",
      mode: parseInt(document.getElementById('gr-mode').value),
      scanOutputs: document.getElementById('gr-scan-outputs')?.value === "true",
      outputMode: parseInt(document.getElementById('gr-output-mode')?.value || "0"),
      pci: {
        enabled: true,
        maskCreditCards: document.getElementById('gr-pci-cc').checked,
        maskIban: document.getElementById('gr-pci-iban').checked,
        maskCvv: document.getElementById('gr-pci-cvv').checked
      },
      pii: {
        enabled: true,
        maskSsn: document.getElementById('gr-pii-ssn').checked,
        maskEmails: document.getElementById('gr-pii-email').checked,
        maskPhoneNumbers: document.getElementById('gr-pii-phone').checked,
        maskPassports: document.getElementById('gr-pii-passport').checked
      },
      secrets: {
        enabled: true,
        maskAwsKeys: document.getElementById('gr-sec-aws').checked,
        maskPrivateKeys: document.getElementById('gr-sec-privkey').checked,
        maskJwtTokens: document.getElementById('gr-sec-jwt').checked,
        maskGenericApiKeys: document.getElementById('gr-sec-keys').checked
      },
      promptInjection: {
        enabled: true,
        blockSystemOverrides: document.getElementById('gr-inj-override').checked,
        blockJailbreaks: document.getElementById('gr-inj-jailbreak').checked
      },
      bedrockGuardrails: {
        enabled: false,
        guardrailIdentifier: "",
        guardrailVersion: "DRAFT"
      }
    };

    try {
      const res = await fetch('/api/guardrails/config', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });

      if (res.ok) {
        alert('Enterprise Guardrail Policy saved successfully!');
      } else {
        alert('Failed to save Guardrail configuration.');
      }
    } catch (err) {
      alert(`Save error: ${err.message}`);
    }
  });

  // Guardrail Sandbox Run (Inbound vs Outbound)
  btnRunGrTest.addEventListener('click', async () => {
    const input = document.getElementById('gr-test-input').value;
    const modeVal = document.getElementById('gr-test-mode').value;
    const target = document.getElementById('gr-sandbox-target')?.value || 'input';

    btnRunGrTest.disabled = true;
    btnRunGrTest.textContent = 'Inspecting...';

    const resultBox = document.getElementById('gr-sandbox-result');
    resultBox.style.display = 'block';

    try {
      const payload = {
        input: input,
        mode: modeVal !== "" ? parseInt(modeVal) : undefined
      };

      const testEndpoint = target === 'output' ? '/api/guardrails/test-output' : '/api/guardrails/test';

      const res = await fetch(testEndpoint, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });

      const data = await res.json();

      document.getElementById('gr-res-action').textContent = data.actionTaken.toUpperCase();
      document.getElementById('gr-res-count').textContent = data.violations?.length || 0;
      document.getElementById('gr-res-risk').textContent = `${(data.riskScore * 100).toFixed(0)}%`;
      document.getElementById('gr-res-lat').textContent = `${data.latencyMs} ms`;

      const vContainer = document.getElementById('gr-res-violations-container');
      vContainer.innerHTML = '';

      if (data.violations && data.violations.length > 0) {
        data.violations.forEach(v => {
          const pill = document.createElement('div');
          pill.className = `violation-pill severity-${v.severity}`;
          pill.innerHTML = `
            <div>
              <span class="violation-title">[${v.category}] ${escapeHtml(v.ruleName)} (${v.severity})</span>
              <div class="violation-desc">${escapeHtml(v.description)} - Snippet: <code>${escapeHtml(v.detectedSnippet || '')}</code></div>
            </div>
          `;
          vContainer.appendChild(pill);
        });
      } else {
        vContainer.innerHTML = `<div style="color:var(--accent-green); font-size:13px;">No security policy violations detected in this text snippet.</div>`;
      }

      document.getElementById('gr-res-sanitized').textContent = data.sanitizedInput || '(None)';
    } catch (e) {
      document.getElementById('gr-res-sanitized').textContent = `Inspection error: ${e.message}`;
    } finally {
      btnRunGrTest.disabled = false;
      btnRunGrTest.textContent = 'Analyze & Sanitize';
    }
  });

  // Fetch Models
  async function loadModels() {
    try {
      const res = await fetch('/api/models');
      if (res.ok) {
        availableModels = await res.json();
        populateModelDropdowns();
      }
    } catch (e) {
      console.error('Error fetching models', e);
    }
  }

  function populateModelDropdowns() {
    const modalProvider = document.getElementById('modal-app-provider').value;
    const modalModelSelect = document.getElementById('modal-app-model');
    modalModelSelect.innerHTML = '';

    const models = modalProvider === 'bedrock' ? availableModels.bedrock : availableModels.local;
    models.forEach(m => {
      const opt = document.createElement('option');
      opt.value = typeof m === 'string' ? m : m.id;
      opt.textContent = typeof m === 'string' ? m : `${m.name} (${m.id})`;
      modalModelSelect.appendChild(opt);
    });

    updateUniversalModelSelect();
  }

  function updateUniversalModelSelect() {
    const provider = univProvider.value;
    univModel.innerHTML = '';
    const models = provider === 'bedrock' ? availableModels.bedrock : availableModels.local;
    models.forEach(m => {
      const opt = document.createElement('option');
      opt.value = typeof m === 'string' ? m : m.id;
      opt.textContent = typeof m === 'string' ? m : `${m.name} (${m.id})`;
      univModel.appendChild(opt);
    });
  }

  univProvider.addEventListener('change', updateUniversalModelSelect);
  document.getElementById('modal-app-provider').addEventListener('change', populateModelDropdowns);

  // Fetch Applications
  async function loadApps() {
    try {
      const res = await fetch('/api/apps');
      if (!res.ok) throw new Error('Failed to fetch apps');
      allApps = await res.json();
      renderAppCards(allApps);
      renderGeneratorSelect(allApps);
    } catch (e) {
      console.error('Error loading apps', e);
    }
  }

  function renderAppCards(apps) {
    const container = document.getElementById('apps-container');
    container.innerHTML = '';

    if (apps.length === 0) {
      container.innerHTML = `<div class="card" style="grid-column: 1/-1; text-align: center; color: var(--text-muted);">No applications registered yet. Click 'New Application' to generate one.</div>`;
      return;
    }

    apps.forEach(app => {
      let secondaryKeyHtml = '<span style="color:var(--text-muted); font-size:11px;">None</span>';
      if (app.secondaryApiKeyPrefix) {
        const isExpired = app.secondaryKeyExpiresAt && new Date(app.secondaryKeyExpiresAt) < new Date();
        if (isExpired) {
          secondaryKeyHtml = `<span class="badge" style="background:rgba(239,68,68,0.2); color:#f87171; font-size:10px;">${escapeHtml(app.secondaryApiKeyPrefix)} (Expired)</span>`;
        } else {
          const expiryDate = app.secondaryKeyExpiresAt ? new Date(app.secondaryKeyExpiresAt).toLocaleDateString() : '';
          secondaryKeyHtml = `<span class="badge" style="background:rgba(245,158,11,0.2); color:#fbbf24; font-size:10px;">${escapeHtml(app.secondaryApiKeyPrefix)} (Exp: ${expiryDate})</span>`;
        }
      }

      let hostsHtml = '<span style="color:var(--text-muted); font-size:11px;">Any Host (0.0.0.0/0)</span>';
      if (app.allowedCidrs && app.allowedCidrs.length > 0) {
        hostsHtml = `<span class="badge" style="background:rgba(59,130,246,0.15); color:#60a5fa; font-size:10px;" title="${escapeHtml(app.allowedCidrs.join(', '))}">${escapeHtml(app.allowedCidrs.slice(0, 2).join(', '))}${app.allowedCidrs.length > 2 ? ' +' + (app.allowedCidrs.length - 2) : ''}</span>`;
      }

      const card = document.createElement('div');
      card.className = 'app-card';
      card.innerHTML = `
        <div class="app-card-header">
          <div>
            <h3 class="app-title">${escapeHtml(app.name)}</h3>
            <span class="app-id-badge">${escapeHtml(app.appId)}</span>
          </div>
          <span class="badge ${app.provider === 'bedrock' ? 'badge-net' : ''}" style="background: rgba(16, 185, 129, 0.15); color: #34d399;">v${app.version}</span>
        </div>
        <p class="app-desc">${escapeHtml(app.description || 'No description provided.')}</p>
        <div class="app-meta-list">
          <div class="app-meta-row">
            <span>Provider:</span>
            <span>${app.provider.toUpperCase()}</span>
          </div>
          <div class="app-meta-row">
            <span>Model:</span>
            <span style="font-family: var(--font-mono); font-size: 11px;">${escapeHtml(app.model)}</span>
          </div>
          <div class="app-meta-row">
            <span>Primary Key:</span>
            <span style="font-family: var(--font-mono); font-size: 11px;">${escapeHtml(app.apiKeyPrefix || 'ug_live_***')}</span>
          </div>
          <div class="app-meta-row">
            <span>Secondary Grace Key:</span>
            <span>${secondaryKeyHtml}</span>
          </div>
          <div class="app-meta-row">
            <span>Authorized Hosts:</span>
            <span>${hostsHtml}</span>
          </div>
        </div>
        <div class="app-card-actions" style="display:flex; gap:6px; flex-wrap:wrap;">
          <button class="btn btn-primary btn-sm" onclick="selectAppForTest('${app.appId}')">Test API</button>
          <button class="btn btn-outline btn-sm" onclick="openStsModalForApp('${app.appId}')" style="border-color: rgba(59,130,246,0.5); color:#60a5fa;">⚡ Mint STS</button>
          <button class="btn btn-outline btn-sm" onclick="openRotateModalForApp('${app.appId}')" style="border-color: rgba(139,92,246,0.5); color:#a78bfa;">🔄 Rotate Key</button>
          <button class="btn btn-danger btn-sm" onclick="deleteApp('${app.appId}')">Delete</button>
        </div>
      `;
      container.appendChild(card);
    });
  }

  function renderGeneratorSelect(apps) {
    const prevSelected = genAppSelect.value;
    genAppSelect.innerHTML = '';
    apps.forEach(app => {
      const opt = document.createElement('option');
      opt.value = app.appId;
      opt.textContent = `${app.name} (${app.appId})`;
      genAppSelect.appendChild(opt);
    });

    if (apps.length > 0) {
      if (prevSelected && apps.some(a => a.appId === prevSelected)) {
        genAppSelect.value = prevSelected;
      }
      const selected = apps.find(a => a.appId === genAppSelect.value) || apps[0];
      updateGeneratorView(selected);
    }
  }

  genAppSelect.addEventListener('change', () => {
    const selected = allApps.find(a => a.appId === genAppSelect.value);
    if (selected) updateGeneratorView(selected);
  });

  // Auth Mode Toggles (STS vs Long-term Key)
  btnModeSts.addEventListener('click', () => {
    activeAuthMode = 'sts';
    btnModeSts.classList.add('active');
    btnModeKey.classList.remove('active');
    genAuthBadge.textContent = 'Short-Term STS Secret';
    genAuthBadge.style.background = 'rgba(139, 92, 246, 0.2)';
    genAuthBadge.style.color = '#a78bfa';
    genStsControlsPanel.style.display = 'block';
    const selected = allApps.find(a => a.appId === genAppSelect.value);
    if (selected) updateGeneratorView(selected);
  });

  btnModeKey.addEventListener('click', () => {
    activeAuthMode = 'key';
    btnModeKey.classList.add('active');
    btnModeSts.classList.remove('active');
    genAuthBadge.textContent = 'Permanent Application Key';
    genAuthBadge.style.background = 'rgba(239, 68, 68, 0.2)';
    genAuthBadge.style.color = '#f87171';
    genStsControlsPanel.style.display = 'none';
    const selected = allApps.find(a => a.appId === genAppSelect.value);
    if (selected) updateGeneratorView(selected);
  });

  // Mint STS button in generator panel
  btnMintGenSts.addEventListener('click', async () => {
    const appId = genAppSelect.value;
    if (!appId) return;

    const duration = parseInt(genStsTtlSelect.value) || 3600;
    btnMintGenSts.disabled = true;
    btnMintGenSts.textContent = 'Minting...';

    try {
      const res = await fetch(`/api/apps/${appId}/sts-token?durationSeconds=${duration}`, {
        method: 'POST'
      });
      if (!res.ok) throw new Error('Failed to mint STS token');
      const data = await res.json();

      appStsCache[appId] = {
        token: data.token,
        expiresAt: data.expiresAt
      };

      genCurrentStsInput.value = data.token;
      const expiryDate = new Date(data.expiresAt);
      genStsExpiryText.textContent = `Valid for ${Math.round(data.durationSeconds / 60)} mins (Expires: ${expiryDate.toLocaleTimeString()})`;

      const selected = allApps.find(a => a.appId === appId);
      if (selected) updateGeneratorView(selected);
    } catch (err) {
      alert(`Error minting STS token: ${err.message}`);
    } finally {
      btnMintGenSts.disabled = false;
      btnMintGenSts.textContent = 'Mint New STS Token';
    }
  });

  btnCopyGenSts.addEventListener('click', () => {
    if (!genCurrentStsInput.value) return;
    navigator.clipboard.writeText(genCurrentStsInput.value);
    alert('STS Token copied to clipboard!');
  });

  function updateGeneratorView(app) {
    const host = window.location.origin;
    const endpoint = `${host}/gateway/${app.appId}/invoke`;
    genEndpointUrl.textContent = `/gateway/${app.appId}/invoke`;

    genAppDetails.innerHTML = `
      <div class="app-meta-list" style="border:none; padding:0; margin-bottom:12px;">
        <div class="app-meta-row"><span>System Prompt:</span><span style="max-width: 250px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;">${escapeHtml(app.systemPrompt)}</span></div>
        <div class="app-meta-row"><span>Target Engine:</span><span>${app.provider.toUpperCase()} (${app.model})</span></div>
        <div class="app-meta-row"><span>Default Temp / MaxTokens:</span><span>${app.temperature} / ${app.maxTokens}</span></div>
      </div>
    `;

    // Check if we have cached STS token for this app
    const cachedSts = appStsCache[app.appId];
    if (cachedSts) {
      genCurrentStsInput.value = cachedSts.token;
      const expiry = new Date(cachedSts.expiresAt);
      genStsExpiryText.textContent = `Active STS Token (Expires: ${expiry.toLocaleTimeString()})`;
    } else {
      genCurrentStsInput.value = '';
      genStsExpiryText.textContent = 'No active STS token yet. Click "Mint New STS Token" to generate.';
    }

    updateCodeSnippet(app, endpoint);
  }

  function updateCodeSnippet(app, endpoint) {
    const host = window.location.origin;

    if (activeLang === 'curl') {
      if (activeAuthMode === 'sts') {
        genCodeSnippet.textContent = `# Step 1: Exchange Long-Term Key for Short-Term STS Token (1-hour TTL)
STS_TOKEN=$(curl -s -X POST "${host}/gateway/sts/token" \\
  -H "Content-Type: application/json" \\
  -H "X-API-Key: YOUR_APP_API_KEY" \\
  -d '{"appId": "${app.appId}", "durationSeconds": 3600}' | grep -o '"token":"[^"]*' | cut -d'"' -f4)

# Step 2: Invoke Gateway using Short-Term STS Secret
curl -X POST "${endpoint}" \\
  -H "Content-Type: application/json" \\
  -H "Authorization: Bearer $STS_TOKEN" \\
  -d '{
    "input": "User query message",
    "sessionId": "sess_abc123"
  }'`;
      } else {
        genCodeSnippet.textContent = `# Direct Invocation using Permanent Application API Key
curl -X POST "${endpoint}" \\
  -H "Content-Type: application/json" \\
  -H "X-API-Key: YOUR_APP_API_KEY" \\
  -d '{
    "input": "User query message",
    "sessionId": "sess_abc123"
  }'`;
      }
    } else if (activeLang === 'csharp') {
      if (activeAuthMode === 'sts') {
        genCodeSnippet.textContent = `using System;
using System.Net.Http;
using System.Net.Http.Json;

using var client = new HttpClient();
var gatewayUrl = "${host}";
var longTermKey = "YOUR_APP_API_KEY";
var appId = "${app.appId}";

// Step 1: Exchange Long-Term Key for Short-Term STS Token (1-hour TTL)
client.DefaultRequestHeaders.Clear();
client.DefaultRequestHeaders.Add("X-API-Key", longTermKey);

var stsReq = new { appId = appId, durationSeconds = 3600 };
var stsRes = await client.PostAsJsonAsync($"{gatewayUrl}/gateway/sts/token", stsReq);
var stsData = await stsRes.Content.ReadFromJsonAsync<AppStsTokenResponse>();
var stsToken = stsData?.Token;

// Step 2: Invoke Gateway using Short-Term STS Secret
using var invokeClient = new HttpClient();
invokeClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {stsToken}");

var payload = new {
    input = "User query message",
    sessionId = "sess_abc123"
};

var response = await invokeClient.PostAsJsonAsync($"{gatewayUrl}/gateway/{appId}/invoke", payload);
var result = await response.Content.ReadFromJsonAsync<UniversalResponse>();
Console.WriteLine(result?.Output);`;
      } else {
        genCodeSnippet.textContent = `using System;
using System.Net.Http;
using System.Net.Http.Json;

using var client = new HttpClient();
// Permanent Application API Key
client.DefaultRequestHeaders.Add("X-API-Key", "YOUR_APP_API_KEY");

var payload = new {
    input = "User query message",
    sessionId = "sess_abc123"
};

var response = await client.PostAsJsonAsync("${endpoint}", payload);
var result = await response.Content.ReadFromJsonAsync<UniversalResponse>();
Console.WriteLine(result?.Output);`;
      }
    } else if (activeLang === 'python') {
      if (activeAuthMode === 'sts') {
        genCodeSnippet.textContent = `import requests

GATEWAY_URL = "${host}"
LONG_TERM_KEY = "YOUR_APP_API_KEY"
APP_ID = "${app.appId}"

# Step 1: Exchange Long-Term Key for Short-Term STS Token (1-hour TTL)
sts_resp = requests.post(
    f"{GATEWAY_URL}/gateway/sts/token",
    headers={"Content-Type": "application/json", "X-API-Key": LONG_TERM_KEY},
    json={"appId": APP_ID, "durationSeconds": 3600}
)
sts_token = sts_resp.json()["token"]

# Step 2: Invoke Gateway using Short-Term STS Secret
invoke_resp = requests.post(
    f"{GATEWAY_URL}/gateway/{APP_ID}/invoke",
    headers={"Content-Type": "application/json", "Authorization": f"Bearer {sts_token}"},
    json={
        "input": "User query message",
        "sessionId": "sess_abc123"
    }
)

result = invoke_resp.json()
print(result.get("output"))`;
      } else {
        genCodeSnippet.textContent = `import requests

url = "${endpoint}"
# Permanent Application API Key
headers = {
    "Content-Type": "application/json",
    "X-API-Key": "YOUR_APP_API_KEY"
}
data = {
    "input": "User query message",
    "sessionId": "sess_abc123"
}

resp = requests.post(url, headers=headers, json=data)
print(resp.json().get("output"))`;
      }
    } else if (activeLang === 'java') {
      if (activeAuthMode === 'sts') {
        genCodeSnippet.textContent = `// Java 11+ Standard HttpClient (2-Step STS Flow)
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import com.fasterxml.jackson.databind.JsonNode;
import com.fasterxml.jackson.databind.ObjectMapper;

public class GatewayClient {
    public static void main(String[] args) throws Exception {
        HttpClient client = HttpClient.newHttpClient();
        ObjectMapper mapper = new ObjectMapper();

        String gatewayUrl = "${host}";
        String longTermKey = "YOUR_APP_API_KEY";
        String appId = "${app.appId}";

        // Step 1: Exchange Long-Term Key for Short-Term STS Token
        String stsPayload = String.format("""
            {
              "appId": "%s",
              "durationSeconds": 3600
            }
            """, appId);

        HttpRequest stsRequest = HttpRequest.newBuilder()
            .uri(URI.create(gatewayUrl + "/gateway/sts/token"))
            .header("Content-Type", "application/json")
            .header("X-API-Key", longTermKey)
            .POST(HttpRequest.BodyPublishers.ofString(stsPayload))
            .build();

        HttpResponse<String> stsResponse = client.send(stsRequest, HttpResponse.BodyHandlers.ofString());
        JsonNode stsJson = mapper.readTree(stsResponse.body());
        String stsToken = stsJson.get("token").asText();

        // Step 2: Invoke Gateway using Short-Term STS Secret
        String invokePayload = """
            {
              "input": "User query message",
              "sessionId": "sess_abc123"
            }
            """;

        HttpRequest invokeRequest = HttpRequest.newBuilder()
            .uri(URI.create(gatewayUrl + "/gateway/" + appId + "/invoke"))
            .header("Content-Type", "application/json")
            .header("Authorization", "Bearer " + stsToken)
            .POST(HttpRequest.BodyPublishers.ofString(invokePayload))
            .build();

        HttpResponse<String> invokeResponse = client.send(invokeRequest, HttpResponse.BodyHandlers.ofString());
        System.out.println(invokeResponse.body());
    }
}`;
      } else {
        genCodeSnippet.textContent = `// Java 11+ Standard HttpClient
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;

public class GatewayClient {
    public static void main(String[] args) throws Exception {
        HttpClient client = HttpClient.newHttpClient();
        String jsonPayload = """
            {
              "input": "User query message",
              "sessionId": "sess_abc123"
            }
            """;

        // Permanent Application API Key
        HttpRequest request = HttpRequest.newBuilder()
            .uri(URI.create("${endpoint}"))
            .header("Content-Type", "application/json")
            .header("X-API-Key", "YOUR_APP_API_KEY")
            .POST(HttpRequest.BodyPublishers.ofString(jsonPayload))
            .build();

        HttpResponse<String> response = client.send(request, HttpResponse.BodyHandlers.ofString());
        System.out.println(response.body());
    }
}`;
      }
    } else if (activeLang === 'powershell') {
      if (activeAuthMode === 'sts') {
        genCodeSnippet.textContent = `# Step 1: Exchange Long-Term Key for Short-Term STS Token
$stsResp = Invoke-RestMethod -Uri "${host}/gateway/sts/token" \`
    -Method Post \`
    -Headers @{
        "Content-Type" = "application/json"
        "X-API-Key"    = "YOUR_APP_API_KEY"
    } \`
    -Body (@{ "appId" = "${app.appId}"; "durationSeconds" = 3600 } | ConvertTo-Json)

# Step 2: Use the token directly from the response object
$headers = @{
    "Content-Type"  = "application/json"
    "Authorization" = "Bearer $($stsResp.token)"
}

$body = @{
    "input"     = "User query message"
    "sessionId" = "sess_abc123"
} | ConvertTo-Json

$response = Invoke-RestMethod -Uri "${endpoint}" \`
    -Method Post \`
    -Headers $headers \`
    -Body $body

Write-Output $response.output`;
      } else {
        genCodeSnippet.textContent = `# PowerShell - Invoke-RestMethod (Permanent Application API Key)
$headers = @{
    "Content-Type" = "application/json"
    "X-API-Key"    = "YOUR_APP_API_KEY"
}

$body = @{
    "input"     = "User query message"
    "sessionId" = "sess_abc123"
} | ConvertTo-Json

$response = Invoke-RestMethod -Uri "${endpoint}" \`
    -Method Post \`
    -Headers $headers \`
    -Body $body

Write-Output $response.output`;
      }
    }
  }

  codeTabBtns.forEach(b => {
    b.addEventListener('click', () => {
      codeTabBtns.forEach(x => x.classList.remove('active'));
      b.classList.add('active');
      activeLang = b.dataset.lang;
      const selected = allApps.find(a => a.appId === genAppSelect.value);
      if (selected) updateGeneratorView(selected);
    });
  });

  // App Invocation Test
  btnRunAppTest.addEventListener('click', async () => {
    const appId = genAppSelect.value;
    if (!appId) return;

    const input = document.getElementById('test-input').value;
    const session = document.getElementById('test-session').value;
    const temp = document.getElementById('test-temp').value;

    btnRunAppTest.disabled = true;
    btnRunAppTest.textContent = 'Invoking Gateway...';

    const respArea = document.getElementById('app-test-response-area');
    respArea.style.display = 'block';
    document.getElementById('res-output').textContent = 'Processing request across guardrails & routing layer...';

    try {
      const payload = {
        input: input,
        sessionId: session || undefined,
        temperature: temp ? parseFloat(temp) : undefined
      };

      const res = await fetch(`/api/apps/${appId}/test`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });

      const data = await res.json();

      document.getElementById('res-latency').textContent = `${data.latency_ms || 0} ms`;
      document.getElementById('res-tokens').textContent = `${data.tokens?.total || 0} (in: ${data.tokens?.input || 0}, out: ${data.tokens?.output || 0})`;
      document.getElementById('res-provider').textContent = data.provider || 'unknown';
      document.getElementById('res-fallback').textContent = data.fallback_used ? 'YES' : 'NO';

      if (data.error) {
        document.getElementById('res-output').innerHTML = `<span style="color:var(--accent-red);">Error [${data.error.code}]: ${escapeHtml(data.error.message)} ${data.error.details ? '<br/><small>' + escapeHtml(data.error.details) + '</small>' : ''}</span>`;
      } else {
        document.getElementById('res-output').textContent = data.output || '(Empty response)';
      }
    } catch (e) {
      document.getElementById('res-output').textContent = `Invocation failed: ${e.message}`;
    } finally {
      btnRunAppTest.disabled = false;
      btnRunAppTest.innerHTML = `<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polygon points="5 3 19 12 5 21 5 3"/></svg> Execute Invocations`;
    }
  });

  // Universal Invocation Test
  btnRunUnivTest.addEventListener('click', async () => {
    const provider = univProvider.value;
    const model = univModel.value;
    const system = document.getElementById('univ-system').value;
    const input = document.getElementById('univ-input').value;
    const temp = parseFloat(document.getElementById('univ-temp').value) || 0.7;
    const maxTokens = parseInt(document.getElementById('univ-tokens').value) || 1024;
    const adminKeyOrToken = univApiKey?.value?.trim() || '';

    const respArea = document.getElementById('univ-response-area');
    respArea.innerHTML = '<p>Dispatching universal request through guardrails...</p>';
    btnRunUnivTest.disabled = true;

    try {
      const payload = {
        model,
        provider,
        system,
        input,
        temperature: temp,
        max_tokens: maxTokens
      };

      const headers = {
        'Content-Type': 'application/json'
      };

      if (adminKeyOrToken) {
        headers['X-API-Key'] = adminKeyOrToken;
      }

      const res = await fetch('/gateway/universal/invoke', {
        method: 'POST',
        headers: headers,
        body: JSON.stringify(payload)
      });

      const data = await res.json();
      
      if (data.error) {
        respArea.innerHTML = `
          <div class="response-meta">
            <span class="meta-item">Status: <strong style="color:var(--accent-red)">Unauthorized / Error</strong></span>
          </div>
          <div class="response-output-box">
            <label>Error Details:</label>
            <div class="response-text" style="color:var(--accent-red)">[${escapeHtml(data.error.code || 'ERROR')}]: ${escapeHtml(data.error.message || '')}</div>
          </div>
        `;
      } else {
        respArea.innerHTML = `
          <div class="response-meta">
            <span class="meta-item">Latency: <strong>${data.latency_ms || 0} ms</strong></span>
            <span class="meta-item">Tokens: <strong>${data.tokens?.total || 0}</strong></span>
            <span class="meta-item">Provider: <strong>${data.provider}</strong></span>
          </div>
          <div class="response-output-box">
            <label>Generated Output:</label>
            <div class="response-text">${escapeHtml(data.output || '')}</div>
          </div>
        `;
      }
    } catch (e) {
      respArea.innerHTML = `<div style="color:var(--accent-red)">Request failed: ${escapeHtml(e.message)}</div>`;
    } finally {
      btnRunUnivTest.disabled = false;
    }
  });

  // Telemetry & Metrics
  async function loadMetrics() {
    try {
      const res = await fetch('/api/metrics');
      if (!res.ok) return;
      const data = await res.json();

      document.getElementById('kpi-total-req').textContent = data.totalRequests || 0;
      document.getElementById('kpi-gr-redacted').textContent = data.guardrailRedactedCount || 0;
      document.getElementById('kpi-gr-blocked').textContent = data.guardrailBlockedCount || 0;
      document.getElementById('kpi-total-tokens').textContent = (data.totalTokens || 0).toLocaleString();

      const tbody = document.getElementById('logs-table-body');
      tbody.innerHTML = '';

      if (!data.recentLogs || data.recentLogs.length === 0) {
        tbody.innerHTML = '<tr><td colspan="8" style="text-align:center; color:var(--text-muted);">No invocation logs recorded yet.</td></tr>';
        return;
      }

      data.recentLogs.forEach(log => {
        const tr = document.createElement('tr');
        const time = new Date(log.timestamp).toLocaleTimeString();
        const grBadge = log.guardrailAction === 'Redacted' ? '<span class="badge" style="background:#3b82f6; color:#fff;">Redacted</span>' :
                        log.guardrailAction === 'Blocked' ? '<span class="badge" style="background:#ef4444; color:#fff;">Blocked</span>' :
                        '<span class="badge" style="background:rgba(255,255,255,0.1); color:var(--text-muted);">Passed</span>';

        tr.innerHTML = `
          <td>${time}</td>
          <td><code>${escapeHtml(log.appId || 'universal')}</code></td>
          <td>${escapeHtml(log.provider || '-')}</td>
          <td style="font-family:var(--font-mono); font-size:11px;">${escapeHtml(log.model || '-')}</td>
          <td>${grBadge}</td>
          <td>${log.latencyMs} ms</td>
          <td>${log.totalTokens}</td>
          <td>${log.success ? '<span style="color:var(--accent-green)">Success</span>' : '<span style="color:var(--accent-red)">Error</span>'} ${log.fallbackUsed ? '<span class="badge" style="background:#f59e0b; color:#000;">Fallback</span>' : ''}</td>
        `;
        tbody.appendChild(tr);
      });

      await loadPersistentAuditLogs();
    } catch (e) {
      console.error('Failed to load metrics', e);
    }
  }

  // Persistent Audit Log Loader
  async function loadPersistentAuditLogs() {
    try {
      const selectedApp = auditFilterApp ? auditFilterApp.value : '';
      const selectedStatus = auditFilterStatus ? auditFilterStatus.value : '';

      // Populate app dropdown in audit filters if needed
      if (auditFilterApp && auditFilterApp.options.length <= 1 && allApps.length > 0) {
        allApps.forEach(a => {
          const opt = document.createElement('option');
          opt.value = a.appId;
          opt.textContent = `${a.name} (${a.appId})`;
          auditFilterApp.appendChild(opt);
        });
      }

      let url = '/api/audit/logs?limit=50';
      if (selectedApp) url += `&appId=${encodeURIComponent(selectedApp)}`;
      if (selectedStatus) url += `&status=${encodeURIComponent(selectedStatus)}`;

      const res = await fetch(url);
      if (!res.ok) return;
      const data = await res.json();

      const tbody = document.getElementById('audit-table-body');
      if (!tbody) return;
      tbody.innerHTML = '';

      if (!data.records || data.records.length === 0) {
        tbody.innerHTML = '<tr><td colspan="9" style="text-align:center; color:var(--text-muted);">No persistent audit records matching query.</td></tr>';
        return;
      }

      data.records.forEach(r => {
        const tr = document.createElement('tr');
        const time = new Date(r.timestamp).toLocaleString();
        const inBadge = r.inputGuardrailAction === 'Redacted' ? '<span class="badge" style="background:#3b82f6; color:#fff;">In: Redacted</span>' :
                        r.inputGuardrailAction === 'Blocked' ? '<span class="badge" style="background:#ef4444; color:#fff;">In: Blocked</span>' :
                        '<span class="badge" style="background:rgba(255,255,255,0.08); color:var(--text-muted);">In: Passed</span>';

        const outBadge = r.outputGuardrailAction === 'Redacted' ? '<span class="badge" style="background:#8b5cf6; color:#fff;">Out: Redacted</span>' :
                         r.outputGuardrailAction === 'Blocked' ? '<span class="badge" style="background:#ef4444; color:#fff;">Out: Blocked</span>' :
                         r.outputGuardrailAction === 'None' ? '<span class="badge" style="background:rgba(255,255,255,0.05); color:var(--text-muted);">-</span>' :
                         '<span class="badge" style="background:rgba(255,255,255,0.08); color:var(--text-muted);">Out: Passed</span>';

        const statusBadge = r.success ? '<span style="color:var(--accent-green)">200 OK</span>' :
                            r.statusCode === 422 ? '<span style="color:#f87171;">422 Blocked</span>' :
                            `<span style="color:var(--accent-red);">${r.statusCode || 500} Error</span>`;

        tr.innerHTML = `
          <td><code style="font-size:10px;">${escapeHtml(r.auditId ? r.auditId.substring(0, 8) : '-')}...</code></td>
          <td style="font-size:11px; white-space:nowrap;">${time}</td>
          <td><code>${escapeHtml(r.appId || 'direct')}</code></td>
          <td style="font-size:11px;">${escapeHtml(r.provider)} / ${escapeHtml(r.model)}</td>
          <td>${inBadge}</td>
          <td>${outBadge}</td>
          <td>${r.totalTokens}</td>
          <td>${r.latencyMs} ms</td>
          <td>${statusBadge}</td>
        `;
        tbody.appendChild(tr);
      });
    } catch (e) {
      console.error('Failed to load persistent audit logs', e);
    }
  }

  // Modals handling
  btnOpenCreateModal.addEventListener('click', () => {
    createModal.classList.add('active');
  });

  btnCloseCreateModal.addEventListener('click', () => createModal.classList.remove('active'));
  btnCancelCreate.addEventListener('click', () => createModal.classList.remove('active'));
  btnCloseKeyModal.addEventListener('click', () => keyModal.classList.remove('active'));
  btnDoneKey.addEventListener('click', () => keyModal.classList.remove('active'));

  if (btnCloseStsModal) btnCloseStsModal.addEventListener('click', () => stsModal.classList.remove('active'));
  if (btnCloseStsModalBottom) btnCloseStsModalBottom.addEventListener('click', () => stsModal.classList.remove('active'));

  // Create App Form Submission
  createAppForm.addEventListener('submit', async (e) => {
    e.preventDefault();

    const cidrsInput = document.getElementById('modal-app-cidrs')?.value || '';
    const allowedCidrs = cidrsInput
      .split(',')
      .map(s => s.trim())
      .filter(s => s.length > 0);

    const payload = {
      appId: document.getElementById('modal-app-id').value,
      name: document.getElementById('modal-app-name').value,
      description: document.getElementById('modal-app-desc').value,
      provider: document.getElementById('modal-app-provider').value,
      model: document.getElementById('modal-app-model').value,
      systemPrompt: document.getElementById('modal-app-prompt').value,
      temperature: parseFloat(document.getElementById('modal-app-temp').value),
      maxTokens: parseInt(document.getElementById('modal-app-tokens').value),
      fallbackProvider: document.getElementById('modal-app-fallback-provider').value || null,
      fallbackModel: document.getElementById('modal-app-fallback-model').value || null,
      allowedCidrs: allowedCidrs
    };

    try {
      const res = await fetch('/api/apps', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });

      if (!res.ok) {
        const err = await res.json();
        alert(`Error: ${err.error || 'Failed to create app'}`);
        return;
      }

      const created = await res.json();
      createModal.classList.remove('active');
      createAppForm.reset();

      // Cache initial STS token
      if (created.stsToken) {
        appStsCache[created.app.appId] = {
          token: created.stsToken,
          expiresAt: created.stsExpiresAt
        };
      }

      // Show Key & STS Modal
      document.getElementById('key-modal-endpoint').value = `${window.location.origin}${created.endpointUrl}`;
      document.getElementById('key-modal-key').value = created.apiKey;
      document.getElementById('key-modal-sts').value = created.stsToken || '(STS Generated)';
      keyModal.classList.add('active');

      await loadApps();
    } catch (err) {
      alert(`Submission error: ${err.message}`);
    }
  });

  document.getElementById('btn-copy-key').addEventListener('click', () => {
    const keyInput = document.getElementById('key-modal-key');
    keyInput.select();
    navigator.clipboard.writeText(keyInput.value);
    alert('Permanent API Key copied to clipboard!');
  });

  document.getElementById('btn-copy-sts').addEventListener('click', () => {
    const stsInput = document.getElementById('key-modal-sts');
    stsInput.select();
    navigator.clipboard.writeText(stsInput.value);
    alert('Short Temporary Secret (STS token) copied to clipboard!');
  });

  // On-demand STS Modal Actions
  window.openStsModalForApp = (appId) => {
    const app = allApps.find(a => a.appId === appId);
    if (!app) return;

    selectedAppForStsModal = app;
    document.getElementById('sts-modal-app-name').value = `${app.name} (${app.appId})`;
    document.getElementById('sts-modal-result').style.display = 'none';
    stsModal.classList.add('active');
  };

  if (btnMintStsModal) {
    btnMintStsModal.addEventListener('click', async () => {
      if (!selectedAppForStsModal) return;

      const duration = parseInt(document.getElementById('sts-modal-duration').value) || 3600;
      btnMintStsModal.disabled = true;
      btnMintStsModal.textContent = 'Minting STS Token...';

      try {
        const res = await fetch(`/api/apps/${selectedAppForStsModal.appId}/sts-token?durationSeconds=${duration}`, {
          method: 'POST'
        });

        if (!res.ok) throw new Error('Failed to mint STS token');
        const data = await res.json();

        appStsCache[selectedAppForStsModal.appId] = {
          token: data.token,
          expiresAt: data.expiresAt
        };

        document.getElementById('sts-modal-token-output').value = data.token;
        const expiry = new Date(data.expiresAt);
        document.getElementById('sts-modal-meta').textContent = `Expires at: ${expiry.toLocaleString()} (${Math.round(data.durationSeconds / 60)} minutes TTL)`;
        document.getElementById('sts-modal-result').style.display = 'block';

        // Update generator if this app is selected
        if (genAppSelect.value === selectedAppForStsModal.appId) {
          updateGeneratorView(selectedAppForStsModal);
        }
      } catch (err) {
        alert(`Error: ${err.message}`);
      } finally {
        btnMintStsModal.disabled = false;
        btnMintStsModal.textContent = 'Mint STS Token';
      }
    });
  }

  if (btnCopyStsModalToken) {
    btnCopyStsModalToken.addEventListener('click', () => {
      const tokenInput = document.getElementById('sts-modal-token-output');
      tokenInput.select();
      navigator.clipboard.writeText(tokenInput.value);
      alert('STS Token copied to clipboard!');
    });
  }

  // Key Rotation Modal Handlers
  window.openRotateModalForApp = (appId) => {
    const app = allApps.find(a => a.appId === appId);
    if (!app) return;

    selectedAppForRotateModal = app;
    document.getElementById('rotate-modal-app-name').value = `${app.name} (${app.appId})`;
    document.getElementById('rotate-modal-result').style.display = 'none';
    rotateKeyModal.classList.add('active');
  };

  if (btnCloseRotateModal) btnCloseRotateModal.addEventListener('click', () => rotateKeyModal.classList.remove('active'));
  if (btnCloseRotateModalBottom) btnCloseRotateModalBottom.addEventListener('click', () => rotateKeyModal.classList.remove('active'));

  if (btnConfirmRotateKey) {
    btnConfirmRotateKey.addEventListener('click', async () => {
      if (!selectedAppForRotateModal) return;

      const graceDays = parseInt(document.getElementById('rotate-modal-grace-days').value) || 7;
      btnConfirmRotateKey.disabled = true;
      btnConfirmRotateKey.textContent = 'Rotating Key...';

      try {
        const res = await fetch(`/api/apps/${selectedAppForRotateModal.appId}/rotate-key`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ gracePeriodDays: graceDays })
        });

        if (!res.ok) throw new Error('Key rotation failed');
        const data = await res.json();

        document.getElementById('rotate-modal-new-key').value = data.newApiKey;
        const expiry = data.secondaryKeyExpiresAt ? new Date(data.secondaryKeyExpiresAt).toLocaleString() : '';
        document.getElementById('rotate-modal-secondary-info').innerHTML = `
          <strong>Previous Key (${escapeHtml(data.secondaryKeyPrefix || 'ug_live_...')})</strong> remains active until <strong>${expiry}</strong> (${graceDays} days grace period).
        `;
        document.getElementById('rotate-modal-result').style.display = 'block';

        await loadApps();
      } catch (err) {
        alert(`Rotation error: ${err.message}`);
      } finally {
        btnConfirmRotateKey.disabled = false;
        btnConfirmRotateKey.textContent = 'Rotate Key Now';
      }
    });
  }

  if (btnCopyRotatedKey) {
    btnCopyRotatedKey.addEventListener('click', () => {
      const keyInput = document.getElementById('rotate-modal-new-key');
      keyInput.select();
      navigator.clipboard.writeText(keyInput.value);
      alert('New Primary API Key copied to clipboard!');
    });
  }

  if (btnRevokeSecondaryKey) {
    btnRevokeSecondaryKey.addEventListener('click', async () => {
      if (!selectedAppForRotateModal) return;
      if (!confirm(`Are you sure you want to immediately revoke the secondary key for '${selectedAppForRotateModal.appId}'? Running clients using the old key will fail immediately.`)) return;

      try {
        const res = await fetch(`/api/apps/${selectedAppForRotateModal.appId}/revoke-secondary-key`, {
          method: 'POST'
        });

        if (res.ok) {
          alert('Secondary key revoked successfully.');
          document.getElementById('rotate-modal-secondary-info').innerHTML = `<span style="color:#f87171;">Secondary key revoked. Only the new primary key is active.</span>`;
          await loadApps();
        } else {
          alert('Failed to revoke secondary key.');
        }
      } catch (err) {
        alert(`Revocation error: ${err.message}`);
      }
    });
  }

  // Audit Filter & Export Listeners
  if (auditFilterApp) auditFilterApp.addEventListener('change', loadPersistentAuditLogs);
  if (auditFilterStatus) auditFilterStatus.addEventListener('change', loadPersistentAuditLogs);
  if (btnRefreshAudit) btnRefreshAudit.addEventListener('click', loadPersistentAuditLogs);

  if (btnExportAudit) {
    btnExportAudit.addEventListener('click', () => {
      const app = auditFilterApp ? auditFilterApp.value : '';
      const status = auditFilterStatus ? auditFilterStatus.value : '';
      let exportUrl = '/api/audit/export?';
      if (app) exportUrl += `appId=${encodeURIComponent(app)}&`;
      if (status) exportUrl += `status=${encodeURIComponent(status)}&`;
      window.location.href = exportUrl;
    });
  }

  btnRefreshStatus.addEventListener('click', () => {
    loadStsStatus();
    loadApps();
    loadMetrics();
    loadGuardrailConfig();
  });

  // Global window functions for cards
  window.selectAppForTest = (appId) => {
    navButtons.forEach(b => {
      if (b.dataset.tab === 'generator') b.click();
    });
    genAppSelect.value = appId;
    const selected = allApps.find(a => a.appId === appId);
    if (selected) updateGeneratorView(selected);
  };

  window.deleteApp = async (appId) => {
    if (!confirm(`Are you sure you want to delete application '${appId}'?`)) return;
    try {
      const res = await fetch(`/api/apps/${appId}`, { method: 'DELETE' });
      if (res.ok) {
        await loadApps();
      }
    } catch (e) {
      alert('Delete failed');
    }
  };

  function escapeHtml(str) {
    if (!str) return '';
    return str.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&#039;");
  }

  // Initial Load
  loadStsStatus();
  loadModels();
  loadApps();
  loadGuardrailConfig();
});
