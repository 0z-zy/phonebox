'use strict';

// PhoneBox desk panel. Talks to the PhoneBox agent through "/agent/*", which the Android shell
// proxies over USB (adb reverse) to 127.0.0.1:8765 on the computer.

const API = '/agent/api';
const LOCALE = 'en-GB';
const $ = (id) => document.getElementById(id);
const bridge = typeof Android !== 'undefined' ? Android : null;

const DEFAULT_CFG = {
  weather: { city: 'Tallinn', lat: 59.437, lon: 24.745 },
  night: { enabled: true, start: '00:30', end: '07:30', brightness: 0.02 },
  display: { showSeconds: true, idleReturnSec: 90, vibrate: true },
  pages: ['home', 'ai', 'work', 'system', 'procs', 'deck'].map((type) => ({ type, enabled: true })),
  shortcuts: [],
};

const store = {
  get(k, d) { try { const v = localStorage.getItem(k); return v ? JSON.parse(v) : d; } catch (_) { return d; } },
  set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch (_) {} },
};

let cfg = { ...DEFAULT_CFG, ...store.get('cfg', {}) };
let cfgVersion = null;
let online = null;
let lastArtId = -1;
let media = null;        // last media state + the time it was received
let volDragging = false;
let lastTouch = Date.now();
let wakeUntil = 0;
let nightOn = null;

const HIST = 90;
const hist = { cpu: [], gpu: [], ram: [], down: [], up: [] };
const push = (k, v) => { const a = hist[k]; a.push(v); if (a.length > HIST) a.shift(); };

// ---------------------------------------------------------------- helpers
async function api(path, method = 'GET', timeout = 2500) {
  const ctl = new AbortController();
  const t = setTimeout(() => ctl.abort(), timeout);
  try {
    const r = await fetch(API + path, { method, signal: ctl.signal, cache: 'no-store' });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    const ct = r.headers.get('content-type') || '';
    return ct.includes('json') ? r.json() : r.text();
  } finally { clearTimeout(t); }
}

const haptic = () => { try { bridge && bridge.haptic(); } catch (_) {} };
const buzz = (p) => { try { if (bridge && !nightOn && cfg.display?.vibrate !== false) bridge.buzz(p); } catch (_) {} };

function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text != null) e.textContent = text;
  return e;
}

function pageCfg(type) { return (cfg.pages || []).find((p) => p.type === type); }
function opt(type, key, fallback) {
  const o = pageCfg(type)?.options;
  return o && key in o ? o[key] : fallback;
}

const pad = (n) => String(n).padStart(2, '0');
const fmtTime = (ms) => { const s = Math.max(0, Math.floor(ms / 1000)); return Math.floor(s / 60) + ':' + pad(s % 60); };
function fmtRate(bps) {
  if (bps == null) return ['—', ''];
  if (bps >= 1e9) return [(bps / 1e9).toFixed(1), 'GB/s'];
  if (bps >= 1e6) return [(bps / 1e6).toFixed(bps >= 1e8 ? 0 : 1), 'MB/s'];
  if (bps >= 1e3) return [(bps / 1e3).toFixed(0), 'KB/s'];
  return [Math.round(bps).toString(), 'B/s'];
}
const rateStr = (b) => fmtRate(b).join(' ');
function fmtDur(ms) {
  const m = Math.max(0, Math.round(ms / 60000));
  if (m < 60) return `${m} min`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h} h ${m % 60} min`;
  const d = Math.floor(h / 24);
  return `${d} d ${h % 24} h`;
}
function shortDur(ms) {
  const m = Math.max(0, Math.floor(ms / 60000));
  if (m < 1) return 'now';
  if (m < 60) return m + 'm';
  const h = Math.floor(m / 60);
  return h < 24 ? `${h}h ${m % 60}m` : `${Math.floor(h / 24)}d`;
}
const r1 = (v) => (v == null ? '—' : (Math.round(v * 10) / 10).toString());
const chip = (txt, cls = '') => `<span class="chip ${cls}">${txt}</span>`;
const fmtMem = (mb) => (mb >= 1024 ? (mb / 1024).toFixed(1) + ' GB' : Math.round(mb) + ' MB');

function toast(msg, kind = '') {
  const t = $('toast');
  t.textContent = msg;
  t.className = 'toast on ' + kind;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => (t.className = 'toast ' + kind), 2600);
}

// ---------------------------------------------------------------- charts
function sizeCanvas(c) {
  const dpr = window.devicePixelRatio || 1;
  const w = Math.max(1, Math.round(c.clientWidth * dpr)), h = Math.max(1, Math.round(c.clientHeight * dpr));
  if (c.width !== w || c.height !== h) { c.width = w; c.height = h; }
  return [c.getContext('2d'), w, h, dpr];
}

const cssVar = (name) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();

/** Draws one or more series as area charts. series: [{ data, color }] */
function chart(canvas, series, max, opts = {}) {
  if (!canvas.clientWidth) return;
  const [g, w, h, dpr] = sizeCanvas(canvas);
  g.clearRect(0, 0, w, h);
  if (opts.grid) {
    g.strokeStyle = 'rgba(255,255,255,0.06)';
    g.lineWidth = 1;
    for (let i = 1; i < 4; i++) { const y = Math.round((h * i) / 4) + 0.5; g.beginPath(); g.moveTo(0, y); g.lineTo(w, y); g.stroke(); }
  }
  const top = 4 * dpr;
  for (const s of series) {
    const d = s.data;
    if (d.length < 2) continue;
    const step = w / (HIST - 1);
    const x0 = w - (d.length - 1) * step;
    const y = (v) => h - (Math.min(v, max) / max) * (h - top);
    g.beginPath();
    g.moveTo(x0, y(d[0]));
    for (let i = 1; i < d.length; i++) g.lineTo(x0 + i * step, y(d[i]));
    g.lineWidth = 1.6 * dpr;
    g.strokeStyle = s.color;
    g.lineJoin = 'round';
    g.stroke();
    g.lineTo(w, h);
    g.lineTo(x0, h);
    g.closePath();
    const grad = g.createLinearGradient(0, 0, 0, h);
    grad.addColorStop(0, s.color + '55');
    grad.addColorStop(1, s.color + '00');
    g.fillStyle = grad;
    g.fill();
  }
}

// ---------------------------------------------------------------- pages (order and visibility come from the settings)
const pager = $('pager');
const sections = Object.fromEntries([...document.querySelectorAll('[data-page]')].map((s) => [s.dataset.page, s]));
let visiblePages = [];

function applyLayout() {
  // page order + visibility
  const pages = (cfg.pages && cfg.pages.length ? cfg.pages : DEFAULT_CFG.pages).filter((p) => sections[p.type]);
  const prevType = currentType();
  visiblePages = [];
  for (const p of pages) {
    const sec = sections[p.type];
    pager.appendChild(sec); // reorders the DOM
    sec.hidden = !p.enabled;
    if (p.enabled) visiblePages.push(p.type);
  }
  if (!visiblePages.length) { sections.home.hidden = false; visiblePages = ['home']; }
  const dots = $('dots');
  dots.replaceChildren(...visiblePages.map((_, i) => {
    const d = el('i');
    d.addEventListener('click', () => goPage(i));
    return d;
  }));
  const idx = Math.max(0, visiblePages.indexOf(prevType));
  pager.scrollLeft = idx * window.innerWidth;
  updateDots();

  // home page widgets
  const home = sections.home;
  home.classList.toggle('no-weather', !opt('home', 'weather', true));
  home.classList.toggle('no-media', !opt('home', 'media', true));
  const tiles = opt('home', 'tiles', ['cpu', 'gpu', 'ram', 'net']);
  document.querySelectorAll('[data-tile]').forEach((t) => (t.hidden = !tiles.includes(t.dataset.tile)));
  $('tiles').hidden = tiles.length === 0;
  $('tiles').style.gridTemplateColumns = `repeat(${Math.max(1, tiles.length)}, 1fr)`;
  $('ss').hidden = cfg.display?.showSeconds === false;

  // sessions & github
  const showSessions = opt('work', 'sessions', true), showGh = opt('work', 'github', true);
  $('wSessions').hidden = !showSessions;
  $('wGithub').hidden = !showGh;
  sections.work.style.gridTemplateColumns = showSessions && showGh ? '' : '1fr';

  renderDeck();
  renderQuotas();
}

function currentType() { return visiblePages[currentPage()] || 'home'; }
function currentPage() { return Math.round(pager.scrollLeft / window.innerWidth); }
function goPage(i) { pager.scrollTo({ left: i * window.innerWidth, behavior: 'smooth' }); }
function updateDots() {
  const p = currentPage();
  [...$('dots').children].forEach((d, i) => d.classList.toggle('on', i === p));
}
pager.addEventListener('scroll', updateDots, { passive: true });
window.onAndroidBack = () => { if (sheet) closeSheet(); else goPage(0); };

['pointerdown', 'touchstart'].forEach((ev) => document.addEventListener(ev, () => {
  lastTouch = Date.now();
  if (nightOn) { wakeUntil = Date.now() + 30_000; setNight(false); }
}, { passive: true, capture: true }));

// ---------------------------------------------------------------- clock & night mode
function parseHM(s) { const [h, m] = (s || '0:0').split(':').map(Number); return h * 60 + (m || 0); }
function isNight(now) {
  const n = cfg.night || {};
  if (!n.enabled) return false;
  const t = now.getHours() * 60 + now.getMinutes();
  const a = parseHM(n.start), b = parseHM(n.end);
  return a <= b ? t >= a && t < b : t >= a || t < b;
}

function setNight(on) {
  if (on === nightOn) return;
  nightOn = on;
  document.body.classList.toggle('night', on);
  try { bridge && bridge.setBrightness(on ? (cfg.night.brightness ?? 0.02) : -1); } catch (_) {}
}

function tickClock() {
  const now = new Date();
  const hh = pad(now.getHours()), mm = pad(now.getMinutes());
  $('hh').textContent = hh;
  $('mm').textContent = mm;
  $('ss').textContent = pad(now.getSeconds());
  const date = now.toLocaleDateString(LOCALE, { weekday: 'long', day: 'numeric', month: 'long' });
  $('date').textContent = date;
  $('nClock').textContent = hh + ':' + mm;
  $('nDate').textContent = date;

  setNight(isNight(now) && Date.now() > wakeUntil);

  const idle = cfg.display?.idleReturnSec ?? 90;
  if (idle > 0 && Date.now() - lastTouch > idle * 1000 && (currentPage() !== 0 || sheet)) { if (sheet) closeSheet(); goPage(0); }

  setTimeout(tickClock, 1000 - (Date.now() % 1000) + 5);
}

// ---------------------------------------------------------------- weather (Open-Meteo, straight from the phone)
const WX = {
  0: ['Clear', '☀️', '🌙'], 1: ['Mostly clear', '🌤️', '🌙'], 2: ['Partly cloudy', '⛅', '☁️'], 3: ['Overcast', '☁️', '☁️'],
  45: ['Fog', '🌫️'], 48: ['Rime fog', '🌫️'],
  51: ['Light drizzle', '🌦️'], 53: ['Drizzle', '🌦️'], 55: ['Heavy drizzle', '🌧️'], 56: ['Freezing drizzle', '🌧️'], 57: ['Freezing drizzle', '🌧️'],
  61: ['Light rain', '🌦️'], 63: ['Rain', '🌧️'], 65: ['Heavy rain', '🌧️'], 66: ['Freezing rain', '🌧️'], 67: ['Freezing rain', '🌧️'],
  71: ['Light snow', '🌨️'], 73: ['Snow', '🌨️'], 75: ['Heavy snow', '❄️'], 77: ['Snow grains', '🌨️'],
  80: ['Light showers', '🌦️'], 81: ['Showers', '🌧️'], 82: ['Heavy showers', '⛈️'], 85: ['Snow showers', '🌨️'], 86: ['Heavy snow showers', '❄️'],
  95: ['Thunderstorm', '⛈️'], 96: ['Thunderstorm, hail', '⛈️'], 99: ['Severe hail', '⛈️'],
};

function renderWeather(w) {
  if (!w || !w.current) return;
  const c = w.current, d = w.daily || {};
  const [desc, dayIcon, nightIcon] = WX[c.weather_code] || ['—', '🌡️'];
  $('wxIcon').textContent = c.is_day === 0 && nightIcon ? nightIcon : dayIcon;
  $('wxTemp').textContent = Math.round(c.temperature_2m) + '°';
  $('wxDesc').textContent = `${desc} · ${cfg.weather.city}`;
  const hi = d.temperature_2m_max ? Math.round(d.temperature_2m_max[0]) : null;
  const lo = d.temperature_2m_min ? Math.round(d.temperature_2m_min[0]) : null;
  $('wxRange').textContent = (hi != null ? `↑${hi}° ↓${lo}°` : '') + ` · Feels like ${Math.round(c.apparent_temperature)}°`;
}

async function loadWeather() {
  const { lat, lon } = cfg.weather;
  const key = `${lat},${lon}`;
  const cached = store.get('wx', null);
  if (cached && cached.key === key) renderWeather(cached.data);
  if (cached && cached.key === key && Date.now() - cached.at < 14 * 60_000) return;
  try {
    const url = `https://api.open-meteo.com/v1/forecast?latitude=${lat}&longitude=${lon}` +
      '&current=temperature_2m,apparent_temperature,weather_code,is_day' +
      '&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1';
    const ctl = new AbortController();
    setTimeout(() => ctl.abort(), 8000);
    const data = await (await fetch(url, { signal: ctl.signal })).json();
    store.set('wx', { key, at: Date.now(), data });
    renderWeather(data);
  } catch (_) {
    if (!cached) $('wxDesc').textContent = 'Weather unavailable';
  }
}

// ---------------------------------------------------------------- phone battery
function renderPhoneBattery() {
  if (!bridge) return;
  try {
    const b = JSON.parse(bridge.battery());
    $('phoneBat').textContent = `📱 ${b.level}%${b.plugged ? ' ⚡' : ''}`;
  } catch (_) {}
}

// ---------------------------------------------------------------- settings from the agent
async function loadConfig() {
  try {
    const r = await api('/config');
    const before = JSON.stringify(cfg);
    cfg = { ...DEFAULT_CFG, ...r.config };
    cfgVersion = r.version;
    if (JSON.stringify(cfg) !== before) {
      store.set('cfg', cfg);
      applyLayout();
      loadWeather();
    }
  } catch (_) {}
}

// ---------------------------------------------------------------- shortcuts
function renderDeck() {
  const grid = $('deck');
  const list = cfg.shortcuts || [];
  if (!list.length) {
    grid.innerHTML = '<div class="deck-empty">Shortcuts load once the computer is connected.<br>Edit them from the tray icon › Settings.</div>';
    return;
  }
  grid.innerHTML = '';
  const cols = Math.min(6, Math.max(2, opt('deck', 'columns', 4)));
  grid.style.gridTemplateColumns = `repeat(${cols}, 1fr)`;
  grid.style.gridTemplateRows = `repeat(${Math.ceil(list.length / cols)}, 1fr)`;
  for (const s of list) {
    const b = el('button', 'key');
    b.style.setProperty('--k', s.color || '#6b7280');
    b.append(el('span', 'k-icon', s.icon || '•'), el('span', 'k-label', s.label));
    b.addEventListener('click', async () => {
      haptic();
      b.classList.remove('ok', 'err');
      b.classList.add('busy');
      let ok = false;
      try { await api('/run/' + encodeURIComponent(s.id), 'POST', 5000); ok = true; } catch (_) {}
      b.classList.remove('busy');
      b.classList.add(ok ? 'ok' : 'err');
      setTimeout(() => b.classList.remove('ok', 'err'), 900);
    });
    grid.appendChild(b);
  }
}

// ---------------------------------------------------------------- media
document.querySelectorAll('[data-media]').forEach((btn) => btn.addEventListener('click', async () => {
  haptic();
  const action = btn.dataset.media;
  if (action === 'playpause' && media) {
    media.playing = !media.playing; // optimistic update
    media.at = performance.now();
    $('media').classList.toggle('playing', media.playing);
  }
  try { await api('/media/' + action, 'POST'); } catch (_) {}
}));

$('mBar').addEventListener('click', async (e) => {
  if (!media || !media.durMs || !media.canSeek) return;
  const r = e.currentTarget.getBoundingClientRect();
  const ms = Math.round(((e.clientX - r.left) / r.width) * media.durMs);
  haptic();
  media.posMs = ms; media.at = performance.now();
  try { await api('/media/seek?ms=' + ms, 'POST'); } catch (_) {}
});

const vol = $('vol');
let volTimer = 0;
function paintVol(v) { vol.style.setProperty('--p', v + '%'); $('volVal').textContent = v; }
vol.addEventListener('input', () => {
  volDragging = true;
  paintVol(vol.value);
  clearTimeout(volTimer);
  volTimer = setTimeout(() => api('/volume?level=' + vol.value, 'POST').catch(() => {}), 120);
});
vol.addEventListener('change', () => setTimeout(() => (volDragging = false), 600));
$('muteBtn').addEventListener('click', async () => {
  haptic();
  try { renderAudio(await api('/volume?op=mute', 'POST')); } catch (_) {}
});

function renderAudio(a) {
  if (!a) return;
  $('media').classList.toggle('muted-on', !!a.muted);
  if (!volDragging) { vol.value = a.volume; paintVol(a.volume); }
}

function averageColor(img) {
  try {
    const c = document.createElement('canvas');
    c.width = c.height = 16;
    const g = c.getContext('2d');
    g.drawImage(img, 0, 0, 16, 16);
    const d = g.getImageData(0, 0, 16, 16).data;
    let r = 0, gg = 0, b = 0, n = 0;
    for (let i = 0; i < d.length; i += 4) {
      const mx = Math.max(d[i], d[i + 1], d[i + 2]), mn = Math.min(d[i], d[i + 1], d[i + 2]);
      if (mx < 30 || mx - mn < 20) continue; // skip very dark / grey pixels
      r += d[i]; gg += d[i + 1]; b += d[i + 2]; n++;
    }
    if (!n) return null;
    const [R, G, B] = [r / n, gg / n, b / n];
    const k = 220 / (Math.max(R, G, B) || 1); // normalise brightness
    return [R, G, B].map((v) => Math.round(Math.min(255, v * k))).join(', ');
  } catch (_) { return null; }
}

const DEFAULT_AMBIENT = '45, 212, 191';
const artImg = $('art');
artImg.addEventListener('load', () => {
  artImg.classList.add('on');
  document.documentElement.style.setProperty('--ambient', averageColor(artImg) || DEFAULT_AMBIENT);
});
artImg.addEventListener('error', () => artImg.classList.remove('on'));

function renderMedia(m) {
  const box = $('media');
  document.body.classList.toggle('idle-media', !m);
  if (!m) {
    media = null;
    box.classList.remove('playing');
    $('mApp').textContent = 'Media';
    $('mTitle').textContent = 'Nothing playing';
    $('mArtist').textContent = 'Press ▶ to start Spotify';
    $('mFill').style.width = '0';
    $('mPos').textContent = $('mDur').textContent = '0:00';
    if (lastArtId !== 0) { lastArtId = 0; artImg.classList.remove('on'); document.documentElement.style.setProperty('--ambient', DEFAULT_AMBIENT); }
    return;
  }
  media = { ...m, at: performance.now() };
  box.classList.toggle('playing', m.playing);
  $('mApp').textContent = m.app || 'Media';
  $('mTitle').textContent = m.title || 'Unknown track';
  $('mArtist').textContent = [m.artist, m.album].filter(Boolean).join(' · ') || '—';
  $('mDur').textContent = fmtTime(m.durMs);
  if (m.artId !== lastArtId) {
    lastArtId = m.artId;
    if (m.artId) artImg.src = `${API}/art?id=${m.artId}`;
    else { artImg.classList.remove('on'); document.documentElement.style.setProperty('--ambient', DEFAULT_AMBIENT); }
  }
}

setInterval(() => {
  if (!media || !(media.durMs > 0)) return;
  const pos = Math.min(media.durMs, media.posMs + (media.playing ? performance.now() - media.at : 0));
  $('mFill').style.width = (pos / media.durMs) * 100 + '%';
  $('mPos').textContent = fmtTime(pos);
}, 250);

// ---------------------------------------------------------------- system
function renderPc(pc, ping) {
  if (!pc) return;
  const g = pc.gpu;
  const pingTxt = ping ? (ping.ms != null ? `${Math.round(ping.ms)} ms` : 'no ping') : '';
  push('cpu', pc.cpu ?? 0);
  push('gpu', g ? g.util ?? 0 : 0);
  push('ram', pc.ramUsedGb);
  push('down', pc.netDownBps);
  push('up', pc.netUpBps);

  // home tiles
  $('tCpu').textContent = pc.cpu == null ? '—' : Math.round(pc.cpu);
  $('tCpuSub').textContent = pc.cpuTemp != null ? `${Math.round(pc.cpuTemp)}°C` : (pc.cpuName || '').replace(/^(AMD|Intel\(R\))\s*/, '').split(' ').slice(0, 4).join(' ');
  $('tGpu').textContent = g ? Math.round(g.util) : '—';
  $('tGpuSub').textContent = g ? `${Math.round(g.temp)}°C · ${Math.round(g.power)} W` : 'No GPU data';
  $('tRam').textContent = r1(pc.ramUsedGb);
  $('tRamSub').textContent = `/ ${Math.round(pc.ramTotalGb)} GB`;
  const [dv, du] = fmtRate(pc.netDownBps);
  $('tNet').textContent = dv;
  $('tNetU').textContent = du;
  $('tNetSub').textContent = '↑ ' + rateStr(pc.netUpBps) + (pingTxt ? ' · ' + pingTxt : '');

  const C = { cpu: cssVar('--cpu'), gpu: cssVar('--gpu'), ram: cssVar('--ram'), net: cssVar('--net'), up: cssVar('--up') };
  const netMax = Math.max(1, ...hist.down, ...hist.up) * 1.15;
  const type = currentType();

  if (type === 'home') {
    chart($('sCpu'), [{ data: hist.cpu, color: C.cpu }], 100);
    chart($('sGpu'), [{ data: hist.gpu, color: C.gpu }], 100);
    chart($('sRam'), [{ data: hist.ram, color: C.ram }], pc.ramTotalGb);
    chart($('sNet'), [{ data: hist.down, color: C.net }], netMax);
  }

  // system page
  $('cpuName').textContent = pc.cpuName;
  $('cpuBig').textContent = pc.cpu == null ? '—' : Math.round(pc.cpu);
  $('cpuChips').innerHTML = pc.cpuTemp != null
    ? chip(`${Math.round(pc.cpuTemp)}°C`, pc.cpuTemp > 85 ? 'hot' : pc.cpuTemp > 75 ? 'warn' : '')
    : chip('Temp: needs LibreHardwareMonitor');
  if (g) {
    $('gpuName').textContent = g.name;
    $('gpuBig').textContent = Math.round(g.util);
    $('gpuChips').innerHTML =
      chip(`${Math.round(g.temp)}°C`, g.temp > 83 ? 'hot' : g.temp > 75 ? 'warn' : '') +
      chip(`${Math.round(g.power)}/${Math.round(g.powerLimit)} W`) +
      chip(`${Math.round(g.clock)} MHz`) +
      chip(`Fan ${Math.round(g.fan)}%`);
  }
  const s = pc.uptimeSec, d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60);
  $('uptime').textContent = 'Up ' + (d ? `${d} d ${h} h` : h ? `${h} h ${m} min` : `${m} min`);
  $('ramTxt').textContent = `${r1(pc.ramUsedGb)} / ${Math.round(pc.ramTotalGb)} GB`;
  $('ramBar').style.width = (pc.ramUsedGb / pc.ramTotalGb) * 100 + '%';
  if (g && g.vramTotal) {
    $('vramTxt').textContent = `${r1(g.vramUsed)} / ${Math.round(g.vramTotal)} GB`;
    $('vramBar').style.width = (g.vramUsed / g.vramTotal) * 100 + '%';
  }
  // only numbers and drive letters from the local agent end up in this markup
  $('disks').innerHTML = (pc.disks || []).map((dk) =>
    `<div class="meter"><div class="m-row"><span>${dk.name}</span><span>${Math.round((dk.usedGb / dk.totalGb) * 100)}%</span></div>` +
    `<div class="track"><i style="width:${(dk.usedGb / dk.totalGb) * 100}%"></i></div></div>`).join('');
  $('netDown').textContent = rateStr(pc.netDownBps);
  $('netUp').textContent = rateStr(pc.netUpBps);
  $('netPing').textContent = ping
    ? `Ping ${ping.ms != null ? Math.round(ping.ms) + ' ms' : '—'} · avg ${ping.avgMs ?? '—'} ms · loss ${ping.lossPct}%`
    : '';

  if (type === 'system') {
    chart($('cCpu'), [{ data: hist.cpu, color: C.cpu }], 100, { grid: true });
    chart($('cGpu'), [{ data: hist.gpu, color: C.gpu }], 100, { grid: true });
    chart($('cNet'), [{ data: hist.down, color: C.net }, { data: hist.up, color: C.up }], netMax, { grid: true });
  }
}

// ---------------------------------------------------------------- AI quotas
const Q_STYLE = { claude: { color: '#d97757', logo: '✳' }, codex: { color: '#e5e7eb', logo: '◎' } };
let quotas = null;

function fmtReset(ts) {
  if (!ts) return '';
  const d = new Date(ts);
  const hm = d.toLocaleTimeString(LOCALE, { hour: '2-digit', minute: '2-digit' });
  const day = d.toDateString() === new Date().toDateString() ? hm : d.toLocaleDateString(LOCALE, { weekday: 'short' }) + ' ' + hm;
  return `${day} · in ${fmtDur(ts - Date.now())}`;
}

/** Compares the share of the quota used with the share of the window elapsed. */
function paceInfo(l) {
  if (!l.resetsAt || !l.windowSec) return null;
  const win = l.windowSec * 1000;
  const left = l.resetsAt - Date.now();
  const elapsed = Math.min(1, Math.max(0, 1 - left / win));
  if (l.percent >= 100) return { elapsed, cls: 'over', text: 'Limit reached' };
  if (elapsed < 0.04 || l.percent < 1) return { elapsed, cls: 'calm', text: 'Plenty left' };
  const rate = l.percent / (elapsed * win);          // % per ms
  const msTo100 = (100 - l.percent) / rate;
  if (msTo100 < left) return { elapsed, cls: 'over', text: `At this pace: full in ${fmtDur(msTo100)}` };
  const diff = l.percent - elapsed * 100;
  if (diff > 10) return { elapsed, cls: 'fast', text: 'Ahead of pace' };
  if (diff < -10) return { elapsed, cls: 'calm', text: 'Plenty left' };
  return { elapsed, cls: '', text: 'On pace' };
}

function ringSvg(pct, elapsed) {
  const r = 42, c = 2 * Math.PI * r;
  const off = c * (1 - Math.min(100, pct) / 100);
  let tick = '';
  if (elapsed != null) {
    const a = elapsed * 2 * Math.PI;
    const p = (rad) => [50 + Math.cos(a) * rad, 50 + Math.sin(a) * rad].map((v) => v.toFixed(1));
    const [x1, y1] = p(35), [x2, y2] = p(49);
    tick = `<line class="pace" x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}"/>`;
  }
  return `<svg viewBox="0 0 100 100"><circle class="bg" cx="50" cy="50" r="${r}"/>` +
    `<circle class="fg" cx="50" cy="50" r="${r}" stroke-dasharray="${c.toFixed(1)}" stroke-dashoffset="${off.toFixed(1)}"/>${tick}</svg>`;
}

function renderQuotas() {
  const list = (quotas?.providers || []).filter((p) => opt('ai', p.id, true));
  const mini = $('qMini');
  mini.textContent = '';
  for (const p of list) {
    const l = p.limits[0];
    if (!l) continue;
    const s = el('span', 'qm');
    s.append(el('span', null, p.name), el('b', null, Math.round(l.percent) + '%'));
    s.style.color = l.percent >= 95 ? '#f87171' : l.percent >= 80 ? '#fbbf24' : '';
    mini.appendChild(s);
  }
  if (!quotas) return;

  const grid = $('qGrid');
  grid.textContent = '';
  if (!list.length) { grid.appendChild(el('div', 'deck-empty', 'No quota providers enabled')); return; }
  for (const p of list) {
    const st = Q_STYLE[p.id] || { color: '#94a3b8', logo: '●' };
    const card = el('div', 'card q-card');
    card.style.setProperty('--q', st.color);
    card.style.flex = String(Math.max(1, p.limits.length) + 0.3);

    const head = el('div', 'q-head');
    head.append(el('span', 'q-logo', st.logo), el('span', 'q-name', p.name), el('span', 'q-plan', p.plan));
    if (p.updatedAt) head.append(el('span', 'q-upd', fmtDur(Date.now() - p.updatedAt) + ' ago'));
    card.appendChild(head);

    const rings = el('div', 'q-rings');
    for (const l of p.limits) {
      const pace = paceInfo(l);
      const ring = el('div', 'q-ring');
      const dial = el('div', 'q-dial');
      if (l.percent >= 95 || l.severity === 'critical') dial.style.setProperty('--qc', '#ef4444');
      else if (l.percent >= 80 || l.severity === 'warning') dial.style.setProperty('--qc', '#f59e0b');
      dial.innerHTML = ringSvg(l.percent, pace?.elapsed); // numbers only
      const pct = el('div', 'q-pct');
      pct.append(document.createTextNode(Math.round(l.percent)), el('small', null, '%'));
      dial.appendChild(pct);
      ring.append(dial, el('div', 'q-label', l.label), el('div', 'q-reset', fmtReset(l.resetsAt)));
      if (pace) ring.appendChild(el('div', 'q-pace ' + pace.cls, pace.text));
      rings.appendChild(ring);
    }
    if (!p.limits.length) rings.appendChild(el('div', 'q-reset', 'No data'));
    card.appendChild(rings);

    const foot = el('div', 'q-foot');
    foot.append(el('span', null, p.extra || ''), p.error ? el('span', 'q-err', p.error) : el('span'));
    card.appendChild(foot);
    grid.appendChild(card);
  }
}

async function loadQuotas() {
  try { quotas = await api('/quotas'); renderQuotas(); } catch (_) {}
}

// ---------------------------------------------------------------- sessions & GitHub
const S_ORDER = { waiting: 0, busy: 1, shell: 2, idle: 3 };
const prevStatus = new Map();
let workLoaded = false;

/** Vibrates and shows a toast when a session finishes or starts waiting for input. */
function notifyTransitions(sessions) {
  for (const s of sessions) {
    const before = prevStatus.get(s.id);
    if (workLoaded && before && before !== s.status) {
      if (s.status === 'waiting') {
        buzz('0,90,90,90,90,160');
        toast(`${s.name}: ${s.waitingFor || 'needs input'}`, 'err');
      } else if (s.status === 'idle' && (before === 'busy' || before === 'shell')) {
        buzz('0,70,110,70');
        toast(`${s.name} finished`, 'ok');
      }
    }
    prevStatus.set(s.id, s.status);
  }
  workLoaded = true;
}

function renderSessions(sessions) {
  const counts = { waiting: 0, busy: 0, shell: 0, idle: 0 };
  sessions.forEach((s) => (counts[s.status] = (counts[s.status] || 0) + 1));
  $('sSum').textContent = [
    counts.busy + counts.shell ? `${counts.busy + counts.shell} working` : '',
    counts.waiting ? `${counts.waiting} waiting` : '',
    counts.idle ? `${counts.idle} idle` : '',
  ].filter(Boolean).join(' · ') || 'No open sessions';
  $('sMini').textContent = counts.waiting ? `${counts.waiting} waiting for you` : '';

  const list = $('sList');
  list.textContent = '';
  const sorted = [...sessions].sort((a, b) => (S_ORDER[a.status] ?? 9) - (S_ORDER[b.status] ?? 9) || b.since - a.since);
  if (!sorted.length) list.appendChild(el('div', 'deck-empty', 'No Claude Code / Codex sessions open'));
  for (const s of sorted) {
    const row = el('div', 'sess ' + s.status);
    row.append(
      el('span', 'st'),
      el('span', 'tl ' + s.tool, s.tool === 'claude' ? '✳' : '◎'),
      el('span', 'nm', s.name),
      el('span', 'ds', (s.status === 'waiting' ? 'waiting · ' : '') + shortDur(Date.now() - s.since)),
    );
    list.appendChild(row);
  }
}

function prRow(p, mine) {
  const row = el('div', 'gh-pr' + (p.draft ? ' draft' : ''));
  if (mine) {
    const ciMap = { SUCCESS: '✓', FAILURE: '✗', ERROR: '✗', PENDING: '●', EXPECTED: '●' };
    row.appendChild(el('span', 'ci ' + (p.ci || 'none'), ciMap[p.ci] || '–'));
  } else {
    row.appendChild(el('span', 'ci none', '◇'));
  }
  row.append(el('span', 'rp', `${p.repo.split('/')[1]}#${p.number}`), el('span', 'tt', p.title));
  if (mine && (p.review === 'APPROVED' || p.review === 'CHANGES_REQUESTED'))
    row.appendChild(el('span', 'rv ' + p.review, p.review === 'APPROVED' ? 'approved' : 'changes'));
  return row;
}

function renderGitHub(g) {
  if (!g) { $('ghErr').textContent = 'Waiting for GitHub data…'; return; }
  $('ghUser').textContent = g.user ? '@' + g.user : '';
  $('ghBell').textContent = `🔔 ${g.notifications >= 50 ? '50+' : g.notifications}`;
  $('ghRevCount').textContent = g.reviewRequests.length;
  $('ghMineCount').textContent = g.myPrs.length;
  const rev = $('ghRev'), mine = $('ghMine');
  rev.textContent = '';
  mine.textContent = '';
  g.reviewRequests.slice(0, 4).forEach((p) => rev.appendChild(prRow(p, false)));
  if (!g.reviewRequests.length) rev.appendChild(el('div', 'gh-pr', 'No pending reviews'));
  g.myPrs.slice(0, 5).forEach((p) => mine.appendChild(prRow(p, true)));
  if (!g.myPrs.length) mine.appendChild(el('div', 'gh-pr', 'No open pull requests'));
  $('ghErr').textContent = g.error ? 'GitHub: ' + g.error : '';
}

async function loadWork() {
  try {
    const w = await api('/work');
    notifyTransitions(w.sessions || []);
    renderSessions(w.sessions || []);
    renderGitHub(w.github);
  } catch (_) {}
}

// ---------------------------------------------------------------- processes
let procSort = 'cpu';
let procData = null;
const procEls = new Map();

function monogramColor(name) {
  let h = 0;
  for (const ch of name) h = (h * 31 + ch.charCodeAt(0)) % 360;
  return `hsl(${h} 70% 65%)`;
}

/** Fills an icon box with the app icon, falling back to a coloured monogram. */
function fillIcon(box, g) {
  box.replaceChildren();
  box.style.background = monogramColor(g.name);
  box.textContent = (g.title || g.name).charAt(0).toUpperCase();
  if (g.hasIcon !== false) {
    const img = new Image();
    img.onload = () => { box.textContent = ''; box.style.background = 'transparent'; box.appendChild(img); };
    img.src = `${API}/procs/icon?name=${encodeURIComponent(g.name)}`;
  }
}

function procEl(g) {
  let e = procEls.get(g.name);
  if (e) return e;
  e = el('div', 'proc');
  e.innerHTML =
    '<div class="p-ico"></div>' +
    '<div class="p-info"><div class="p-title"><span class="p-t"></span><span class="p-count"></span><span class="p-lock"></span></div><div class="p-bar"><i></i></div></div>' +
    '<div class="p-nums"><b></b><small></small></div>';
  fillIcon(e.querySelector('.p-ico'), g);
  e.addEventListener('click', () => { haptic(); openSheet(e._g); });
  procEls.set(g.name, e);
  return e;
}

function renderProcs() {
  const grid = $('pGrid');
  if (!procData) return;
  if (!procData.groups.length) { $('pSum').textContent = 'Measuring…'; return; }
  const list = [...procData.groups]
    .sort((a, b) => (procSort === 'cpu' ? b.cpu - a.cpu || b.memMb - a.memMb : b.memMb - a.memMb))
    .slice(0, 12);
  const max = Math.max(0.1, ...list.map((g) => (procSort === 'cpu' ? g.cpu : g.memMb)));
  $('pSum').textContent = `${procData.total} processes · ${procData.groupCount ?? procData.groups.length} apps · tap one for details`;

  if (grid.querySelector('.deck-empty')) grid.innerHTML = '';
  const keep = new Set(list.map((g) => g.name));
  for (const [n, e] of procEls) if (!keep.has(n)) { e.remove(); procEls.delete(n); }

  list.forEach((g) => {
    const e = procEl(g);
    e._g = g;
    e.querySelector('.p-t').textContent = g.title;
    e.querySelector('.p-count').textContent = g.count > 1 ? '×' + g.count : '';
    e.querySelector('.p-lock').textContent = g.locked ? '🔒' : '';
    e.querySelector('.p-bar i').style.width = ((procSort === 'cpu' ? g.cpu : g.memMb) / max) * 100 + '%';
    e.querySelector('.p-nums b').textContent = procSort === 'cpu' ? g.cpu.toFixed(1) + '%' : fmtMem(g.memMb);
    e.querySelector('.p-nums small').textContent = procSort === 'cpu' ? fmtMem(g.memMb) : g.cpu.toFixed(1) + '%';
    grid.appendChild(e); // keeps the order
  });
}

async function loadProcs() {
  try { procData = await api('/procs?top=150'); renderProcs(); } catch (_) {}
}

document.querySelectorAll('#pSort button').forEach((b) => b.addEventListener('click', () => {
  haptic();
  procSort = b.dataset.sort;
  document.querySelectorAll('#pSort button').forEach((x) => x.classList.toggle('on', x === b));
  renderProcs();
}));

// ---- instance sheet: end one specific process (with its children) or all of them
let sheet = null;   // { group, detail }

/** Two-tap confirmation: the first tap arms the button, the second one runs the action. */
function armButton(btn, armedText, action) {
  btn.addEventListener('click', (ev) => {
    ev.stopPropagation();
    if (btn.disabled) return;
    if (!btn.classList.contains('armed')) {
      haptic();
      btn.dataset.text = btn.textContent;
      btn.textContent = armedText;
      btn.classList.add('armed');
      clearTimeout(btn._t);
      btn._t = setTimeout(() => { btn.classList.remove('armed'); btn.textContent = btn.dataset.text; }, 3500);
      return;
    }
    clearTimeout(btn._t);
    btn.classList.remove('armed');
    action();
  });
}

function openSheet(g) {
  sheet = { group: g, detail: null };
  fillIcon($('psIco'), g);
  $('psTitle').textContent = g.title;
  $('psSub').textContent = 'Loading…';
  $('psList').replaceChildren();
  $('pSheet').hidden = false;
  sections.procs.classList.add('sheet-open');
  loadSheet();
}

function closeSheet() {
  sheet = null;
  $('pSheet').hidden = true;
  sections.procs.classList.remove('sheet-open');
}

async function loadSheet() {
  if (!sheet) return;
  try {
    const d = await api('/procs/detail?name=' + encodeURIComponent(sheet.group.name));
    if (!sheet || d.name !== sheet.group.name) return;
    sheet.detail = d;
    renderSheet();
  } catch (_) {}
}

function renderSheet() {
  const d = sheet.detail;
  const canKill = opt('procs', 'allowKill', true);
  const totalCpu = d.instances.reduce((a, i) => a + i.cpu, 0), totalMem = d.instances.reduce((a, i) => a + i.memMb, 0);
  $('psSub').textContent = `${d.instances.length} instance${d.instances.length === 1 ? '' : 's'} · CPU ${totalCpu.toFixed(1)}% · ${fmtMem(totalMem)}` +
    (d.locked ? ' · system process' : !canKill ? ' · ending disabled in settings' : '');

  const all = $('psAll');
  all.textContent = d.instances.length > 1 ? `End all ${d.instances.length}` : 'End';
  all.disabled = d.locked || !canKill || !d.instances.length;

  const list = $('psList');
  list.replaceChildren();
  if (!d.instances.length) { list.appendChild(el('div', 'deck-empty', 'No running instances')); return; }
  for (const i of d.instances) {
    const row = el('div', 'pi');
    const lbl = el('div', 'lbl');
    const primary = i.session ? '✳ ' + i.session : i.window || i.args || d.title;
    const secondary = [i.session ? i.args : i.window ? i.args : null, i.startedAt ? (Date.now() - i.startedAt < 60_000 ? 'just started' : 'started ' + shortDur(Date.now() - i.startedAt) + ' ago') : null]
      .filter(Boolean).join(' · ');
    lbl.append(el('b', i.session ? 'sess' : '', primary), el('small', null, secondary));
    const num = el('div', 'num');
    num.append(el('b', null, i.cpu.toFixed(1) + '%'), el('br'), document.createTextNode(fmtMem(i.memMb)));
    const btn = el('button', 'ps-btn danger', 'End');
    btn.disabled = i.locked || !canKill;
    armButton(btn, 'Confirm', () => killInstance(i, primary));
    row.append(el('span', 'pid', 'PID ' + i.pid), lbl, num, btn);
    list.appendChild(row);
  }
}

async function postKill(query) {
  const r = await fetch(`${API}/procs/kill?${query}`, { method: 'POST', cache: 'no-store' });
  return { ok: r.ok, body: await r.json().catch(() => ({})) };
}

async function killInstance(i, label) {
  try {
    const { ok, body } = await postKill('pid=' + i.pid);
    ok ? toast(`Ended ${label} (PID ${i.pid})`, 'ok') : toast(body.error || 'Could not end it', 'err');
  } catch (_) { toast('Computer unreachable', 'err'); }
  setTimeout(() => { loadSheet(); loadProcs(); }, 500);
}

armButton($('psAll'), 'Tap again to confirm', async () => {
  const g = sheet?.group;
  if (!g) return;
  try {
    const { ok, body } = await postKill('name=' + encodeURIComponent(g.name));
    if (ok) { toast(`Ended ${body.killed} × ${g.title}${body.failed ? ` (${body.failed} failed)` : ''}`, 'ok'); closeSheet(); }
    else toast(body.error || 'Could not end them', 'err');
  } catch (_) { toast('Computer unreachable', 'err'); }
  setTimeout(loadProcs, 500);
});
$('psClose').addEventListener('click', () => { haptic(); closeSheet(); });

pager.addEventListener('scrollend', () => {
  if (currentType() === 'procs') loadProcs();
  else if (sheet) closeSheet();
});

// ---------------------------------------------------------------- connection loop
function setOnline(on, host) {
  document.body.classList.toggle('offline', !on);
  $('pcDot').className = 'dot ' + (on ? 'on' : 'off');
  let via = '';
  try { via = bridge ? bridge.transport() : ''; } catch (_) {}
  $('pcName').textContent = on ? (host || 'PC') + (via === 'wifi' ? ' · Wi-Fi' : via === 'usb' ? ' · USB' : '') : 'PC not connected';
  if (on && online !== true) { loadConfig(); loadQuotas(); loadWork(); }
  online = on;
}

async function poll() {
  try {
    const s = await api('/state');
    setOnline(true, s.host);
    if (s.cfgVersion !== undefined && cfgVersion !== null && s.cfgVersion !== cfgVersion) loadConfig();
    renderPc(s.pc, s.ping);
    renderMedia(s.media);
    renderAudio(s.audio);
  } catch (_) {
    setOnline(false);
  }
  setTimeout(poll, 1000);
}

// ---------------------------------------------------------------- start
applyLayout();
tickClock();
loadWeather();
setInterval(loadWeather, 15 * 60_000);
renderPhoneBattery();
setInterval(renderPhoneBattery, 30_000);
setInterval(() => { if (online) loadQuotas(); else renderQuotas(); }, 30_000);
setInterval(() => { if (online) loadWork(); }, 3000);
setInterval(() => { if (online && currentType() === 'procs') { loadProcs(); loadSheet(); } }, 2000);
setInterval(() => { if (online) loadConfig(); }, 60_000);
poll();
