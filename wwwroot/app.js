const $ = (selector) => document.querySelector(selector);
const state = {
  models: [],
  messages: [],
  busy: false,
  reportBusy: false,
  connected: false,
  reportDatabasesLoaded: false,
  databaseProfiles: [],
  databaseGuidanceProfileId: null,
  databaseTodoItems: [],
  databaseTestPrompts: [],
  databaseGenerationBusy: false,
  databaseGuidanceSaving: false
};
const api = async (path, options = {}) => {
  const response = await fetch(path, options);
  if (!response.ok) {
    const text = await response.text();
    let message = text;
    try {
      const problem = JSON.parse(text);
      message = [problem.title, problem.detail || problem.error].filter(Boolean).join(': ') || text;
    } catch { /* Keep the original response text. */ }
    throw new Error(message || `Request failed (${response.status})`);
  }
  return response;
};

function toast(message, error = false) {
  const item = document.createElement('div');
  item.className = `toast${error ? ' error' : ''}`;
  item.textContent = message;
  $('#toastRegion').append(item);
  setTimeout(() => item.remove(), 4200);
}

function formatBytes(bytes) {
  if (!Number.isFinite(bytes) || bytes < 0) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}

function safeText(value, fallback = '—') {
  return typeof value === 'string' && value.length ? value : fallback;
}

async function loadHealth() {
  try {
    const response = await fetch('/api/health');
    const info = await response.json();
    state.connected = response.ok;
    $('#statusDot').className = `status-dot ${state.connected ? 'connected' : 'disconnected'}`;
    $('#connectionText').textContent = state.connected ? 'Ollama connected' : 'Ollama unavailable';
    $('#connectionUrl').textContent = state.connected ? `v${info.version || ''}` : (info.error || 'Check Ollama is running');
    if (!state.connected) $('#connectionUrl').title = info.error || '';
  } catch {
    state.connected = false;
    $('#statusDot').className = 'status-dot disconnected';
    $('#connectionText').textContent = 'Ollama unavailable';
    $('#connectionUrl').textContent = 'Check Ollama is running';
  }
}

async function loadModels() {
  try {
    const data = await (await api('/api/models')).json();
    state.models = Array.isArray(data.models) ? data.models : [];
    renderModels();
    if (!state.models.length) $('#connectionText').textContent = state.connected ? 'No models yet' : 'Ollama unavailable';
  } catch (error) {
    $('#sidebarModels').innerHTML = '';
    const empty = document.createElement('div');
    empty.className = 'side-empty';
    empty.textContent = 'Could not load models.';
    $('#sidebarModels').append(empty);
    toast(error.message, true);
  }
}

function renderModels() {
  const select = $('#chatModel');
  const previous = select.value;
  const reportSelect = $('#reportModel');
  const previousReportModel = reportSelect.value;
  const safetySettingsModel = $('#databaseSafetySettingsModel');
  const previousSafetyModel = safetySettingsModel.value;
  select.replaceChildren(new Option(state.models.length ? 'Select a model…' : 'Pull a model to get started…', ''));
  reportSelect.replaceChildren(new Option(state.models.length ? 'Select a model…' : 'Pull a model to get started…', ''));
  safetySettingsModel.replaceChildren(new Option(state.models.length ? 'Select a model…' : 'No models available', ''));
  $('#sidebarModels').replaceChildren();
  for (const model of state.models) {
    const name = safeText(model.name || model.model);
    select.add(new Option(name, model.name || model.model));
    reportSelect.add(new Option(name, model.name || model.model));
    safetySettingsModel.add(new Option(name, model.name || model.model));
    const item = document.createElement('button');
    item.className = 'sidebar-model';
    item.title = name;
    const dot = document.createElement('span');
    dot.className = 'model-dot';
    dot.textContent = '●';
    const label = document.createElement('span');
    label.className = 'model-name';
    label.textContent = name;
    item.append(dot, label);
    item.addEventListener('click', () => { select.value = model.name || model.model; select.dispatchEvent(new Event('change')); });
    $('#sidebarModels').append(item);
  }
  if (!state.models.length) {
    const empty = document.createElement('div');
    empty.className = 'side-empty';
    empty.textContent = 'No local models. Pull one to start chatting.';
    $('#sidebarModels').append(empty);
  }
  select.value = state.models.some((model) => (model.name || model.model) === previous)
    ? previous
    : (state.models[0]?.name || state.models[0]?.model || '');
  reportSelect.value = state.models.some((model) => (model.name || model.model) === previousReportModel)
    ? previousReportModel
    : select.value;
  safetySettingsModel.value = state.models.some((model) => (model.name || model.model) === previousSafetyModel)
    ? previousSafetyModel
    : reportSelect.value;
  $('#sendButton').disabled = !select.value || state.busy;
  updateReportButton();
  updateDatabasePromptButtons();
}

async function loadRunningModels() {
  try {
    const data = await (await api('/api/running')).json();
    const models = Array.isArray(data.models) ? data.models : [];
    const select = $('#runningModelSelect');
    const previous = select.value;
    select.replaceChildren(new Option(models.length ? 'Select a loaded model…' : 'No models loaded', ''));
    for (const model of models) {
      const name = model.name || model.model;
      if (name) select.add(new Option(name, name));
    }
    select.value = models.some((model) => (model.name || model.model) === previous)
      ? previous
      : (models[0]?.name || models[0]?.model || '');
    $('#offloadSelected').disabled = !select.value;

    const container = $('#runningModels');
    container.replaceChildren();
    if (!models.length) {
      const empty = document.createElement('div');
      empty.className = 'side-empty';
      empty.textContent = 'No models currently loaded.';
      container.append(empty);
      $('#gpuValue').textContent = '—';
      $('#gpuMeter').style.width = '0%';
      $('#gpuCaption').textContent = 'Ollama model allocation';
      return;
    }
    const totalVram = models.reduce((sum, model) => sum + (Number(model.size_vram) || 0), 0);
    const totalSize = models.reduce((sum, model) => sum + (Number(model.size) || 0), 0);
    $('#gpuValue').textContent = totalVram ? formatBytes(totalVram) : 'CPU';
    $('#gpuCaption').textContent = `${models.length} model${models.length === 1 ? '' : 's'} loaded · VRAM allocation`;
    $('#gpuMeter').style.width = `${totalSize ? Math.min(100, totalVram / totalSize * 100) : (totalVram ? 100 : 0)}%`;
    for (const model of models) {
      const row = document.createElement('div');
      row.className = 'running-model';
      const name = document.createElement('span');
      name.textContent = safeText(model.name || model.model);
      const allocation = document.createElement('span');
      allocation.textContent = Number(model.size_vram) ? `${formatBytes(Number(model.size_vram))} VRAM` : 'CPU';
      row.append(name, allocation);
      container.append(row);
    }
  } catch {
    $('#runningModels').textContent = 'Running model data unavailable.';
    $('#runningModelSelect').replaceChildren(new Option('Could not load models', ''));
    $('#offloadSelected').disabled = true;
  }
}

async function offloadSelectedModel() {
  const model = $('#runningModelSelect').value;
  if (!model) return;

  const button = $('#offloadSelected');
  button.disabled = true;
  try {
    await api('/api/models/unload', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: model })
    });
    toast(`Offloaded ${model}.`);
    await loadRunningModels();
  } catch (error) {
    toast(`Could not offload ${model}: ${error.message}`, true);
    button.disabled = false;
  }
}

async function loadMetrics() {
  try {
    const metrics = await (await api('/api/metrics')).json();
    if (Number.isFinite(metrics.cpuPercent)) {
      $('#cpuValue').textContent = `${Math.round(metrics.cpuPercent)}%`;
      $('#cpuMeter').style.width = `${Math.max(0, Math.min(100, metrics.cpuPercent))}%`;
      $('#cpuCaption').textContent = 'System utilization';
    } else {
      $('#cpuValue').textContent = 'N/A';
      $('#cpuCaption').textContent = metrics.errors?.find((error) => error.startsWith('CPU')) || 'Not supported';
    }
    if (Number.isFinite(metrics.memoryUsedBytes) && Number.isFinite(metrics.memoryTotalBytes)) {
      const percent = metrics.memoryTotalBytes ? metrics.memoryUsedBytes / metrics.memoryTotalBytes * 100 : 0;
      $('#memoryValue').textContent = `${Math.round(percent)}%`;
      $('#memoryMeter').style.width = `${Math.max(0, Math.min(100, percent))}%`;
      $('#memoryCaption').textContent = `${formatBytes(metrics.memoryUsedBytes)} / ${formatBytes(metrics.memoryTotalBytes)}`;
    } else {
      $('#memoryValue').textContent = 'N/A';
      $('#memoryCaption').textContent = metrics.errors?.find((error) => error.startsWith('Memory')) || 'Not supported';
    }
    $('#gpuCaption').title = metrics.gpuNote || '';
    renderMemoryUsers(metrics);
  } catch (error) {
    $('#cpuCaption').textContent = 'Metrics unavailable';
    $('#memoryCaption').textContent = error.message;
    $('#memoryUsersSummary').textContent = 'Process data unavailable';
    $('#memoryUsersList').textContent = error.message;
  }
}

function renderMemoryUsers(metrics) {
  const processes = Array.isArray(metrics.topMemoryProcesses) ? metrics.topMemoryProcesses : [];
  const list = $('#memoryUsersList');
  list.replaceChildren();

  if (metrics.processMemoryError) {
    list.textContent = metrics.processMemoryError;
  } else if (!processes.length) {
    list.textContent = 'No process memory data available.';
  } else {
    const largestProcess = Math.max(...processes.map((process) => Number(process.memoryBytes) || 0));
    for (const process of processes) {
      const row = document.createElement('div');
      row.className = 'memory-process';
      const heading = document.createElement('div');
      heading.className = 'memory-process-heading';
      const name = document.createElement('span');
      name.textContent = safeText(process.name, 'Unknown process');
      const amount = document.createElement('span');
      const count = Number(process.processCount) || 1;
      amount.textContent = `${formatBytes(Number(process.memoryBytes))}${count > 1 ? ` · ${count} processes` : ''}`;
      heading.append(name, amount);
      const meter = document.createElement('div');
      meter.className = 'memory-process-meter';
      const fill = document.createElement('span');
      fill.style.width = `${largestProcess ? Number(process.memoryBytes) / largestProcess * 100 : 0}%`;
      meter.append(fill);
      row.append(heading, meter);
      list.append(row);
    }
  }

  if (Number.isFinite(metrics.memoryUsedBytes) && Number.isFinite(metrics.memoryTotalBytes) &&
      metrics.memoryTotalBytes > 0) {
    const usage = metrics.memoryUsedBytes / metrics.memoryTotalBytes * 100;
    const advice = usage >= 85
      ? 'Memory use is high. Consider closing large apps you recognize but are not using. Avoid stopping system processes.'
      : 'These are the largest processes by resident memory. Close only apps you recognize and no longer need.';
    $('#memoryAdvice').textContent = metrics.processMemoryWarning
      ? `${advice} ${metrics.processMemoryWarning}`
      : advice;
    $('#memoryUsersSummary').textContent = `${Math.round(usage)}% memory in use · ${processes.length} largest processes`;
  } else {
    $('#memoryAdvice').textContent = metrics.processMemoryWarning
      ? metrics.processMemoryWarning
      : 'Close only apps you recognize and no longer need.';
    $('#memoryUsersSummary').textContent = `${processes.length} largest processes`;
  }
}

function setWorkspaceMode(mode) {
  const showingChat = mode === 'chat';
  const showingReports = mode === 'reports';
  const showingDatabases = mode === 'databases';
  const showingSafety = mode === 'safety';
  $('#chatColumn').hidden = !showingChat;
  $('#reportsColumn').hidden = !showingReports;
  $('#databaseConnectionsColumn').hidden = !showingDatabases;
  $('#chatSafetyColumn').hidden = !showingSafety;
  $('#settingsPanel').hidden = !showingChat;
  $('#contentGrid').classList.toggle('report-mode', showingReports);
  $('#contentGrid').classList.toggle('database-mode', showingDatabases || showingSafety);
  $('#chatNav').classList.toggle('active', showingChat);
  $('#reportsNav').classList.toggle('active', showingReports);
  $('#databasesNav').classList.toggle('active', showingDatabases);
  $('#safetyNav').classList.toggle('active', showingSafety);
  $('#pageTitle').textContent = showingReports
    ? 'Database Chat'
    : showingDatabases
      ? 'Database connections'
      : showingSafety
        ? 'Chat Safety Settings'
        : 'General Chat';
  if (showingReports) {
    if (state.reportDatabasesLoaded) void loadReportSchema();
    else void loadReportDatabases();
  } else if (showingDatabases) {
    void loadReportDatabases($('#reportDatabase').value || null);
  } else if (showingSafety && state.reportDatabasesLoaded) {
    renderGuidanceDatabaseOptions(state.databaseProfiles);
  } else if (showingSafety) {
    void loadReportDatabases();
  }
}

function updateReportButton() {
  $('#runReportButton').disabled = !$('#reportQuestion').value.trim() ||
    !$('#reportModel').value || !$('#reportDatabase').value || state.reportBusy;
}

async function loadReportDatabases(preferredDatabaseId = null) {
  const select = $('#reportDatabase');
  select.disabled = true;
  try {
    const databases = await (await api('/api/reports/databases')).json();
    const previous = preferredDatabaseId || select.value;
    select.replaceChildren();
    for (const database of databases) {
      select.add(new Option(`${database.name} · ${database.provider}`, database.id));
    }
    renderDatabaseProfiles(databases);

    if (!databases.length) {
      select.add(new Option('No databases available', ''));
      $('#reportDatabaseStatus').textContent = 'No report databases available';
      $('#reportSchema').textContent = 'No report databases are registered.';
      state.reportDatabasesLoaded = true;
      updateReportButton();
      return;
    }

    select.value = databases.some((database) => database.id === previous)
      ? previous
      : databases[0].id;
    select.disabled = databases.length < 2;
    state.reportDatabasesLoaded = true;
    await loadReportSchema();
    updateReportButton();
  } catch (error) {
    select.replaceChildren(new Option('Database list unavailable', ''));
    $('#reportDatabaseStatus').textContent = 'Database list unavailable';
    $('#reportSchema').textContent = error.message;
    $('#databaseProfileStatus').textContent = error.message;
    $('#guidanceDatabaseSelect').replaceChildren(new Option('Saved profiles unavailable', ''));
    $('#guidanceDatabaseSelect').disabled = true;
    $('#chatSafetyEmpty').textContent = error.message;
    $('#chatSafetyEmpty').hidden = false;
    closeDatabaseGuidance();
    state.reportDatabasesLoaded = false;
    updateReportButton();
  }
}

function renderDatabaseProfiles(databases) {
  state.databaseProfiles = databases;
  const list = $('#databaseProfileList');
  list.replaceChildren();
  if (state.databaseGuidanceProfileId &&
      !databases.some((database) => database.id === state.databaseGuidanceProfileId)) {
    closeDatabaseGuidance();
  }

  for (const database of databases) {
    const item = document.createElement('div');
    item.className = 'database-profile-item';
    const name = document.createElement('span');
    name.textContent = `${database.name} · ${database.provider}`;
    const kind = document.createElement('span');
    kind.className = 'database-profile-kind';
    kind.textContent = database.isDemoData
      ? 'Built-in'
      : database.canRemove
        ? 'Saved profile'
        : 'Environment';
    item.append(name, kind);

    if (database.canRemove) {
      const actions = document.createElement('div');
      actions.className = 'database-profile-item-actions';
      const remove = document.createElement('button');
      remove.type = 'button';
      remove.className = 'database-profile-remove';
      remove.textContent = 'Remove';
      remove.addEventListener('click', () => void removeDatabaseProfile(database));
      actions.append(remove);
      item.append(actions);
    }

    list.append(item);
  }

  renderGuidanceDatabaseOptions(databases);
}

function renderGuidanceDatabaseOptions(databases) {
  const select = $('#guidanceDatabaseSelect');
  const savedProfiles = databases.filter((database) => database.canRemove);
  const previous = state.databaseGuidanceProfileId || select.value;
  select.replaceChildren(new Option(
    savedProfiles.length ? 'Select a saved profile…' : 'No saved profiles available',
    ''
  ));
  for (const profile of savedProfiles) {
    select.add(new Option(`${profile.name} · ${profile.provider}`, profile.id));
  }

  select.value = savedProfiles.some((profile) => profile.id === previous) ? previous : '';
  select.disabled = savedProfiles.length === 0;
  $('#chatSafetyEmpty').textContent =
    'No saved profiles yet. Add one in Database connections to configure its chat safety settings.';
  $('#chatSafetyEmpty').hidden = savedProfiles.length > 0;
  if (!select.value) {
    closeDatabaseGuidance();
  } else if (select.value !== state.databaseGuidanceProfileId) {
    const profile = savedProfiles.find((item) => item.id === select.value);
    if (profile) void openDatabaseGuidance(profile);
  }
}

async function openDatabaseGuidance(database) {
  state.databaseGuidanceProfileId = database.id;
  $('#guidanceDatabaseSelect').value = database.id;
  $('#databaseGuidancePanel').hidden = false;
  $('#databaseGuidanceTitle').textContent = `Settings for ${database.name}`;
  $('#databaseGuidanceStatus').textContent = 'Loading saved settings…';
  $('#databaseSafetyGenerationStatus').textContent = '';
  $('#databaseContext').value = '';
  $('#databaseHints').value = '';
  $('#databaseValidations').value = '';
  $('#newDatabaseTodo').value = '';
  $('#newDatabaseTestPrompt').value = '';
  state.databaseTodoItems = [];
  state.databaseTestPrompts = [];
  renderDatabaseTodoItems();
  renderDatabaseTestPrompts();
  try {
    const guidance = await (await api(
      `/api/reports/databases/${encodeURIComponent(database.id)}/guidance`
    )).json();
    if (state.databaseGuidanceProfileId !== database.id) return;
    $('#databaseContext').value = guidance.context || '';
    $('#databaseHints').value = guidance.hints || '';
    $('#databaseValidations').value = guidance.validations || '';
    state.databaseTodoItems = (guidance.todoItems || []).map((item) => ({
      text: item.text,
      isComplete: Boolean(item.isComplete)
    }));
    state.databaseTestPrompts = (guidance.testPrompts || []).map((prompt) => String(prompt));
    renderDatabaseTodoItems();
    renderDatabaseTestPrompts();
    $('#databaseGuidanceStatus').textContent = '';
  } catch (error) {
    if (state.databaseGuidanceProfileId === database.id) {
      $('#databaseGuidanceStatus').textContent = error.message;
    }
  }
  updateDatabasePromptButtons();
}

function closeDatabaseGuidance() {
  state.databaseGuidanceProfileId = null;
  state.databaseTodoItems = [];
  state.databaseTestPrompts = [];
  $('#databaseSafetyGenerationStatus').textContent = '';
  $('#databaseGuidancePanel').hidden = true;
  updateDatabasePromptButtons();
}

function renderDatabaseTodoItems() {
  const list = $('#databaseTodoList');
  list.replaceChildren();
  state.databaseTodoItems.forEach((item, index) => {
    const row = document.createElement('div');
    row.className = 'database-editable-item todo-item';
    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.checked = item.isComplete;
    checkbox.setAttribute('aria-label', `Mark checklist item ${index + 1} complete`);
    checkbox.addEventListener('change', () => {
      item.isComplete = checkbox.checked;
    });
    const text = document.createElement('input');
    text.type = 'text';
    text.maxLength = 200;
    text.value = item.text;
    text.setAttribute('aria-label', `Checklist item ${index + 1}`);
    text.addEventListener('input', () => {
      item.text = text.value;
    });
    const remove = createDatabaseListRemoveButton('Remove checklist item', () => {
      state.databaseTodoItems.splice(index, 1);
      renderDatabaseTodoItems();
    });
    row.append(checkbox, text, remove);
    list.append(row);
  });
}

function renderDatabaseTestPrompts() {
  const list = $('#databaseTestPromptList');
  list.replaceChildren();
  state.databaseTestPrompts.forEach((prompt, index) => {
    const row = document.createElement('div');
    row.className = 'database-editable-item prompt-item';
    const text = document.createElement('input');
    text.type = 'text';
    text.maxLength = 200;
    text.value = prompt;
    text.setAttribute('aria-label', `Test prompt ${index + 1}`);
    text.addEventListener('input', () => {
      state.databaseTestPrompts[index] = text.value;
    });
    const remove = createDatabaseListRemoveButton('Remove test prompt', () => {
      state.databaseTestPrompts.splice(index, 1);
      renderDatabaseTestPrompts();
      updateDatabasePromptButtons();
    });
    row.append(text, remove);
    list.append(row);
  });
  updateDatabasePromptButtons();
}

function createDatabaseListRemoveButton(label, onClick) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'database-remove-item';
  button.textContent = 'Remove';
  button.setAttribute('aria-label', label);
  button.addEventListener('click', onClick);
  return button;
}

function updateDatabasePromptButtons() {
  const canGenerate = Boolean(state.databaseGuidanceProfileId) &&
    Boolean($('#databaseSafetySettingsModel').value) &&
    !state.databaseGenerationBusy &&
    !state.databaseGuidanceSaving;
  $('#generateDatabaseSafetySettings').disabled = !canGenerate;
  $('#generateDatabaseTestPrompts').disabled =
    !canGenerate;
  $('#addDatabaseTestPrompt').disabled =
    !state.databaseGuidanceProfileId || state.databaseTestPrompts.length >= 3;
  $('#saveDatabaseGuidance').disabled =
    state.databaseGenerationBusy || state.databaseGuidanceSaving;
}

async function generateDatabaseTestPrompts() {
  const databaseId = state.databaseGuidanceProfileId;
  const model = $('#databaseSafetySettingsModel').value;
  if (!databaseId || !model || state.databaseGenerationBusy || state.databaseGuidanceSaving) return;

  state.databaseGenerationBusy = true;
  $('#databaseGuidanceStatus').textContent = 'Generating test prompts…';
  updateDatabasePromptButtons();
  try {
    const prompts = await (await api(
      `/api/reports/databases/${encodeURIComponent(databaseId)}/test-prompts`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ model })
      }
    )).json();
    if (state.databaseGuidanceProfileId !== databaseId) return;
    state.databaseTestPrompts = prompts;
    renderDatabaseTestPrompts();
    $('#databaseGuidanceStatus').textContent =
      'Review the generated prompts, then save them with this profile.';
  } catch (error) {
    if (state.databaseGuidanceProfileId === databaseId) {
      $('#databaseGuidanceStatus').textContent = error.message;
    }
  } finally {
    state.databaseGenerationBusy = false;
    updateDatabasePromptButtons();
  }
}

async function generateDatabaseSafetySettings() {
  const databaseId = state.databaseGuidanceProfileId;
  const model = $('#databaseSafetySettingsModel').value;
  if (!databaseId || !model || state.databaseGenerationBusy || state.databaseGuidanceSaving) return;

  state.databaseGenerationBusy = true;
  $('#databaseSafetyGenerationStatus').textContent = 'Generating starter settings…';
  $('#databaseGuidanceStatus').textContent = '';
  updateDatabasePromptButtons();
  try {
    const draft = await (await api(
      `/api/reports/databases/${encodeURIComponent(databaseId)}/safety-settings-draft`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ model })
      }
    )).json();
    if (state.databaseGuidanceProfileId !== databaseId) return;

    $('#databaseContext').value = draft.context;
    $('#databaseHints').value = draft.hints;
    $('#databaseValidations').value = draft.validations;
    state.databaseTodoItems = (draft.todoItems || []).map((item) => ({
      text: item.text,
      isComplete: false
    }));
    state.databaseTestPrompts = (draft.testPrompts || []).map((prompt) => String(prompt));
    renderDatabaseTodoItems();
    renderDatabaseTestPrompts();
    $('#databaseSafetyGenerationStatus').textContent =
      'Starter settings generated. Review all fields below, then save them with the profile.';
  } catch (error) {
    if (state.databaseGuidanceProfileId === databaseId) {
      $('#databaseSafetyGenerationStatus').textContent = error.message === 'Not Found'
        ? 'This running server does not have the settings-generation endpoint (404). Restart HelloOllama and reload this page.'
        : error.message;
    }
  } finally {
    state.databaseGenerationBusy = false;
    updateDatabasePromptButtons();
  }
}

async function removeDatabaseProfile(database) {
  if (!window.confirm(`Remove "${database.name}" from HelloOllama? The database itself will not be deleted or modified.`)) {
    return;
  }

  const selectedDatabaseId = $('#reportDatabase').value;
  try {
    await api(`/api/reports/databases/${encodeURIComponent(database.id)}`, { method: 'DELETE' });
    $('#databaseProfileStatus').textContent = `Removed ${database.name}. The database itself was not changed.`;
    await loadReportDatabases(selectedDatabaseId === database.id ? null : selectedDatabaseId);
  } catch (error) {
    $('#databaseProfileStatus').textContent = error.message;
  }
}

async function loadReportSchema() {
  const databaseId = $('#reportDatabase').value;
  if (!databaseId) return;

  try {
    const schema = await (await api(`/api/reports/schema?databaseId=${encodeURIComponent(databaseId)}`)).json();
    if (databaseId !== $('#reportDatabase').value) return;
    $('#reportDatabaseStatus').textContent =
      `${schema.databaseName} · ${schema.providerName} · Read only${schema.isDemoData ? ' · sample data' : ''}`;
    $('#reportQuestion').placeholder = schema.databaseId === 'access-control-demo'
      ? 'Ask about cardholders, sites, swipes, doors, readers, or attendance…'
      : schema.databaseId === 'sales-demo'
        ? 'Ask a question about sales, customers, products…'
        : 'Ask a question about the available database tables…';
    const container = $('#reportSchema');
    container.replaceChildren();
    for (const table of schema.tables || []) {
      const line = document.createElement('div');
      const relationships = (table.foreignKeys || []).map((foreignKey) =>
        `${foreignKey.column} -> ${foreignKey.referencedTable}.${foreignKey.referencedColumn || 'primary key'}`);
      line.textContent = `${table.name} (${(table.columns || []).join(', ')})${relationships.length ? ` · joins: ${relationships.join(', ')}` : ''}`;
      container.append(line);
    }
    if (!schema.tables?.length) container.textContent = 'No tables found in this database.';
    renderSavedReportPrompts(schema.testPrompts || []);
    updateReportWelcome(schema);
  } catch (error) {
    if (databaseId !== $('#reportDatabase').value) return;
    $('#reportDatabaseStatus').textContent = 'Database unavailable';
    $('#reportSchema').textContent = error.message;
    renderSavedReportPrompts([]);
  }
}

function renderSavedReportPrompts(prompts) {
  const panel = $('#reportSavedPrompts');
  const list = $('#reportSavedPromptList');
  list.replaceChildren();
  for (const prompt of prompts) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'report-suggestion';
    button.textContent = prompt;
    button.dataset.reportQuestion = prompt;
    button.addEventListener('click', () => {
      $('#reportQuestion').value = prompt;
      $('#reportQuestion').dispatchEvent(new Event('input'));
      $('#reportQuestion').focus();
    });
    list.append(button);
  }
  panel.hidden = prompts.length === 0;
}

function updateReportWelcome(schema) {
  const welcome = $('#reportWelcome');
  if (!welcome) return;

  const isSalesDemo = schema.databaseId === 'sales-demo';
  const isAccessControlDemo = schema.databaseId === 'access-control-demo';
  const examples = isSalesDemo
    ? [
      ['Completed sales by region', 'What was total completed sales by region?'],
      ['Monthly sales in 2026', 'Show monthly completed sales for 2026.'],
      ['Top products by revenue', 'Which products generated the most revenue?']
    ]
    : isAccessControlDemo
      ? [
        ['Denied swipes by site', 'Count denied swipe attempts by site. Join swipes.reader_id to readers.reader_id, readers.door_id to doors.door_id, and doors.site_id to sites.site_id. Do not join cards or attendance_records. Filter access_result = DENIED and swipe_time_utc from 2026-11-16 inclusive to 2026-11-19 exclusive. Return site_name and the count, highest count first.'],
        ['Daily attendance', 'For 2026-11-16, list each cardholder name, site name, attendance status, first entry time, last exit time, and minutes on site. Join attendance_records.cardholder_id to cardholders.cardholder_id and attendance_records.site_id to sites.site_id.'],
        ['Door alerts', 'List door activities with severity ALARM or WARNING from 2026-11-16 through 2026-11-18. Include site, door, event time, event type, severity, and details. Join door_activities.door_id to doors.door_id and doors.site_id to sites.site_id.']
      ]
    : [];

  const description = welcome.querySelector('p');
  description.textContent = isSalesDemo
    ? 'This starter database contains sample sales, customers, regions, products, and orders.'
    : isAccessControlDemo
      ? 'This access-control sample contains fictional cardholders, sites, doors, readers, swipes, activities, and attendance.'
      : 'Ask a question about the tables shown in Available tables and columns.';

  welcome.querySelectorAll('.report-suggestion').forEach((button, index) => {
    const example = examples[index];
    button.hidden = !example;
    if (!example) return;
    button.textContent = example[0];
    button.dataset.reportQuestion = example[1];
  });
}

function renderReportResult(container, report) {
  container.replaceChildren();
  const summary = document.createElement('p');
  summary.className = 'report-summary';
  summary.textContent = safeText(report.summary);
  if (report.needsClarification) {
    const meta = document.createElement('p');
    meta.className = 'report-meta';
    meta.textContent = `Clarification needed · ${report.databaseName}`;
    container.append(summary, meta);
    return;
  }

  const meta = document.createElement('p');
  meta.className = 'report-meta';
  const rowCount = report.rows?.length || 0;
  meta.textContent = `${rowCount} row${rowCount === 1 ? '' : 's'}${report.truncated ? ` shown (limited to 100)` : ''} · ${report.databaseName} · ${report.providerName}${report.isDemoData ? ' · sample data' : ''} · ${new Date(report.generatedAt).toLocaleTimeString()}`;
  container.append(summary, meta);

  if (report.columns?.length) {
    const wrapper = document.createElement('div');
    wrapper.className = 'report-table-wrap';
    const table = document.createElement('table');
    table.className = 'report-table';
    const head = document.createElement('thead');
    const headingRow = document.createElement('tr');
    for (const column of report.columns) {
      const heading = document.createElement('th');
      heading.textContent = column;
      headingRow.append(heading);
    }
    head.append(headingRow);
    const body = document.createElement('tbody');
    for (const row of report.rows || []) {
      const tableRow = document.createElement('tr');
      for (const value of row) {
        const cell = document.createElement('td');
        cell.textContent = value === null || value === undefined
          ? '—'
          : (typeof value === 'object' ? JSON.stringify(value) : String(value));
        tableRow.append(cell);
      }
      body.append(tableRow);
    }
    table.append(head, body);
    wrapper.append(table);
    container.append(wrapper);
  }

  const sqlDetails = document.createElement('details');
  sqlDetails.className = 'report-sql';
  const sqlSummary = document.createElement('summary');
  sqlSummary.textContent = 'View generated SQL';
  const sql = document.createElement('pre');
  sql.textContent = report.sql;
  sqlDetails.append(sqlSummary, sql);
  container.append(sqlDetails);
}

async function runBusinessReport(question, retryModel = null, retryDatabaseId = null) {
  const prompt = question.trim();
  const model = retryModel || $('#reportModel').value;
  const databaseId = retryDatabaseId || $('#reportDatabase').value;
  if (!prompt || state.reportBusy) return;
  if (!model) {
    toast('Select an Ollama model before running a report.', true);
    return;
  }
  if (!databaseId) {
    toast('Select a database before running a report.', true);
    return;
  }

  state.reportBusy = true;
  $('#runReportButton').disabled = true;
  $('#reportQuestion').value = '';
  const welcome = $('#reportWelcome');
  welcome?.remove();

  const entry = document.createElement('article');
  entry.className = 'report-entry';
  const questionElement = document.createElement('p');
  questionElement.className = 'report-question';
  questionElement.textContent = prompt;
  const response = document.createElement('div');
  response.className = 'report-meta';
  response.textContent = `Asking ${model} to prepare a read-only report…`;
  entry.append(questionElement, response);
  $('#reportFeed').append(entry);
  $('#reportFeed').scrollTop = $('#reportFeed').scrollHeight;

  try {
    const report = await (await api('/api/reports/query', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ databaseId, model, question: prompt })
    })).json();
    renderReportResult(response, report);
  } catch (error) {
    response.className = 'report-error';
    const message = document.createElement('p');
    message.textContent = error.message;
    const retryButton = document.createElement('button');
    retryButton.className = 'report-retry';
    retryButton.type = 'button';
    retryButton.textContent = 'Retry question';
    retryButton.addEventListener('click', () => {
      if (state.reportBusy) return;
      retryButton.disabled = true;
      void runBusinessReport(prompt, model, databaseId);
    });
    response.replaceChildren(message, retryButton);
  } finally {
    state.reportBusy = false;
    updateReportButton();
    $('#reportFeed').scrollTop = $('#reportFeed').scrollHeight;
    $('#reportQuestion').focus();
  }
}

function addMessage(role, text = '') {
  $('#welcome')?.remove();
  const article = document.createElement('article');
  article.className = `message ${role}`;
  const avatar = document.createElement('div');
  avatar.className = 'message-avatar';
  avatar.textContent = role === 'assistant' ? '✳' : '●';
  const body = document.createElement('div');
  body.className = 'message-body';
  const name = document.createElement('div');
  name.className = 'message-name';
  name.textContent = role === 'assistant' ? ($('#chatModel').value || 'Assistant') : 'You';
  const content = document.createElement('div');
  content.className = 'message-content';
  content.textContent = text;
  body.append(name, content);
  article.append(avatar, body);
  $('#chatMessages').append(article);
  $('#chatMessages').scrollTop = $('#chatMessages').scrollHeight;
  return content;
}

function currentOptions() {
  const options = {
    temperature: Number($('#temperature').value),
    top_p: Number($('#topP').value),
    top_k: Number($('#topK').value),
    num_ctx: Number($('#numCtx').value),
    repeat_penalty: Number($('#repeatPenalty').value)
  };
  const seed = Number($('#seed').value);
  if ($('#seed').value !== '' && Number.isFinite(seed) && seed >= 0) options.seed = seed;
  return options;
}

async function readNdjson(response, onObject) {
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  while (true) {
    const { value, done } = await reader.read();
    buffer += decoder.decode(value || new Uint8Array(), { stream: !done });
    const lines = buffer.split('\n');
    buffer = lines.pop() || '';
    for (const line of lines) {
      if (!line.trim()) continue;
      onObject(JSON.parse(line));
    }
    if (done) break;
  }
  if (buffer.trim()) onObject(JSON.parse(buffer));
}

async function sendMessage(text) {
  const model = $('#chatModel').value;
  if (!model || state.busy) return;
  const content = text.trim();
  if (!content) return;
  state.busy = true;
  $('#sendButton').disabled = true;
  $('#messageInput').value = '';
  $('#messageInput').style.height = 'auto';
  state.messages.push({ role: 'user', content });
  addMessage('user', content);
  const responseNode = addMessage('assistant');
  responseNode.classList.add('typing-cursor');
  try {
    const response = await api('/api/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ model, messages: state.messages, stream: true, options: currentOptions() })
    });
    let answer = '';
    await readNdjson(response, (part) => {
      if (part.error) throw new Error(part.error);
      const delta = part.message?.content || '';
      answer += delta;
      responseNode.textContent = answer;
      $('#chatMessages').scrollTop = $('#chatMessages').scrollHeight;
    });
    responseNode.classList.remove('typing-cursor');
    state.messages.push({ role: 'assistant', content: answer });
    loadRunningModels();
  } catch (error) {
    responseNode.classList.remove('typing-cursor');
    responseNode.closest('.message').classList.add('message-error');
    responseNode.textContent = error.message;
    state.messages.pop();
  } finally {
    state.busy = false;
    $('#sendButton').disabled = !$('#chatModel').value;
    $('#messageInput').focus();
  }
}

function updateTemperature() {
  $('#temperatureValue').textContent = Number($('#temperature').value).toFixed(1);
}

function restoreParameters() {
  try {
    const saved = JSON.parse(localStorage.getItem('ollama-desk-parameters') || '{}');
    for (const key of ['temperature', 'topP', 'topK', 'numCtx', 'repeatPenalty', 'seed']) {
      if (saved[key] !== undefined) $(`#${key}`).value = saved[key];
    }
  } catch { /* Ignore malformed local preferences and keep the defaults. */ }
  updateTemperature();
}

function saveParameters() {
  const values = {};
  for (const key of ['temperature', 'topP', 'topK', 'numCtx', 'repeatPenalty', 'seed']) {
    values[key] = $(`#${key}`).value;
  }
  localStorage.setItem('ollama-desk-parameters', JSON.stringify(values));
}

function clampNumber(input, minimum, maximum, fallback) {
  const value = Number(input.value);
  if (!Number.isFinite(value)) input.value = fallback;
  else input.value = Math.max(minimum, Math.min(maximum, value));
}

async function runPull(name, onProgress) {
  const response = await api('/api/pull', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, stream: true })
  });
  await readNdjson(response, (part) => {
    if (part.error) throw new Error(part.error);
    const percent = part.total ? Math.min(100, Math.round(part.completed / part.total * 100)) : null;
    onProgress(part.status || 'Working…', percent);
  });
  await loadModels();
  await loadRunningModels();
}

function toggleCommandFields() {
  const action = $('#commandSelect').value;
  const isPull = action === 'pull';
  $('#pullModelField').hidden = !isPull;
  $('#namedModelField').hidden = isPull;
  $('#popularModelSelect').value = '';
  $('#otherPullModel').value = '';
  $('#otherPullModel').hidden = true;
  $('#copyDestinationField').hidden = action !== 'copy';
  $('#commandResult').hidden = true;
  $('#commandProgress').hidden = !isPull;
  $('#runCommand').disabled = false;
}

async function executeCommand() {
  const action = $('#commandSelect').value;
  const model = action === 'pull'
    ? ($('#popularModelSelect').value === 'other'
      ? $('#otherPullModel').value.trim()
      : $('#popularModelSelect').value)
    : $('#commandModel').value.trim();
  if (!model) { toast('Enter a model name first.', true); return; }
  if (action === 'delete' && !confirm(`Delete ${model} from your Ollama library? This cannot be undone.`)) return;
  const button = $('#runCommand');
  const result = $('#commandResult');
  const progress = $('#commandProgress');
  button.disabled = true;
  result.hidden = true;
  try {
    if (action === 'pull') {
      progress.hidden = false;
      $('#commandProgressMeter').style.width = '0%';
      $('#commandProgressText').textContent = `Preparing to pull ${model}…`;
      await runPull(model, (status, percent) => {
        $('#commandProgressText').textContent = percent === null ? status : `${status} · ${percent}%`;
        if (percent !== null) $('#commandProgressMeter').style.width = `${percent}%`;
      });
      $('#commandProgressText').textContent = `${model} is ready.`;
      $('#commandProgressMeter').style.width = '100%';
      toast(`Pulled ${model}.`);
    } else {
      const body = action === 'copy'
        ? { source: model, destination: $('#copyDestination').value.trim() }
        : { name: model };
      if (action === 'copy' && !body.destination) { toast('Enter a destination model name.', true); return; }
      const data = await (await api(`/api/models/${action}`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
      })).json();
      if (action === 'show') {
        result.textContent = JSON.stringify(data, null, 2);
        result.hidden = false;
      } else {
        toast(action === 'unload' ? `Offloaded ${model}.` : `${action === 'delete' ? 'Deleted' : 'Copied'} ${model}.`);
      }
      await loadModels();
      await loadRunningModels();
    }
  } catch (error) {
    $('#commandProgressText').textContent = error.message;
    toast(error.message, true);
  } finally {
    button.disabled = false;
  }
}

function setup() {
  restoreParameters();
  $('#connectionUrl').textContent = 'localhost:11434';
  $('#chatForm').addEventListener('submit', (event) => {
    event.preventDefault();
    sendMessage($('#messageInput').value);
  });
  $('#messageInput').addEventListener('input', (event) => {
    const input = event.currentTarget;
    input.style.height = 'auto';
    input.style.height = `${Math.min(input.scrollHeight, 150)}px`;
    $('#sendButton').disabled = !input.value.trim() || !$('#chatModel').value || state.busy;
  });
  $('#messageInput').addEventListener('keydown', (event) => {
    if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); $('#chatForm').requestSubmit(); }
  });
  $('#chatModel').addEventListener('change', () => { $('#sendButton').disabled = !$('#chatModel').value || state.busy; });
  $('#chatModel').addEventListener('change', () => {
    $('#reportModel').value = $('#chatModel').value;
    updateReportButton();
  });
  $('#reportModel').addEventListener('change', () => {
    $('#chatModel').value = $('#reportModel').value;
    $('#sendButton').disabled = !$('#chatModel').value || state.busy;
    updateReportButton();
  });
  $('#databaseSafetySettingsModel').addEventListener('change', updateDatabasePromptButtons);
  $('#guidanceDatabaseSelect').addEventListener('change', () => {
    const profile = state.databaseProfiles.find((database) =>
      database.id === $('#guidanceDatabaseSelect').value && database.canRemove);
    if (profile) void openDatabaseGuidance(profile);
    else closeDatabaseGuidance();
  });
  $('#addDatabaseTodo').addEventListener('click', () => {
    const text = $('#newDatabaseTodo').value.trim();
    if (!text) {
      $('#databaseGuidanceStatus').textContent = 'Enter a checklist item before adding it.';
      return;
    }
    if (state.databaseTodoItems.length >= 25) {
      $('#databaseGuidanceStatus').textContent = 'A profile can have at most 25 checklist items.';
      return;
    }
    state.databaseTodoItems.push({ text, isComplete: false });
    $('#newDatabaseTodo').value = '';
    $('#databaseGuidanceStatus').textContent = '';
    renderDatabaseTodoItems();
  });
  $('#addDatabaseTestPrompt').addEventListener('click', () => {
    const text = $('#newDatabaseTestPrompt').value.trim();
    if (!text) {
      $('#databaseGuidanceStatus').textContent = 'Enter a test question before adding it.';
      return;
    }
    if (state.databaseTestPrompts.length >= 3) {
      $('#databaseGuidanceStatus').textContent = 'A profile can have at most three test prompts.';
      return;
    }
    state.databaseTestPrompts.push(text);
    $('#newDatabaseTestPrompt').value = '';
    $('#databaseGuidanceStatus').textContent = '';
    renderDatabaseTestPrompts();
  });
  $('#generateDatabaseTestPrompts').addEventListener('click', () => {
    void generateDatabaseTestPrompts();
  });
  $('#generateDatabaseSafetySettings').addEventListener('click', () => {
    void generateDatabaseSafetySettings();
  });
  $('#databaseGuidanceForm').addEventListener('submit', async (event) => {
    event.preventDefault();
    const databaseId = state.databaseGuidanceProfileId;
    if (!databaseId || state.databaseGenerationBusy || state.databaseGuidanceSaving) return;

    state.databaseGuidanceSaving = true;
    updateDatabasePromptButtons();
    $('#databaseGuidanceStatus').textContent = 'Saving profile settings…';
    try {
      const guidance = await (await api(
        `/api/reports/databases/${encodeURIComponent(databaseId)}/guidance`,
        {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            context: $('#databaseContext').value,
            hints: $('#databaseHints').value,
            validations: $('#databaseValidations').value,
            todoItems: state.databaseTodoItems,
            testPrompts: state.databaseTestPrompts
          })
        }
      )).json();
      if (state.databaseGuidanceProfileId !== databaseId) return;
      state.databaseTodoItems = guidance.todoItems || [];
      state.databaseTestPrompts = guidance.testPrompts || [];
      renderDatabaseTodoItems();
      renderDatabaseTestPrompts();
      $('#databaseGuidanceStatus').textContent =
        `Saved settings and test prompts with ${guidance.databaseName}.`;
      $('#databaseSafetyGenerationStatus').textContent = '';
      if ($('#reportDatabase').value === databaseId) void loadReportSchema();
    } catch (error) {
      if (state.databaseGuidanceProfileId === databaseId) {
        $('#databaseGuidanceStatus').textContent = error.message;
      }
    } finally {
      state.databaseGuidanceSaving = false;
      updateDatabasePromptButtons();
    }
  });
  $('#reportDatabase').addEventListener('change', () => {
    updateReportButton();
    void loadReportSchema();
  });
  $('#databaseProfileProvider').addEventListener('change', () => {
    $('#databaseProfileConnectionString').placeholder =
      $('#databaseProfileProvider').value === 'sqlserver'
        ? 'Server=localhost;Database=YourDatabase;User Id=...;Password=...;Encrypt=True'
        : 'Data Source=/absolute/path/to/database.sqlite';
  });
  $('#toggleDatabaseConnectionString').addEventListener('click', (event) => {
    const input = $('#databaseProfileConnectionString');
    const button = event.currentTarget;
    const show = input.type === 'password';
    input.type = show ? 'text' : 'password';
    button.textContent = show ? 'Hide' : 'Show';
    button.setAttribute('aria-pressed', String(show));
  });
  $('#databaseProfileForm').addEventListener('submit', async (event) => {
    event.preventDefault();
    const form = event.currentTarget;
    const submit = form.querySelector('button[type="submit"]');
    submit.disabled = true;
    $('#databaseProfileStatus').textContent = 'Testing connection and saving profile…';
    try {
      const response = await (await api('/api/reports/databases', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          name: $('#databaseProfileName').value,
          provider: $('#databaseProfileProvider').value,
          connectionString: $('#databaseProfileConnectionString').value
        })
      })).json();
      $('#databaseProfileConnectionString').value = '';
      $('#databaseProfileConnectionString').type = 'password';
      $('#toggleDatabaseConnectionString').textContent = 'Show';
      $('#toggleDatabaseConnectionString').setAttribute('aria-pressed', 'false');
      $('#databaseProfileStatus').textContent = `Connected and saved ${response.name}.`;
      await loadReportDatabases(response.id);
    } catch (error) {
      $('#databaseProfileStatus').textContent = error.message;
    } finally {
      submit.disabled = false;
    }
  });
  $('#chatNav').addEventListener('click', () => setWorkspaceMode('chat'));
  $('#reportsNav').addEventListener('click', () => setWorkspaceMode('reports'));
  $('#databasesNav').addEventListener('click', () => setWorkspaceMode('databases'));
  $('#safetyNav').addEventListener('click', () => setWorkspaceMode('safety'));
  $('#reportForm').addEventListener('submit', (event) => {
    event.preventDefault();
    runBusinessReport($('#reportQuestion').value);
  });
  $('#reportQuestion').addEventListener('input', () => {
    updateReportButton();
  });
  $('#reportQuestion').addEventListener('keydown', (event) => {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      $('#reportForm').requestSubmit();
    }
  });
  document.querySelectorAll('.report-suggestion').forEach((button) => button.addEventListener('click', () => {
    $('#reportQuestion').value = button.dataset.reportQuestion;
    $('#reportQuestion').dispatchEvent(new Event('input'));
    $('#reportQuestion').focus();
  }));
  $('#newChat').addEventListener('click', () => {
    setWorkspaceMode('chat');
    state.messages = [];
    $('#chatMessages').replaceChildren();
    const welcome = document.createElement('div');
    welcome.id = 'welcome';
    welcome.className = 'welcome';
    welcome.innerHTML = '<div class="welcome-glyph">✳</div><div class="eyebrow">YOUR LOCAL AI WORKSPACE</div><h1>What’s on your mind?</h1><p>Chat privately with a model running on your machine.</p>';
    $('#chatMessages').append(welcome);
    $('#messageInput').focus();
  });
  $('#clearChat').addEventListener('click', () => $('#newChat').click());
  $('#refreshModels').addEventListener('click', loadModels);
  $('#refreshMetrics').addEventListener('click', loadMetrics);
  $('#refreshRunning').addEventListener('click', loadRunningModels);
  $('#runningModelSelect').addEventListener('change', () => {
    $('#offloadSelected').disabled = !$('#runningModelSelect').value;
  });
  $('#offloadSelected').addEventListener('click', offloadSelectedModel);
  $('#openCommands').addEventListener('click', () => {
    toggleCommandFields();
    $('#commandsDialog').showModal();
  });
  $('#openAbout').addEventListener('click', () => $('#aboutDialog').showModal());
  $('#cancelCommand').addEventListener('click', () => $('#commandsDialog').close());
  $('#commandSelect').addEventListener('change', toggleCommandFields);
  $('#popularModelSelect').addEventListener('change', () => {
    const isOther = $('#popularModelSelect').value === 'other';
    $('#otherPullModel').hidden = !isOther;
    if (isOther) $('#otherPullModel').focus();
  });
  $('#runCommand').addEventListener('click', executeCommand);
  $('#resetParams').addEventListener('click', () => {
    $('#temperature').value = '0.7'; $('#topP').value = '0.9'; $('#topK').value = '40';
    $('#numCtx').value = '4096'; $('#repeatPenalty').value = '1.1'; $('#seed').value = '';
    updateTemperature(); saveParameters();
  });
  $('#temperature').addEventListener('input', updateTemperature);
  document.querySelectorAll('.settings-panel input').forEach((input) => input.addEventListener('change', () => {
    if (input.id === 'topP') clampNumber(input, 0, 1, '0.9');
    if (input.id === 'topK') clampNumber(input, 1, 100, '40');
    if (input.id === 'numCtx') clampNumber(input, 512, 131072, '4096');
    if (input.id === 'repeatPenalty') clampNumber(input, 0.5, 2, '1.1');
    saveParameters();
  }));
  document.querySelectorAll('.suggestion').forEach((button) => button.addEventListener('click', () => {
    $('#messageInput').value = button.dataset.prompt;
    $('#messageInput').dispatchEvent(new Event('input'));
    $('#messageInput').focus();
  }));
  document.addEventListener('keydown', (event) => {
    if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      ($('#reportsColumn').hidden ? $('#messageInput') : $('#reportQuestion')).focus();
    }
  });

  loadHealth();
  loadModels();
  loadMetrics();
  loadRunningModels();
  setInterval(loadHealth, 15000);
  setInterval(loadMetrics, 3000);
  setInterval(loadRunningModels, 7000);
}

setup();
