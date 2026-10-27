'use strict';

(() => {
  const $ = (id) => document.getElementById(id);
  const ui = {
    shell: $('app-shell'), sidebar: $('sidebar'), backdrop: $('sidebar-backdrop'),
    sessions: $('session-list'), search: $('history-search'), welcome: $('welcome'),
    messages: $('messages'), scroller: $('conversation-scroll'), input: $('message-input'),
    send: $('send-button'), stop: $('stop-button'), progress: $('generation-status'),
    notification: $('notification'), notificationText: $('notification-text'),
    dialog: $('session-dialog'), dialogForm: $('dialog-form'), dialogTitle: $('dialog-title'),
    dialogInput: $('dialog-input'), dialogLabel: $('dialog-label'),
    dialogDescription: $('dialog-description'), dialogError: $('dialog-error'),
    dialogSubmit: $('dialog-submit'), dialogCancel: $('dialog-cancel'),
    model: $('model-select'), modelContext: $('model-context'),
  };
  const state = {
    sessions: [], selectedId: null, session: null, drafts: new Map(), jobs: new Map(),
    pending: new Map(), outcomes: new Map(), finishedJobs: new Set(), sending: false, loading: false,
    loadVersion: 0, pollBusy: false, statusBusy: false, dialogAction: null, menu: null,
    models: [], selectedModelId: null, modelsBusy: false, switchingModel: false, modelVersion: 0,
  };
  const activeStatuses = new Set(['pending', 'running']);
  const sessionPath = (id) => `/api/sessions/${encodeURIComponent(id)}`;
  const draftKey = () => state.selectedId || '__new__';
  const currentJob = () => [...state.jobs.values()].find((job) =>
    job.session_id === state.selectedId && activeStatuses.has(job.status));

  function icon(name) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.classList.add('icon');
    svg.setAttribute('aria-hidden', 'true');
    const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    use.setAttribute('href', `#icon-${name}`);
    svg.append(use);
    return svg;
  }

  function rememberSelection(id) {
    try {
      if (id) localStorage.setItem('bonsai-dotnet-selected-chat', id);
      else localStorage.removeItem('bonsai-dotnet-selected-chat');
    } catch (_) { /* Browser storage is optional. */ }
  }

  function savedSelection() {
    try { return localStorage.getItem('bonsai-dotnet-selected-chat'); }
    catch (_) { return null; }
  }

  async function api(path, options = {}) {
    const controller = new AbortController();
    const timer = window.setTimeout(() => controller.abort(), options.timeout || 15000);
    try {
      const response = await fetch(path, {
        method: options.method || 'GET', signal: controller.signal, cache: 'no-store',
        headers: options.body !== undefined ? { 'Content-Type': 'application/json' } : {},
        body: options.body !== undefined ? JSON.stringify(options.body) : undefined,
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) {
        const error = new Error(data.error || `Request failed (${response.status}).`);
        error.status = response.status;
        throw error;
      }
      return data;
    } catch (error) {
      if (error.name === 'AbortError') throw new Error('The server took too long to respond. Check that it is running, then try again.');
      if (error instanceof TypeError) throw new Error('Cannot connect to the server. Check that it is running; your completed chats are saved.');
      throw error;
    } finally {
      window.clearTimeout(timer);
    }
  }

  function notify(message) {
    ui.notificationText.textContent = message;
    ui.notification.hidden = false;
  }

  function clearNotification() {
    ui.notification.hidden = true;
    ui.notificationText.textContent = '';
  }

  function announce(message) { $('screen-reader-status').textContent = message; }

  function resizeComposer() {
    ui.input.style.height = 'auto';
    ui.input.style.height = `${Math.min(ui.input.scrollHeight, matchMedia('(max-width: 768px)').matches ? 160 : 220)}px`;
  }

  function saveDraft() { state.drafts.set(draftKey(), ui.input.value); }

  function restoreDraft() {
    ui.input.value = state.drafts.get(draftKey()) || '';
    resizeComposer();
    updateComposer();
  }

  function updateComposer() {
    const job = currentJob();
    ui.send.hidden = Boolean(job);
    ui.stop.hidden = !job;
    ui.send.disabled = !ui.input.value.trim() || state.sending || state.loading || state.switchingModel;
    ui.input.disabled = state.loading || state.sending;
    ui.progress.hidden = !job && !state.sending;
    if (job) ui.progress.textContent = job.progress || 'Thinking…';
    else if (state.sending) ui.progress.textContent = 'Starting your message…';
    else ui.progress.textContent = '';
    if (job) ui.input.placeholder = 'Draft your next message';
    else ui.input.placeholder = 'Message Bonsai';
    updateModelControl();
  }

  function updateModelControl() {
    const busy = [...state.jobs.values()].some((job) => activeStatuses.has(job.status));
    ui.model.disabled = !state.models.length || state.modelsBusy || state.switchingModel || state.sending || busy;
    if (state.selectedModelId) ui.model.value = state.selectedModelId;
    ui.model.title = busy || state.sending ? 'Wait for replies to finish before switching models.'
      : state.switchingModel ? 'Switching model…'
      : state.models.find((model) => model.id === state.selectedModelId)?.name || 'Choose a local model';
  }

  function renderModels() {
    ui.model.replaceChildren();
    if (!state.models.length) {
      const option = document.createElement('option');
      option.value = '';
      option.textContent = 'No models available';
      ui.model.append(option);
    }
    state.models.forEach((model) => {
      const option = document.createElement('option');
      option.value = model.id;
      option.textContent = `${model.name}${model.available ? '' : ' (not installed)'}`;
      option.disabled = !model.available;
      if (Number.isSafeInteger(model.context_size) && model.context_size > 0) {
        option.title = `${model.name} · ${model.context_size.toLocaleString()} tokens of context`;
      }
      ui.model.append(option);
    });
    updateContextDisplay(state.models.find((model) => model.id === state.selectedModelId)?.context_size);
    updateModelControl();
  }

  function updateContextDisplay(size) {
    const valid = Number.isSafeInteger(size) && size > 0;
    ui.modelContext.hidden = !valid;
    if (!valid) return;
    const label = size >= 1024 ? `${(size / 1024).toLocaleString(undefined, { maximumFractionDigits: 1 })}K` : size.toLocaleString();
    ui.modelContext.textContent = `${label} context · `;
    ui.modelContext.title = `${size.toLocaleString()} tokens of context`;
  }

  async function loadModels() {
    if (state.modelsBusy) return;
    state.modelsBusy = true;
    const version = state.modelVersion;
    updateModelControl();
    try {
      const result = await api('/api/models');
      state.models = result.models || [];
      if (version === state.modelVersion && !state.switchingModel) state.selectedModelId = result.selected_id;
      renderModels();
    } catch (error) {
      notify(error.message);
    } finally {
      state.modelsBusy = false;
      updateModelControl();
    }
  }

  function applyModelStatus(model, selectedId) {
    if (selectedId) state.selectedModelId = selectedId;
    updateContextDisplay(model.context_size ?? state.models.find((item) => item.id === state.selectedModelId)?.context_size);
    const modelState = model.state || (model.online ? 'ready' : 'error');
    const statusLabels = {
      ready: 'Model ready', configured: 'Ready to load', loading: 'Loading model',
      missing: 'Model unavailable', error: 'Model unavailable',
    };
    const available = model.online || modelState === 'configured';
    $('model-status').className = `model-status ${available ? 'online' : 'offline'}`;
    $('model-status-text').textContent = statusLabels[modelState] || (model.online ? 'Model ready' : 'Model unavailable');
    $('model-status').title = model.message || (available
      ? 'Your local model is ready. The first reply may load it into memory.'
      : 'This model is unavailable. Choose another model or check the server.');
    updateModelControl();
  }

  async function selectModel() {
    const id = ui.model.value;
    if (!id || id === state.selectedModelId || state.switchingModel) return;
    if (state.sending || [...state.jobs.values()].some((job) => activeStatuses.has(job.status))) {
      updateModelControl();
      return;
    }
    state.switchingModel = true;
    state.modelVersion += 1;
    clearNotification();
    updateComposer();
    $('model-status-text').textContent = 'Switching model';
    try {
      const result = await api('/api/models/select', { method: 'POST', body: { id }, timeout: 60000 });
      state.selectedModelId = result.selected_id;
      applyModelStatus(result.model || {}, result.selected_id);
      const name = state.models.find((model) => model.id === state.selectedModelId)?.name || 'Model';
      announce(`${name} selected.`);
    } catch (error) {
      notify(error.message);
      // A dropped POST response can still have changed the server selection.
      await loadModels();
      await checkStatus();
    } finally {
      state.switchingModel = false;
      updateComposer();
      checkStatus();
    }
  }

  function isNearBottom() {
    return ui.scroller.scrollHeight - ui.scroller.scrollTop - ui.scroller.clientHeight < 160;
  }

  function scrollToBottom() {
    requestAnimationFrame(() => { ui.scroller.scrollTop = ui.scroller.scrollHeight; });
  }

  async function copyText(text, button, label = false) {
    try {
      await navigator.clipboard.writeText(text);
      button.replaceChildren(icon('check'));
      if (label) button.append(document.createTextNode('Copied'));
      button.setAttribute('aria-label', 'Copied');
      window.setTimeout(() => {
        if (!button.isConnected) return;
        button.replaceChildren(icon('copy'));
        if (label) button.append(document.createTextNode('Copy code'));
        button.setAttribute('aria-label', label ? 'Copy code' : 'Copy response');
      }, 1800);
      announce('Copied to clipboard.');
    } catch (_) {
      notify('Clipboard access is unavailable. You can select the text and copy it manually.');
    }
  }

  function markdown(content) {
    const body = document.createElement('div');
    body.className = 'markdown';
    if (!window.marked || !window.DOMPurify) {
      body.textContent = content;
      return body;
    }
    body.innerHTML = DOMPurify.sanitize(marked.parse(content, { gfm: true, breaks: false }), {
      USE_PROFILES: { html: true },
      FORBID_TAGS: ['style', 'form', 'input', 'button', 'textarea', 'select', 'iframe', 'object', 'embed'],
      FORBID_ATTR: ['style', 'id', 'name'],
    });
    body.querySelectorAll('a').forEach((link) => {
      link.target = '_blank';
      link.rel = 'noopener noreferrer';
    });
    body.querySelectorAll('pre').forEach((pre) => {
      const code = pre.querySelector('code');
      if (!code) return;
      const source = code.textContent;
      const language = [...code.classList].find((name) => name.startsWith('language-'))?.slice(9) || '';
      if (window.hljs) {
        try {
          if (!language || hljs.getLanguage(language)) hljs.highlightElement(code);
        } catch (_) { /* Unknown languages still display their original code. */ }
      }
      const block = document.createElement('div');
      block.className = 'code-block';
      const header = document.createElement('div');
      header.className = 'code-header';
      const title = document.createElement('span');
      title.textContent = language || 'code';
      const copy = document.createElement('button');
      copy.type = 'button';
      copy.className = 'copy-code';
      copy.setAttribute('aria-label', 'Copy code');
      copy.append(icon('copy'), document.createTextNode('Copy code'));
      copy.addEventListener('click', () => copyText(source, copy, true));
      header.append(title, copy);
      pre.before(block);
      block.append(header, pre);
    });
    body.querySelectorAll('table').forEach((table) => {
      const wrapper = document.createElement('div');
      wrapper.className = 'table-wrapper';
      table.before(wrapper);
      wrapper.append(table);
    });
    return body;
  }

  function messageElement(message) {
    const article = document.createElement('article');
    const assistant = message.role === 'assistant';
    article.className = `message ${assistant ? 'assistant-message' : 'user-message'}`;
    article.setAttribute('aria-label', assistant ? 'Bonsai response' : 'Your message');
    if (assistant) {
      const heading = document.createElement('div');
      heading.className = 'assistant-heading';
      const avatar = document.createElement('span');
      avatar.className = 'assistant-avatar';
      avatar.append(icon('leaf'));
      heading.append(avatar, document.createTextNode('Bonsai'));
      article.append(heading);
    }
    article.append(markdown(message.content || ''));
    if (assistant && message.content) {
      const actions = document.createElement('div');
      actions.className = 'message-actions';
      const copy = document.createElement('button');
      copy.className = 'icon-button';
      copy.type = 'button';
      copy.title = 'Copy response';
      copy.setAttribute('aria-label', 'Copy response');
      copy.append(icon('copy'));
      copy.addEventListener('click', () => copyText(message.content, copy));
      actions.append(copy);
      article.append(actions);
    }
    return article;
  }

  function renderConversation(forceBottom = false) {
    const keepBottom = forceBottom || isNearBottom();
    ui.messages.replaceChildren();
    if (state.loading) {
      ui.welcome.hidden = true;
      const loading = document.createElement('p');
      loading.className = 'loading-conversation';
      loading.textContent = 'Opening your conversation…';
      ui.messages.append(loading);
      return;
    }
    const messages = state.session?.messages || [];
    const pending = state.pending.get(state.selectedId);
    const job = currentJob();
    ui.welcome.hidden = messages.length > 0 || Boolean(pending) || Boolean(job);
    messages.forEach((message) => ui.messages.append(messageElement(message)));
    if (pending || job) {
      ui.messages.append(messageElement({ role: 'user', content: pending?.content || job.content || '' }));
      const thinking = messageElement({ role: 'assistant', content: job?.response || '' });
      if (!job?.response) {
        const dots = document.createElement('div');
        dots.className = 'typing-indicator';
        dots.setAttribute('aria-label', 'Generating reply');
        dots.append(document.createElement('span'), document.createElement('span'), document.createElement('span'));
        thinking.append(dots);
      }
      ui.messages.append(thinking);
    }
    if (keepBottom) scrollToBottom();
  }

  function upsertSession(session) {
    if (!session?.id) return;
    const summary = { ...session };
    delete summary.messages;
    delete summary.active_job;
    const index = state.sessions.findIndex((item) => item.id === session.id);
    if (index < 0) state.sessions.push(summary);
    else state.sessions[index] = summary;
    state.sessions.sort((a, b) => String(b.updated_at).localeCompare(String(a.updated_at)));
  }

  function timeGroup(session) {
    const date = new Date(session.updated_at || session.created_at);
    if (Number.isNaN(date.getTime())) return 'Earlier';
    const today = new Date(); today.setHours(0, 0, 0, 0);
    if (date >= today) return 'Today';
    const yesterday = new Date(today); yesterday.setDate(yesterday.getDate() - 1);
    if (date >= yesterday) return 'Yesterday';
    const week = new Date(today); week.setDate(week.getDate() - 7);
    if (date >= week) return 'Previous 7 days';
    return 'Earlier';
  }

  function closeMenu() {
    state.menu?.remove();
    state.menu = null;
    ui.sessions.querySelectorAll('[aria-expanded="true"]').forEach((button) => button.setAttribute('aria-expanded', 'false'));
  }

  function openMenu(session, row, button) {
    const same = state.menu?.dataset.sessionId === session.id;
    closeMenu();
    if (same) return;
    const menu = document.createElement('div');
    menu.className = 'session-menu';
    menu.dataset.sessionId = session.id;
    menu.setAttribute('role', 'menu');
    for (const [action, label] of [['rename', 'Rename'], ['delete', 'Delete chat']]) {
      const choice = document.createElement('button');
      choice.type = 'button';
      choice.textContent = label;
      choice.setAttribute('role', 'menuitem');
      if (action === 'delete') choice.className = 'delete-action';
      choice.addEventListener('click', () => { closeMenu(); openDialog(action, session); });
      menu.append(choice);
    }
    row.append(menu);
    state.menu = menu;
    button.setAttribute('aria-expanded', 'true');
    menu.querySelector('button').focus();
  }

  function renderSessions() {
    closeMenu();
    ui.sessions.replaceChildren();
    const search = ui.search.value.trim().toLocaleLowerCase();
    const sessions = state.sessions.filter((session) => String(session.title).toLocaleLowerCase().includes(search));
    if (!sessions.length) {
      const empty = document.createElement('p');
      empty.className = 'history-empty';
      empty.textContent = search ? 'No chats match your search.' : 'Your conversations will appear here. Start a new chat to begin.';
      ui.sessions.append(empty);
      return;
    }
    const groups = new Map();
    sessions.forEach((session) => {
      const label = timeGroup(session);
      if (!groups.has(label)) {
        const section = document.createElement('section');
        section.className = 'history-group';
        const heading = document.createElement('h2');
        heading.textContent = label;
        section.append(heading);
        groups.set(label, section);
        ui.sessions.append(section);
      }
      const row = document.createElement('div');
      row.className = `session-row${session.id === state.selectedId ? ' active' : ''}`;
      const select = document.createElement('button');
      select.className = 'session-link';
      select.type = 'button';
      select.textContent = session.title || 'New chat';
      select.title = session.title || 'New chat';
      if (session.id === state.selectedId) select.setAttribute('aria-current', 'page');
      select.addEventListener('click', () => selectSession(session.id));
      const options = document.createElement('button');
      options.className = 'icon-button session-menu-button';
      options.type = 'button';
      options.setAttribute('aria-label', `Options for ${session.title || 'chat'}`);
      options.setAttribute('aria-haspopup', 'menu');
      options.setAttribute('aria-expanded', 'false');
      options.append(icon('more'));
      options.addEventListener('click', (event) => { event.stopPropagation(); openMenu(session, row, options); });
      row.append(select);
      if ([...state.jobs.values()].some((job) => job.session_id === session.id && activeStatuses.has(job.status))) {
        const dot = document.createElement('span');
        dot.className = 'session-running';
        dot.title = 'Generating a reply';
        row.append(dot);
      }
      row.append(options);
      groups.get(label).append(row);
    });
  }

  async function refreshSessions() {
    const result = await api('/api/sessions');
    state.sessions = result.sessions || [];
    renderSessions();
  }

  function closeMobileSidebar() {
    ui.shell.classList.remove('sidebar-open-mobile');
    ui.backdrop.hidden = true;
    $('sidebar-open').setAttribute('aria-expanded', matchMedia('(max-width: 768px)').matches ? 'false' : String(!ui.shell.classList.contains('sidebar-collapsed')));
  }

  function toggleSidebar() {
    if (matchMedia('(max-width: 768px)').matches) {
      const open = ui.shell.classList.toggle('sidebar-open-mobile');
      ui.backdrop.hidden = !open;
      $('sidebar-open').setAttribute('aria-expanded', String(open));
    } else {
      ui.shell.classList.toggle('sidebar-collapsed');
      $('sidebar-open').setAttribute('aria-expanded', String(!ui.shell.classList.contains('sidebar-collapsed')));
    }
  }

  function newChat() {
    if (state.sending) return;
    saveDraft();
    state.loadVersion += 1;
    state.loading = false;
    state.selectedId = null;
    state.session = null;
    rememberSelection(null);
    closeMobileSidebar();
    clearNotification();
    restoreDraft();
    renderSessions();
    renderConversation(true);
    document.title = 'Bonsai · Local chat';
    ui.input.focus();
  }

  async function selectSession(id) {
    if (state.sending) return;
    saveDraft();
    closeMobileSidebar();
    clearNotification();
    const version = ++state.loadVersion;
    state.selectedId = id;
    state.session = null;
    state.loading = true;
    rememberSelection(id);
    restoreDraft();
    renderSessions();
    renderConversation(true);
    try {
      const { session } = await api(sessionPath(id));
      if (version !== state.loadVersion) return;
      state.session = session;
      upsertSession(session);
      if (session.active_job) registerJob(session.active_job);
      if (state.outcomes.has(id)) notify(state.outcomes.get(id));
      document.title = `${session.title || 'Chat'} · Bonsai`;
    } catch (error) {
      if (version !== state.loadVersion) return;
      notify(error.message);
      if (error.status === 404) {
        state.sessions = state.sessions.filter((session) => session.id !== id);
        state.selectedId = null;
        rememberSelection(null);
      }
    } finally {
      if (version === state.loadVersion) {
        state.loading = false;
        restoreDraft();
        renderSessions();
        renderConversation(true);
      }
    }
  }

  function registerJob(job) {
    if (state.finishedJobs.has(job.id)) return;
    const previous = state.jobs.get(job.id);
    const existing = Boolean(previous);
    state.jobs.set(job.id, job);
    if (activeStatuses.has(job.status)) {
      state.pending.set(job.session_id, { content: job.content || '', jobId: job.id });
      if (!existing) renderSessions();
    }
    if (job.session_id === state.selectedId && job.response !== previous?.response) renderConversation();
    updateComposer();
  }

  async function finishJob(job) {
    if (!state.jobs.has(job.id)) return;
    state.finishedJobs.add(job.id);
    state.jobs.delete(job.id);
    state.pending.delete(job.session_id);
    updateModelControl();
    const selected = job.session_id === state.selectedId;
    const keepBottom = isNearBottom();
    if (job.status === 'completed') {
      state.outcomes.delete(job.session_id);
      try {
        const session = job.session || (await api(sessionPath(job.session_id))).session;
        upsertSession(session);
        if (job.session_id === state.selectedId) {
          state.session = session;
          document.title = `${session.title || 'Chat'} · Bonsai`;
          announce('Bonsai has finished replying.');
        }
      } catch (error) { if (selected) notify(error.message); }
    } else {
      const message = job.status === 'cancelled'
        ? 'Generation stopped. Your message is back in the composer.'
        : (job.error || 'The reply could not be completed. Your message is back in the composer; try again.');
      state.outcomes.set(job.session_id, message);
      const draft = state.drafts.get(job.session_id) || '';
      if (job.content && !draft.trim()) state.drafts.set(job.session_id, job.content);
      else if (job.content && draft !== job.content) state.drafts.set(job.session_id, `${job.content}\n\n${draft}`);
      if (selected) {
        notify(message);
        restoreDraft();
        announce(job.status === 'cancelled' ? 'Generation stopped.' : 'Reply failed. Your message has been restored.');
      }
    }
    renderSessions();
    if (selected) {
      renderConversation(keepBottom);
      updateComposer();
    }
  }

  async function pollJobs() {
    if (state.pollBusy || !state.jobs.size) return;
    state.pollBusy = true;
    try {
      await Promise.all([...state.jobs.values()].map(async (oldJob) => {
        try {
          const { job } = await api(`/api/jobs/${encodeURIComponent(oldJob.id)}`, { timeout: 6000 });
          if (!state.jobs.has(job.id)) return;
          if (activeStatuses.has(job.status)) registerJob(job);
          else await finishJob(job);
        } catch (error) {
          if (!state.jobs.has(oldJob.id)) return;
          if (error.status === 404) {
            await finishJob({ ...oldJob, status: 'failed', error: 'The server restarted before this reply finished. Your message is restored. Reload the chat to check saved replies.' });
            if (oldJob.session_id === state.selectedId) {
              const result = await api(sessionPath(oldJob.session_id)).catch(() => null);
              if (result && oldJob.session_id === state.selectedId) {
                state.session = result.session;
                renderConversation();
              }
            }
          } else if (oldJob.session_id === state.selectedId) {
            ui.progress.textContent = 'Connection interrupted. Reconnecting to your reply…';
          }
        }
      }));
    } finally { state.pollBusy = false; }
  }

  async function checkStatus() {
    if (state.statusBusy) return;
    state.statusBusy = true;
    const version = state.modelVersion;
    try {
      const result = await api('/api/status', { timeout: 6000 });
      if (version === state.modelVersion && !state.switchingModel) applyModelStatus(result.model || {}, result.selected_model_id);
      for (const job of result.active_jobs || []) {
        const newJob = !state.jobs.has(job.id);
        registerJob(job);
        if (newJob && job.session_id === state.selectedId) renderConversation();
      }
    } catch (_) {
      $('model-status').className = 'model-status offline';
      $('model-status-text').textContent = 'Server offline';
      $('model-status').title = 'Check that the web server is running';
    } finally { state.statusBusy = false; updateModelControl(); }
  }

  async function sendMessage(event) {
    event?.preventDefault();
    const content = ui.input.value.trim();
    if (!content || state.sending || state.loading || state.switchingModel || currentJob()) return;
    if (content.length > 100000) {
      notify('This message is too long. Use at most 100,000 characters.');
      return;
    }
    saveDraft();
    clearNotification();
    state.sending = true;
    updateComposer();
    let id = state.selectedId;
    const previousMessageCount = state.session?.messages?.length || 0;
    try {
      if (!id) {
        const { session } = await api('/api/sessions', { method: 'POST', body: {} });
        id = session.id;
        state.selectedId = id;
        state.session = session;
        state.drafts.set(id, ui.input.value);
        state.drafts.delete('__new__');
        rememberSelection(id);
        upsertSession(session);
        renderSessions();
      }
      state.pending.set(id, { content });
      renderConversation(true);
      const { job } = await api(`${sessionPath(id)}/messages`, { method: 'POST', body: { content } });
      state.drafts.set(id, '');
      ui.input.value = '';
      resizeComposer();
      state.outcomes.delete(id);
      registerJob(job);
      if (!activeStatuses.has(job.status)) await finishJob(job);
      announce('Your message was sent. Bonsai is thinking.');
    } catch (error) {
      state.pending.delete(id);
      notify(error.message);
      renderConversation(true);
      // A lost POST response may still have started the job. Recover it safely.
      if (id) {
        const result = await api(sessionPath(id)).catch(() => null);
        if (result?.session.active_job) {
          state.session = result.session;
          state.drafts.set(id, '');
          ui.input.value = '';
          registerJob(result.session.active_job);
          clearNotification();
          renderConversation(true);
        } else if (result?.session.messages?.length > previousMessageCount
          && result.session.messages.at(-2)?.role === 'user'
          && result.session.messages.at(-2)?.content.trim() === content
          && result.session.messages.at(-1)?.role === 'assistant') {
          state.session = result.session;
          state.drafts.set(id, '');
          ui.input.value = '';
          upsertSession(result.session);
          clearNotification();
          renderSessions();
          renderConversation(true);
        }
      }
    } finally {
      state.sending = false;
      restoreDraft();
      ui.input.focus();
    }
  }

  async function stopGenerating() {
    const job = currentJob();
    if (!job) return;
    ui.stop.disabled = true;
    ui.progress.textContent = 'Stopping…';
    try {
      const result = await api(`/api/jobs/${encodeURIComponent(job.id)}/cancel`, { method: 'POST', body: {} });
      if (activeStatuses.has(result.job.status)) registerJob(result.job);
      else await finishJob(result.job);
    } catch (error) { notify(error.message); }
    finally { ui.stop.disabled = false; updateComposer(); }
  }

  function openDialog(action, session) {
    state.dialogAction = { action, session };
    ui.dialogError.hidden = true;
    ui.dialogDescription.hidden = action !== 'delete';
    ui.dialogLabel.hidden = action === 'delete';
    ui.dialogInput.hidden = action === 'delete';
    ui.dialogInput.required = action === 'rename';
    ui.dialogInput.value = session.title || 'New chat';
    ui.dialogTitle.textContent = action === 'delete' ? 'Delete this chat?' : 'Rename chat';
    ui.dialogDescription.textContent = `“${session.title || 'New chat'}” will be permanently deleted from this computer.`;
    ui.dialogSubmit.textContent = action === 'delete' ? 'Delete' : 'Save';
    ui.dialogSubmit.classList.toggle('danger', action === 'delete');
    ui.dialog.showModal();
    if (action === 'rename') { ui.dialogInput.focus(); ui.dialogInput.select(); }
    else ui.dialogCancel.focus();
  }

  async function submitDialog(event) {
    event.preventDefault();
    if (!state.dialogAction) return;
    const { action, session } = state.dialogAction;
    const title = ui.dialogInput.value.trim();
    if (action === 'rename' && !title) { ui.dialogInput.focus(); return; }
    ui.dialogSubmit.disabled = true;
    ui.dialogCancel.disabled = true;
    ui.dialogError.hidden = true;
    try {
      if (action === 'rename') {
        const result = await api(sessionPath(session.id), { method: 'PATCH', body: { title } });
        upsertSession(result.session);
        if (state.selectedId === session.id) {
          state.session = result.session;
          document.title = `${title} · Bonsai`;
        }
      } else {
        await api(sessionPath(session.id), { method: 'DELETE' });
        state.sessions = state.sessions.filter((item) => item.id !== session.id);
        state.drafts.delete(session.id);
        state.outcomes.delete(session.id);
        if (state.selectedId === session.id) newChat();
      }
      ui.dialog.close();
      state.dialogAction = null;
      renderSessions();
      announce(action === 'rename' ? 'Chat renamed.' : 'Chat deleted.');
    } catch (error) {
      ui.dialogError.textContent = error.message;
      ui.dialogError.hidden = false;
    } finally {
      ui.dialogSubmit.disabled = false;
      ui.dialogCancel.disabled = false;
    }
  }

  $('composer-form').addEventListener('submit', sendMessage);
  ui.input.addEventListener('input', () => { saveDraft(); resizeComposer(); updateComposer(); });
  ui.input.addEventListener('keydown', (event) => {
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      sendMessage();
    }
  });
  $('new-chat').addEventListener('click', newChat);
  $('top-new-chat').addEventListener('click', newChat);
  $('sidebar-open').addEventListener('click', toggleSidebar);
  $('sidebar-close').addEventListener('click', () => {
    if (matchMedia('(max-width: 768px)').matches) closeMobileSidebar();
    else toggleSidebar();
  });
  ui.backdrop.addEventListener('click', closeMobileSidebar);
  ui.search.addEventListener('input', renderSessions);
  ui.stop.addEventListener('click', stopGenerating);
  ui.model.addEventListener('change', selectModel);
  $('dismiss-notification').addEventListener('click', clearNotification);
  ui.dialogForm.addEventListener('submit', submitDialog);
  $('dialog-cancel').addEventListener('click', () => ui.dialog.close());
  $('dialog-close').addEventListener('click', () => ui.dialog.close());
  ui.dialog.addEventListener('click', (event) => {
    if (event.target === ui.dialog && !ui.dialogSubmit.disabled) ui.dialog.close();
  });
  document.querySelectorAll('.suggestion').forEach((button) => {
    button.addEventListener('click', () => {
      ui.input.value = button.dataset.prompt;
      saveDraft(); resizeComposer(); updateComposer(); ui.input.focus();
    });
  });
  document.addEventListener('click', (event) => {
    if (!event.target.closest('.session-menu') && !event.target.closest('.session-menu-button')) closeMenu();
  });
  document.addEventListener('keydown', (event) => {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      if (!ui.dialog.open) newChat();
    }
    if (event.key === 'Escape') { closeMenu(); closeMobileSidebar(); }
  });
  window.addEventListener('resize', resizeComposer);
  window.addEventListener('focus', () => { checkStatus(); loadModels(); refreshSessions().catch(() => {}); });
  document.addEventListener('visibilitychange', () => { if (!document.hidden) { pollJobs(); checkStatus(); } });

  async function initialize() {
    closeMobileSidebar();
    restoreDraft();
    loadModels();
    checkStatus();
    try {
      await refreshSessions();
      const selected = savedSelection();
      if (selected && state.sessions.some((session) => session.id === selected)) await selectSession(selected);
    } catch (error) {
      renderSessions();
      notify(error.message);
    }
    window.setInterval(pollJobs, 900);
    window.setInterval(checkStatus, 20000);
  }
  initialize();
})();
