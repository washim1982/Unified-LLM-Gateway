// Universal AI Gateway - Web Dashboard Frontend
document.addEventListener('DOMContentLoaded', () => {
  let allApps = [];
  let availableModels = { bedrock: [], local: [] };
  let activeLang = 'curl';

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
  const btnOpenCreateModal = document.getElementById('btn-open-create-modal');
  const btnCloseCreateModal = document.getElementById('btn-close-create-modal');
  const btnCancelCreate = document.getElementById('btn-cancel-create');
  const btnCloseKeyModal = document.getElementById('btn-close-key-modal');
  const btnDoneKey = document.getElementById('btn-done-key');
  const createAppForm = document.getElementById('create-app-form');
  const btnRefreshStatus = document.getElementById('btn-refresh-status');

  // Generator & Tester
  const genAppSelect = document.getElementById('gen-app-select');
  const genAppDetails = document.getElementById('gen-app-details');
  const genEndpointUrl = document.getElementById('gen-endpoint-url');
  const genCodeSnippet = document.getElementById('gen-code-snippet');
  const codeTabBtns = document.querySelectorAll('.code-tab-btn');
  const btnRunAppTest = document.getElementById('btn-run-app-test');

  // Universal
  const univProvider = document.getElementById('univ-provider');
  const univModel = document.getElementById('univ-model');
  const btnRunUnivTest = document.getElementById('btn-run-univ-test');

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
      }
    });
  });

  function updatePageHeader(tab) {
    const titles = {
      apps: { title: 'Application Registry', sub: 'Manage per-application AI routing endpoints and system prompts' },
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
        const role = data.isAssumedRole ? 'STS Assumed' : 'Direct AWS';
        const region = data.region || 'us-east-1';
        stsDetail.textContent = `${role} (${region})`;
      } else {
        stsIndicator.classList.remove('online');
        stsDetail.textContent = data.lastError ? `Error: ${data.lastError.substring(0, 30)}...` : 'Offline';
      }
    } catch (e) {
      stsIndicator.classList.remove('online');
      stsDetail.textContent = 'Service Unreachable';
    }
  }

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
            <span>Fallback:</span>
            <span>${app.fallbackModel ? escapeHtml(app.fallbackModel) : 'None'}</span>
          </div>
          <div class="app-meta-row">
            <span>Key Prefix:</span>
            <span style="font-family: var(--font-mono);">${escapeHtml(app.apiKeyPrefix || 'ug_live_***')}</span>
          </div>
        </div>
        <div class="app-card-actions">
          <button class="btn btn-primary btn-sm" onclick="selectAppForTest('${app.appId}')">Test API</button>
          <button class="btn btn-danger btn-sm" onclick="deleteApp('${app.appId}')">Delete</button>
        </div>
      `;
      container.appendChild(card);
    });
  }

  function renderGeneratorSelect(apps) {
    genAppSelect.innerHTML = '';
    apps.forEach(app => {
      const opt = document.createElement('option');
      opt.value = app.appId;
      opt.textContent = `${app.name} (${app.appId})`;
      genAppSelect.appendChild(opt);
    });

    if (apps.length > 0) {
      updateGeneratorView(apps[0]);
    }
  }

  genAppSelect.addEventListener('change', () => {
    const selected = allApps.find(a => a.appId === genAppSelect.value);
    if (selected) updateGeneratorView(selected);
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

    updateCodeSnippet(app, endpoint);
  }

  function updateCodeSnippet(app, endpoint) {
    if (activeLang === 'curl') {
      genCodeSnippet.textContent = `curl -X POST "${endpoint}" \\
  -H "Content-Type: application/json" \\
  -H "X-API-Key: YOUR_APP_API_KEY" \\
  -d '{
    "input": "User query message",
    "sessionId": "sess_abc123"
  }'`;
    } else if (activeLang === 'csharp') {
      genCodeSnippet.textContent = `using var client = new HttpClient();
client.DefaultRequestHeaders.Add("X-API-Key", "YOUR_APP_API_KEY");

var payload = new {
    input = "User query message",
    sessionId = "sess_abc123"
};

var response = await client.PostAsJsonAsync("${endpoint}", payload);
var result = await response.Content.ReadFromJsonAsync<UniversalResponse>();
Console.WriteLine(result?.Output);`;
    } else if (activeLang === 'python') {
      genCodeSnippet.textContent = `import requests

url = "${endpoint}"
headers = {
    "Content-Type": "application/json",
    "X-API-Key": "YOUR_APP_API_KEY"
}
data = {
    "input": "User query message",
    "sessionId": "sess_abc123"
}

resp = requests.post(url, headers=headers, json=data)
print(resp.json()["output"])`;
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
    document.getElementById('res-output').textContent = 'Processing request across routing layer...';

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
        document.getElementById('res-output').innerHTML = `<span style="color:var(--accent-red);">Error [${data.error.code}]: ${escapeHtml(data.error.message)}</span>`;
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

    const respArea = document.getElementById('univ-response-area');
    respArea.innerHTML = '<p>Dispatching universal request...</p>';
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

      const res = await fetch('/gateway/universal/invoke', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });

      const data = await res.json();
      respArea.innerHTML = `
        <div class="response-meta">
          <span class="meta-item">Latency: <strong>${data.latency_ms || 0} ms</strong></span>
          <span class="meta-item">Tokens: <strong>${data.tokens?.total || 0}</strong></span>
          <span class="meta-item">Provider: <strong>${data.provider}</strong></span>
        </div>
        <div class="response-output-box">
          <label>Generated Output:</label>
          <div class="response-text">${escapeHtml(data.output || data.error?.message || '')}</div>
        </div>
      `;
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
      document.getElementById('kpi-total-tokens').textContent = (data.totalTokens || 0).toLocaleString();
      document.getElementById('kpi-avg-latency').textContent = `${data.avgLatencyMs || 0} ms`;
      document.getElementById('kpi-fallbacks').textContent = data.fallbackCount || 0;

      const tbody = document.getElementById('logs-table-body');
      tbody.innerHTML = '';

      if (!data.recentLogs || data.recentLogs.length === 0) {
        tbody.innerHTML = '<tr><td colspan="7" style="text-align:center; color:var(--text-muted);">No invocation logs recorded yet.</td></tr>';
        return;
      }

      data.recentLogs.forEach(log => {
        const tr = document.createElement('tr');
        const time = new Date(log.timestamp).toLocaleTimeString();
        tr.innerHTML = `
          <td>${time}</td>
          <td><code>${escapeHtml(log.appId || 'universal')}</code></td>
          <td>${escapeHtml(log.provider || '-')}</td>
          <td style="font-family:var(--font-mono); font-size:11px;">${escapeHtml(log.model || '-')}</td>
          <td>${log.latencyMs} ms</td>
          <td>${log.totalTokens}</td>
          <td>${log.success ? '<span style="color:var(--accent-green)">Success</span>' : '<span style="color:var(--accent-red)">Error</span>'} ${log.fallbackUsed ? '<span class="badge" style="background:#f59e0b; color:#000;">Fallback</span>' : ''}</td>
        `;
        tbody.appendChild(tr);
      });
    } catch (e) {
      console.error('Failed to load metrics', e);
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

  createAppForm.addEventListener('submit', async (e) => {
    e.preventDefault();

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
      fallbackModel: document.getElementById('modal-app-fallback-model').value || null
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

      // Show Key Modal
      document.getElementById('key-modal-endpoint').value = `${window.location.origin}${created.endpointUrl}`;
      document.getElementById('key-modal-key').value = created.apiKey;
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
    alert('API Key copied to clipboard!');
  });

  btnRefreshStatus.addEventListener('click', () => {
    loadStsStatus();
    loadApps();
    loadMetrics();
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
});
