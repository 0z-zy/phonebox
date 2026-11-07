// PhoneBox web demo: answers the panel's and the settings page's API calls in the browser with made-up data,
// so both run on a static site (GitHub Pages) without a phone or an agent. Load it before app.js / the settings script.
// The panel and the settings page talk through a BroadcastChannel: layout changes and test notifications show up live.
(() => {
  'use strict';
  const ROOT = new URL('.', document.currentScript.src).href;
  const realFetch = window.fetch.bind(window);
  const bc = new BroadcastChannel('phonebox-demo');
  const START = Date.now();
  const now = () => Date.now();
  const rnd = (a, b) => a + Math.random() * (b - a);
  const wave = (period, lo, hi, phase = 0, noise = 0) =>
    lo + (hi - lo) * (0.5 + 0.5 * Math.sin((2 * Math.PI * (now() - START)) / 1000 / period + phase)) + rnd(-noise, noise);

  const SHORTCUTS = [
    ['terminal', 'Terminal', '⌨️', '#4b5563'], ['code', 'VS Code', '📝', '#2f80ed'], ['browser', 'Browser', '🌐', '#0ea5e9'],
    ['files', 'Files', '📁', '#e5b84b'], ['music', 'Spotify', '🎧', '#1db954'], ['chat', 'Discord', '💬', '#6366f1'],
    ['snip', 'Snip', '✂️', '#14b8a6'], ['taskmgr', 'Task Manager', '📊', '#22c55e'], ['mute', 'Mute', '🔇', '#f59e0b'],
    ['screenoff', 'Screen Off', '🌙', '#8b5cf6'], ['lock', 'Lock', '🔒', '#ef4444'], ['settings', 'Settings', '⚙️', '#94a3b8'],
  ].map(([id, label, icon, color]) => ({ id, label, icon, color, cmd: 'demo' }));
  const PAGES = ['home', 'dashboard', 'ai', 'work', 'system', 'procs', 'deck', 'music', 'status', 'ambient', 'world', 'daily', 'markets', 'feeds', 'focus', 'calm', 'clock'];

  let catalog = null;
  async function loadCatalog() {
    if (catalog) return catalog;
    const [widgets, templates] = await Promise.all([realFetch(ROOT + 'widgets.json').then((r) => r.json()), realFetch(ROOT + 'templates.json').then((r) => r.json())]);
    return (catalog = { cols: 12, rows: 6, widgets, templates });
  }

  const store = {
    get(k, d) { try { const v = localStorage.getItem(k); return v ? JSON.parse(v) : d; } catch (_) { return d; } },
    set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch (_) {} },
  };

  async function config() {
    let c = store.get('pb-demo-config', null);
    if (c) return c;
    const { templates } = await loadCatalog();
    const byId = Object.fromEntries(templates.map((t) => [t.id, t]));
    c = {
      weather: { city: 'Tallinn', lat: 59.437, lon: 24.745 },
      night: { enabled: false, start: '00:30', end: '07:30', brightness: 0.02 },
      display: { showSeconds: true, idleReturnSec: 0, vibrate: true, volumeKeys: true },
      lan: { enabled: true, port: 8766, token: 'demo', webhookToken: 'demo-webhook-key' },
      updates: { auto: true, repo: 'ersinkoc/phonebox' },
      claude: { phoneApprovals: true, timeoutSec: 90, onlyWhenAway: false },
      pages: PAGES.map((id) => {
        const t = byId[id], seen = {};
        return { id, title: t.title, icon: t.icon, enabled: true, widgets: t.widgets.map((w) => {
          const n = (seen[w.type] = (seen[w.type] || 0) + 1);
          return { ...w, id: n === 1 ? w.type : `${w.type}-${n}`, options: { ...(w.options || {}) } };
        }) };
      }),
      shortcuts: SHORTCUTS,
    };
    store.set('pb-demo-config', c);
    return c;
  }
  const cfgVersion = () => store.get('pb-demo-version', 1);

  // ---- webhook-style events (notifications, page switches) shared between tabs
  const events = [];
  let seq = 0;
  function emit(kind, data, broadcast = true) {
    const e = { seq: ++seq, kind, data, at: now() };
    events.push(e);
    if (events.length > 50) events.shift();
    if (broadcast) bc.postMessage({ type: 'event', kind, data });
    return e;
  }
  const push = {
    temperature: { data: 21.5, at: now() - 180e3 },
    build: { data: { value: 'passed', sub: 'main · 3m 12s' }, at: now() - 240e3 },
    deploy: { data: { value: 'running', sub: 'production' }, at: now() - 60e3 },
    progress: { data: { value: 64 }, at: now() - 30e3 },
    todo: { data: { items: [{ label: 'Try the layout editor', value: 'now', level: 'warn' }, { label: 'Widgets', value: 'done', level: 'ok' },
      { label: 'Webhooks', value: 'done', level: 'ok' }, 'Water the plants'] }, at: now() - 600e3 },
  };
  let pushVersion = 1;
  let approvals = [];
  const SAMPLE_PROMPTS = [
    { tool: 'Run a command', detail: 'git push origin main --force-with-lease', description: 'Push the rebased branch', project: 'api-gateway' },
    { tool: 'Edit a file', detail: 'src/routes/billing.ts\n\n  export const limit = rateLimit({ windowMs: 60_000, max: 120 })', description: '', project: 'docs-site' },
    { tool: 'Fetch a web page', detail: 'https://docs.github.com/en/rest/pulls', description: '', project: 'blog-engine' },
  ];
  function ask(sample, broadcast = true) {
    const s = sample || SAMPLE_PROMPTS[Math.floor(Math.random() * SAMPLE_PROMPTS.length)];
    const a = { id: Math.random().toString(16).slice(2, 10), source: 'Claude Code', session: 'demo', createdAt: now(), expiresAt: now() + 90e3, ...s };
    approvals.push(a);
    if (broadcast) bc.postMessage({ type: 'ask', sample: s, id: a.id });
    emit('approval', { id: a.id }, false);
    return a;
  }
  bc.onmessage = (m) => {
    const d = m.data || {};
    if (d.type === 'ask') { const a = ask(d.sample, false); a.id = d.id; }
    if (d.type === 'decided') { approvals = approvals.filter((x) => x.id !== d.id); emit('approval-done', { id: d.id }, false); }
    if (d.type === 'event') emit(d.kind, d.data, false);
    if (d.type === 'push') { push[d.key] = { data: d.value, at: now() }; pushVersion++; }
  };

  // ---- made-up computer
  function state(since) {
    const dur = 214000;
    return {
      update: null, ts: now(), host: 'DESK-PC', cfgVersion: cfgVersion(), pushVersion, evSeq: seq,
      events: since == null ? [] : events.filter((e) => e.seq > since),
      pc: {
        cpu: Math.max(1, wave(37, 8, 46, 0, 6)), cpuName: 'AMD Ryzen 9 7950X 16-Core Processor', cpuTemp: wave(60, 52, 68),
        ramUsedGb: wave(120, 21, 24), ramTotalGb: 64, netDownBps: Math.max(0, wave(23, 0.2e6, 9e6, 0, 1e6)), netUpBps: Math.max(0, wave(17, 40e3, 600e3, 1, 50e3)),
        uptimeSec: 3 * 86400 + 5 * 3600 + Math.floor((now() - START) / 1000),
        disks: [{ name: 'C:', usedGb: 612, totalGb: 1000 }, { name: 'D:', usedGb: 1450, totalGb: 2000 }, { name: 'E:', usedGb: 310, totalGb: 4000 }],
        gpu: { name: 'NVIDIA GeForce RTX 4080', util: Math.max(0, wave(29, 4, 38, 2, 4)), temp: wave(50, 46, 61), power: wave(29, 38, 140, 2),
          powerLimit: 320, clock: 2520, fan: 34, vramUsed: 5.8, vramTotal: 16 },
      },
      media: { app: 'Spotify', title: 'Midnight Circuit', artist: 'Neon Harbor', album: 'Low Tide Lights', durMs: dur,
        posMs: (now() - START + 61000) % dur, playing: true, canSeek: true, artId: 1 },
      audio: { volume: 42, muted: false },
      ping: { ms: wave(13, 9, 16), avgMs: 12, lossPct: 0 },
      // the microphone is "in use" for 20 s of every minute, to show the On air widget
      privacy: (() => { const on = (now() / 1000) % 60 < 20; return { mic: on, camera: false, micApps: on ? ['Zoom'] : [], cameraApps: [] }; })(),
    };
  }

  function quotas() {
    const n = now();
    return { providers: [
      { id: 'claude', name: 'Claude', plan: 'Max 5x', updatedAt: n - 120e3, extra: '', error: null, limits: [
        { label: '5-hour session', percent: 34, resetsAt: n + 2.6 * 3600e3, windowSec: 18000 },
        { label: 'Weekly', percent: 52, resetsAt: n + 3.2 * 86400e3, windowSec: 604800 },
        { label: 'Weekly · Fable', percent: 9, resetsAt: n + 3.2 * 86400e3, windowSec: 604800 }] },
      { id: 'codex', name: 'Codex', plan: 'Pro', updatedAt: n - 300e3, extra: 'Credits: 1,250', error: null, limits: [
        { label: '5-hour session', percent: 12, resetsAt: n + 4.1 * 3600e3, windowSec: 18000 },
        { label: 'Weekly', percent: 27, resetsAt: n + 5.5 * 86400e3, windowSec: 604800 }] },
    ] };
  }

  function work() {
    const n = now();
    const s = (i, tool, name, status, mins, waitingFor = null) => ({ id: `${tool}:${i}`, tool, name, project: name, status, waitingFor, since: n - mins * 60e3, startedAt: n - 3 * 3600e3 });
    const pr = (repo, number, title, ci = null, review = null, draft = false) => ({ repo, number, title, ci, review, draft });
    return {
      sessions: [s(1, 'claude', 'api-gateway', 'busy', 4), s(2, 'claude', 'docs-site', 'waiting', 2, 'approve a file edit'), s(3, 'codex', 'mobile-app', 'busy', 11),
        s(4, 'claude', 'infra', 'shell', 1), s(5, 'claude', 'blog-engine', 'idle', 38), s(6, 'codex', 'data-pipeline', 'idle', 74), s(7, 'claude', 'design-system', 'idle', 125)],
      github: { user: 'octocat', notifications: 7, error: null,
        reviewRequests: [pr('acme/api', 412, 'Rate limiter for public endpoints'), pr('acme/web', 1288, 'Dark mode for the dashboard'), pr('acme/infra', 77, 'Move staging to a new region')],
        myPrs: [pr('acme/api', 409, 'Cache user sessions in Redis', 'SUCCESS', 'APPROVED'), pr('acme/web', 1281, 'Faster image uploads', 'PENDING'),
          pr('acme/cli', 53, 'New "deploy" command', 'FAILURE', 'CHANGES_REQUESTED'), pr('acme/docs', 210, 'Getting started rewrite', null, null, true)] },
    };
  }

  const PROCS = [['code', 'Visual Studio Code', 9, 6.2, 2100], ['chrome', 'Google Chrome', 14, 4.8, 3400], ['node', 'Node.js', 6, 3.9, 1200],
    ['claude', 'Claude', 7, 2.7, 1800], ['docker', 'Docker Desktop', 4, 2.1, 2600], ['spotify', 'Spotify', 5, 1.4, 420], ['discord', 'Discord', 6, 1.1, 610],
    ['explorer', 'Windows Explorer', 3, 0.9, 380], ['obs64', 'OBS Studio', 1, 0.8, 520], ['slack', 'Slack', 5, 0.6, 700], ['steam', 'Steam', 3, 0.4, 340],
    ['onedrive', 'Microsoft OneDrive', 1, 0.3, 120], ['python', 'Python', 2, 0.3, 160], ['figma', 'Figma', 3, 0.2, 640]];
  const procs = () => ({ total: 312, groupCount: 141, groups: PROCS.map(([name, title, count, cpu, memMb]) =>
    ({ name, title, count, cpu: Math.round(cpu * rnd(0.8, 1.2) * 10) / 10, memMb, locked: name === 'explorer', hasIcon: false })) });
  function procDetail(name) {
    const p = PROCS.find((x) => x[0] === name) || PROCS[0];
    return { name: p[0], title: p[1], locked: p[0] === 'explorer', instances: Array.from({ length: p[2] }, (_, i) => ({
      pid: 4100 + i * 37, cpu: Math.round((p[3] / p[2]) * 10) / 10, memMb: Math.round(p[4] / p[2]), startedAt: now() - (i + 1) * 1800e3,
      locked: p[0] === 'explorer', session: p[0] === 'claude' && i < 3 ? ['api-gateway', 'docs-site', 'infra'][i] : null, window: null, args: i ? '--type=renderer' : null })) };
  }

  const ART = 'data:image/svg+xml,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 300 300"><defs>' +
    '<linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#ff5f6d"/><stop offset=".55" stop-color="#7b2ff7"/><stop offset="1" stop-color="#12c2e9"/></linearGradient>' +
    '<radialGradient id="s" cx=".5" cy=".42" r=".5"><stop offset="0" stop-color="#ffd86f"/><stop offset="1" stop-color="#ff6f61"/></radialGradient></defs>' +
    '<rect width="300" height="300" fill="url(#g)"/><circle cx="150" cy="130" r="70" fill="url(#s)"/><rect y="170" width="300" height="130" fill="#1b1037" opacity=".85"/>' +
    '<g stroke="#ff6fd8" stroke-width="3" opacity=".9">' + Array.from({ length: 9 }, (_, i) => `<line x1="0" y1="${180 + i * 14}" x2="300" y2="${180 + i * 14}"/>`).join('') +
    Array.from({ length: 13 }, (_, i) => `<line x1="150" y1="170" x2="${-300 + i * 75}" y2="300"/>`).join('') + '</g></svg>');

  // ---- router
  const json = (o, status = 200) => new Response(JSON.stringify(o), { status, headers: { 'Content-Type': 'application/json; charset=utf-8' } });
  const parse = (body) => { if (body == null || body === '') return null; try { return JSON.parse(body); } catch (_) { return String(body); } };

  async function handle(method, path, q, body) {
    if (path === '/api/state') return json(state(q.has('ev') ? +q.get('ev') : null));
    if (path === '/api/config') { const c = await config(); return json({ version: cfgVersion(), config: { ...c, shortcuts: c.shortcuts.map(({ id, label, icon, color }) => ({ id, label, icon, color })) } }); }
    if (path === '/api/quotas') return json(quotas());
    if (path === '/api/work') return json(work());
    if (path === '/api/procs') return json(procs());
    if (path === '/api/procs/detail') return json(procDetail(q.get('name')));
    if (path.startsWith('/api/widget/')) return json({ output: String(3 + Math.floor(Math.random() * 3)), at: now() - 8000, ok: true, running: false, durationMs: 412 });
    if (path === '/api/approvals' && method === 'GET') return json({ items: approvals.filter((a) => a.expiresAt > now()) });
    if (path.startsWith('/api/approvals/') && method === 'POST') {
      const id = decodeURIComponent(path.slice(15));
      if (!approvals.some((a) => a.id === id)) return json({}, 404);
      approvals = approvals.filter((a) => a.id !== id);
      bc.postMessage({ type: 'decided', id });
      return json({ ok: true });
    }
    if (path === '/api/push' && method === 'GET') return json({ version: pushVersion, items: push });
    if (path.startsWith('/api/push/') && method === 'POST') {
      const key = decodeURIComponent(path.slice(10)), value = parse(body);
      push[key] = { data: value, at: now() }; pushVersion++;
      bc.postMessage({ type: 'push', key, value });
      return json({ ok: true });
    }
    if (path === '/api/rules/test' && method === 'POST') {
      // runs the rule's action once, like the agent's Test button
      const r = parse(body) || {};
      if (r.then === 'goto' && r.page) emit('goto', { page: r.page });
      else if (r.then === 'notify') emit('notify', { title: r.name || 'PhoneBox', text: (r.text || 'test').replace('{detail}', 'test'), level: r.level || 'info' });
      else if (r.then === 'push' && r.key) {
        const value = r.pushValue ?? true;
        push[r.key] = { data: value, at: now() }; pushVersion++;
        bc.postMessage({ type: 'push', key: r.key, value });
      }
      return json({ ok: true });
    }
    if (path === '/api/notify' && method === 'POST') { const d = parse(body); return json(emit('notify', typeof d === 'object' && d ? d : { text: String(d) })); }
    if (path.startsWith('/api/goto/') && method === 'POST') return json(emit('goto', { page: decodeURIComponent(path.slice(10)) }));
    if (path === '/api/refresh' && method === 'POST') return json(emit('refresh'));
    // settings page
    if (path === '/api/catalog') return json(await loadCatalog());
    if (path === '/api/settings') {
      if (method === 'PUT') {
        const c = parse(body);
        (c.pages || []).forEach((p, i) => { p.id ||= `page-${i + 1}`; p.title ||= 'Page'; const seen = {}; (p.widgets || []).forEach((w) => { if (!w.id) { const n = (seen[w.type] = (seen[w.type] || 0) + 1); w.id = `${w.type}-${Date.now() % 100000}-${n}`; } }); });
        (c.shortcuts || []).forEach((s, i) => { s.id ||= `shortcut-${i + 1}`; });
        store.set('pb-demo-config', c);
        store.set('pb-demo-version', cfgVersion() + 1);
        return json({ version: cfgVersion() });
      }
      return json(await config());
    }
    if (path === '/api/info') return json({ version: 'demo', os: 'windows', configPath: 'browser storage (demo)', logPath: '—', adb: 'demo', connected: true, devices: ['DEMO0001'] });
    if (path === '/api/lan') return json({ configured: true, active: true, port: 8766, restartNeeded: false, hosts: ['192.168.1.20', '100.101.102.103'], fingerprint: 'demo' });
    if (path === '/api/update') return json({ status: { agentVersion: 'demo', appVersion: 'demo', latest: 'demo', agentUpdate: false, appUpdate: false, lastCheck: now() - 3600e3, state: 'idle' }, auto: true, installed: true });
    if (path === '/api/claude/hook') return json({ installed: false, path: '~/.claude/settings.json' });
    if (method !== 'GET') return json({ ok: true });
    return json({ error: 'not in the demo' }, 404);
  }

  window.fetch = async (input, init = {}) => {
    const url = new URL(typeof input === 'string' ? input : input.url, location.href);
    const i = url.pathname.indexOf('/api/');
    if (url.origin !== location.origin || i < 0) return realFetch(input, init);
    return handle((init.method || 'GET').toUpperCase(), url.pathname.slice(i), url.searchParams, init.body);
  };

  // Album art is an <img>, which fetch() does not see: swap its address for the demo cover.
  new MutationObserver((list) => {
    for (const m of list) {
      const img = m.target;
      if (img.tagName === 'IMG' && /\/api\/art/.test(img.getAttribute('src') || '')) img.src = ART;
    }
  }).observe(document.documentElement, { subtree: true, attributes: true, attributeFilter: ['src'] });

  // A pretend Android bridge for phone-only widgets.
  if (!window.Android && document.currentScript.dataset.panel !== undefined)
    window.Android = { battery: () => JSON.stringify({ level: 82, plugged: true, temp: 29.5 }), transport: () => 'usb', version: () => 'demo',
      haptic() {}, buzz() {}, setBrightness() {}, setVolumeKeys() {} };

  window.PhoneBoxDemo = { emit, ask, reset() { localStorage.removeItem('pb-demo-config'); store.set('pb-demo-version', cfgVersion() + 1); } };
})();
