#!/usr/bin/env python3
"""
PhoneBox demo server: runs the phone panel and the settings page in a desktop browser with made-up data,
no phone, agent or Windows needed. Handy for working on the UI and for screenshots.

    python tools/demo-server.py            # then open http://127.0.0.1:8790/  (panel, size the window to 780 x 360)
                                           #           http://127.0.0.1:8790/settings  (settings page)
Only the Python standard library is used. Settings you save are kept in memory until the server stops.
"""
import json, math, random, sys, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse, parse_qs

ROOT = Path(__file__).resolve().parent.parent
WWW = ROOT / 'android' / 'app' / 'src' / 'main' / 'assets' / 'www'
AGENT = ROOT / 'agent' / 'PhoneBox.Agent'
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8790
START = time.time()
now_ms = lambda: int(time.time() * 1000)

# A fake Android bridge, so phone-only widgets (battery, Wi-Fi/USB label) have something to show.
FAKE_BRIDGE = """<script>window.Android = {
  battery: () => JSON.stringify({ level: 82, plugged: true, temp: 29.5 }), transport: () => 'usb', version: () => 'demo',
  haptic() {}, buzz() {}, setBrightness() {}, setVolumeKeys() {} };</script>"""

SHORTCUTS = [
    ('terminal', 'Terminal', '⌨️', '#4b5563'), ('code', 'VS Code', '📝', '#2f80ed'), ('browser', 'Browser', '🌐', '#0ea5e9'),
    ('files', 'Files', '📁', '#e5b84b'), ('music', 'Spotify', '🎧', '#1db954'), ('chat', 'Discord', '💬', '#6366f1'),
    ('snip', 'Snip', '✂️', '#14b8a6'), ('taskmgr', 'Task Manager', '📊', '#22c55e'), ('mute', 'Mute', '🔇', '#f59e0b'),
    ('screenoff', 'Screen Off', '🌙', '#8b5cf6'), ('lock', 'Lock', '🔒', '#ef4444'), ('settings', 'Settings', '⚙️', '#94a3b8'),
]
PAGES = ['home', 'dashboard', 'ai', 'work', 'system', 'procs', 'deck', 'music', 'status', 'ambient', 'world', 'daily', 'markets', 'feeds', 'focus', 'calm', 'clock', 'devops']


def build_config():
    templates = {t['id']: t for t in json.loads((AGENT / 'templates.json').read_text(encoding='utf-8'))}
    pages = []
    for pid in PAGES:
        t = templates[pid]
        seen = {}
        widgets = []
        for w in t['widgets']:
            n = seen[w['type']] = seen.get(w['type'], 0) + 1
            widgets.append({**w, 'id': w['type'] if n == 1 else f"{w['type']}-{n}", 'options': dict(w.get('options') or {})})
        pages.append({'id': pid, 'title': t['title'], 'icon': t['icon'], 'enabled': True, 'widgets': widgets})
    return {
        'deviceSerial': None, 'adbPath': None,
        'weather': {'city': 'Tallinn', 'lat': 59.437, 'lon': 24.745},
        'night': {'enabled': False, 'start': '00:30', 'end': '07:30', 'brightness': 0.02},
        'display': {'showSeconds': True, 'idleReturnSec': 0, 'vibrate': True, 'volumeKeys': True},
        'lan': {'enabled': True, 'port': 8766, 'token': 'demo-phone-key', 'webhookToken': 'demo-webhook-key'},
        'updates': {'auto': True, 'repo': 'ersinkoc/phonebox'},
        'pages': pages,
        'shortcuts': [{'id': i, 'label': l, 'icon': ic, 'color': c, 'cmd': 'demo.exe'} for i, l, ic, c in SHORTCUTS],
    }


CONFIG = build_config()
CFG_VERSION = [1]


def public_config():
    c = json.loads(json.dumps(CONFIG))
    c['shortcuts'] = [{k: s[k] for k in ('id', 'label', 'icon', 'color')} for s in c['shortcuts']]
    for p in c['pages']:
        for w in p['widgets']:
            if w['type'] == 'command': w['options'].pop('command', None)
    for k in ('lan', 'updates', 'deviceSerial', 'adbPath'): c.pop(k, None)
    return c


def wave(period, lo, hi, phase=0.0, noise=0.0):
    t = time.time() - START
    return lo + (hi - lo) * (0.5 + 0.5 * math.sin(2 * math.pi * t / period + phase)) + random.uniform(-noise, noise)


EVENTS = []          # webhook events for the panel: (seq, kind, data, at)
PUSH_VERSION = [1]


def emit(kind, data=None):
    seq = (EVENTS[-1]['seq'] if EVENTS else 0) + 1
    EVENTS.append({'seq': seq, 'kind': kind, 'data': data, 'at': now_ms()})
    del EVENTS[:-50]
    return EVENTS[-1]


def state(since=None):
    t = time.time() - START
    dur = 214_000
    return {
        'update': None, 'ts': now_ms(), 'host': 'DESK-PC', 'cfgVersion': CFG_VERSION[0], 'pushVersion': PUSH_VERSION[0],
        'evSeq': EVENTS[-1]['seq'] if EVENTS else 0, 'events': [e for e in EVENTS if since is not None and e['seq'] > since],
        'pc': {
            'cpu': max(1, wave(37, 8, 46, noise=6)), 'cpuName': 'AMD Ryzen 9 7950X 16-Core Processor', 'cpuTemp': wave(60, 52, 68),
            'ramUsedGb': wave(120, 21, 24), 'ramTotalGb': 64, 'netDownBps': max(0, wave(23, 0.2e6, 9e6, noise=1e6)), 'netUpBps': max(0, wave(17, 40e3, 600e3, 1, noise=50e3)),
            'uptimeSec': 3 * 86400 + 5 * 3600 + int(t),
            'disks': [{'name': 'C:', 'usedGb': 612, 'totalGb': 1000}, {'name': 'D:', 'usedGb': 1450, 'totalGb': 2000}, {'name': 'E:', 'usedGb': 310, 'totalGb': 4000}],
            'gpu': {'name': 'NVIDIA GeForce RTX 4080', 'util': max(0, wave(29, 4, 38, 2, noise=4)), 'temp': wave(50, 46, 61), 'power': wave(29, 38, 140, 2),
                    'powerLimit': 320, 'clock': 2520, 'fan': 34, 'vramUsed': 5.8, 'vramTotal': 16},
        },
        'media': {'app': 'Spotify', 'title': 'Midnight Circuit', 'artist': 'Neon Harbor', 'album': 'Low Tide Lights', 'durMs': dur,
                  'posMs': int((t * 1000 + 61_000) % dur), 'playing': True, 'canSeek': True, 'artId': 1},
        'audio': {'volume': 42, 'muted': False},
        'ping': {'ms': wave(13, 9, 16), 'avgMs': 12, 'lossPct': 0},
        # the microphone is "in use" for 20 s of every minute, to show the On air widget
        'privacy': {'mic': t % 60 < 20, 'camera': False, 'micApps': ['Zoom'] if t % 60 < 20 else [], 'cameraApps': []},
    }


ART = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 300 300"><defs>
<linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#ff5f6d"/><stop offset=".55" stop-color="#7b2ff7"/><stop offset="1" stop-color="#12c2e9"/></linearGradient>
<radialGradient id="s" cx=".5" cy=".42" r=".5"><stop offset="0" stop-color="#ffd86f"/><stop offset="1" stop-color="#ff6f61"/></radialGradient></defs>
<rect width="300" height="300" fill="url(#g)"/><circle cx="150" cy="130" r="70" fill="url(#s)"/>
<g fill="#1b1037" opacity=".85"><rect x="0" y="170" width="300" height="130"/></g>
<g stroke="#ff6fd8" stroke-width="3" opacity=".9">""" + ''.join(f'<line x1="0" y1="{180 + i * 14}" x2="300" y2="{180 + i * 14}"/>' for i in range(9)) + \
    ''.join(f'<line x1="150" y1="170" x2="{-300 + i * 75}" y2="300"/>' for i in range(13)) + '</g></svg>'


HA = {
    'light.desk': {'name': 'Desk lamp', 'state': 'on'}, 'light.ceiling': {'name': 'Ceiling', 'state': 'off'},
    'switch.coffee': {'name': 'Coffee machine', 'state': 'off'},
    'sensor.office_temperature': {'name': 'Office', 'state': '21.4', 'unit': '°C', 'deviceClass': 'temperature'},
    'sensor.office_humidity': {'name': 'Humidity', 'state': '43', 'unit': '%', 'deviceClass': 'humidity'},
    'binary_sensor.front_door': {'name': 'Front door', 'state': 'off', 'deviceClass': 'door'},
    'lock.front_door': {'name': 'Door lock', 'state': 'locked'}, 'scene.movie': {'name': 'Movie night', 'state': 'scening'},
}

CONTAINERS = [
    {'id': 'a1b2c3d4e5f6', 'name': 'shop-web-1', 'image': 'nginx:1.27', 'project': 'shop', 'state': 'running', 'status': 'Up 3 hours', 'health': None, 'cpu': 0.4, 'mem': '12MiB'},
    {'id': 'b2c3d4e5f6a1', 'name': 'shop-api-1', 'image': 'node:22-alpine', 'project': 'shop', 'state': 'running', 'status': 'Up 3 hours (healthy)', 'health': 'healthy', 'cpu': 6.2, 'mem': '148MiB'},
    {'id': 'c3d4e5f6a1b2', 'name': 'shop-db-1', 'image': 'postgres:17', 'project': 'shop', 'state': 'running', 'status': 'Up 3 hours', 'health': None, 'cpu': 1.1, 'mem': '86MiB'},
    {'id': 'd4e5f6a1b2c3', 'name': 'shop-worker-1', 'image': 'node:22-alpine', 'project': 'shop', 'state': 'running', 'status': 'Up 12 minutes (unhealthy)', 'health': 'unhealthy', 'cpu': 31.5, 'mem': '402MiB'},
    {'id': 'e5f6a1b2c3d4', 'name': 'redis', 'image': 'redis:7', 'project': None, 'state': 'exited', 'status': 'Exited (0) 2 days ago', 'health': None, 'cpu': None, 'mem': None},
]


def calendar():
    # made-up calendar around the current time: one meeting is on now
    import datetime
    t = time.time() * 1000
    day = datetime.datetime.combine(datetime.date.today(), datetime.time()).timestamp() * 1000
    H, M = 3600e3, 60e3
    def ev(title, start, mins, cal, color, location=None, all_day=False):
        return {'title': title, 'start': int(start), 'end': int(start + mins * M), 'allDay': all_day, 'location': location, 'calendar': cal, 'color': color}
    work, home = '#60a5fa', '#34d399'
    events = [
        ev('Design review', t - 20 * M, 45, 'Work', work, 'Room 4'),
        ev('1:1 with Sam', t + 90 * M, 30, 'Work', work),
        ev('Gym', day + 18.5 * H, 60, 'Personal', home),
        ev('Release day', day + 24 * H, 24 * 60, 'Work', work, None, True),
        ev('Sprint planning', day + 33.5 * H, 60, 'Work', work, 'Big room'),
        ev('Lunch with Alex', day + 36 * H, 60, 'Personal', home, 'Noodle bar'),
        ev('Dentist', day + 58 * H, 45, 'Personal', home),
    ]
    return {'updated': int(t - 4 * M), 'errors': [], 'events': sorted(events, key=lambda e: e['start'])}


def quotas():
    n = now_ms()
    return {'providers': [
        {'id': 'claude', 'name': 'Claude', 'plan': 'Max 5x', 'updatedAt': n - 120_000, 'extra': '', 'error': None, 'limits': [
            {'label': '5-hour session', 'percent': 34, 'resetsAt': n + int(2.6 * 3600e3), 'windowSec': 18000},
            {'label': 'Weekly', 'percent': 52, 'resetsAt': n + int(3.2 * 86400e3), 'windowSec': 604800},
            {'label': 'Weekly · Fable', 'percent': 9, 'resetsAt': n + int(3.2 * 86400e3), 'windowSec': 604800}]},
        {'id': 'codex', 'name': 'Codex', 'plan': 'Pro', 'updatedAt': n - 300_000, 'extra': 'Credits: 1,250', 'error': None, 'limits': [
            {'label': '5-hour session', 'percent': 12, 'resetsAt': n + int(4.1 * 3600e3), 'windowSec': 18000},
            {'label': 'Weekly', 'percent': 27, 'resetsAt': n + int(5.5 * 86400e3), 'windowSec': 604800}]},
    ]}


def work():
    n = now_ms()
    s = lambda i, tool, name, st, mins, wf=None: {'id': f'{tool}:{i}', 'tool': tool, 'name': name, 'project': name, 'status': st,
                                                   'waitingFor': wf, 'since': n - mins * 60_000, 'startedAt': n - 3 * 3600e3}
    pr = lambda repo, num, title, ci=None, review=None, draft=False: {'repo': repo, 'number': num, 'title': title, 'ci': ci, 'review': review, 'draft': draft}
    return {
        'sessions': [s(1, 'claude', 'api-gateway', 'busy', 4), s(2, 'claude', 'docs-site', 'waiting', 2, 'approve a file edit'),
                     s(3, 'codex', 'mobile-app', 'busy', 11), s(4, 'claude', 'infra', 'shell', 1), s(5, 'claude', 'blog-engine', 'idle', 38),
                     s(6, 'codex', 'data-pipeline', 'idle', 74), s(7, 'claude', 'design-system', 'idle', 125)],
        'github': {'user': 'octocat', 'notifications': 7, 'error': None,
                   'reviewRequests': [pr('acme/api', 412, 'Rate limiter for public endpoints'), pr('acme/web', 1288, 'Dark mode for the dashboard'),
                                      pr('acme/infra', 77, 'Move staging to a new region')],
                   'myPrs': [pr('acme/api', 409, 'Cache user sessions in Redis', 'SUCCESS', 'APPROVED'), pr('acme/web', 1281, 'Faster image uploads', 'PENDING'),
                             pr('acme/cli', 53, 'New "deploy" command', 'FAILURE', 'CHANGES_REQUESTED'), pr('acme/docs', 210, 'Getting started rewrite', None, None, True)]},
    }


PROCS = [('code', 'Visual Studio Code', 9, 6.2, 2100), ('chrome', 'Google Chrome', 14, 4.8, 3400), ('node', 'Node.js', 6, 3.9, 1200),
         ('claude', 'Claude', 7, 2.7, 1800), ('docker', 'Docker Desktop', 4, 2.1, 2600), ('spotify', 'Spotify', 5, 1.4, 420),
         ('discord', 'Discord', 6, 1.1, 610), ('explorer', 'Windows Explorer', 3, 0.9, 380), ('obs64', 'OBS Studio', 1, 0.8, 520),
         ('slack', 'Slack', 5, 0.6, 700), ('steam', 'Steam', 3, 0.4, 340), ('onedrive', 'Microsoft OneDrive', 1, 0.3, 120),
         ('python', 'Python', 2, 0.3, 160), ('powertoys', 'PowerToys', 4, 0.2, 180), ('figma', 'Figma', 3, 0.2, 640), ('postgres', 'PostgreSQL', 6, 0.1, 290)]


def procs():
    groups = [{'name': n, 'title': t, 'count': c, 'cpu': max(0.0, round(cpu * random.uniform(.8, 1.2), 1)), 'memMb': mem, 'locked': n == 'explorer', 'hasIcon': False}
              for n, t, c, cpu, mem in PROCS]
    return {'total': 312, 'groupCount': 141, 'groups': groups}


def proc_detail(name):
    p = next((x for x in PROCS if x[0] == name), PROCS[0])
    n = now_ms()
    inst = [{'pid': 4100 + i * 37, 'cpu': round(p[3] / p[2], 1), 'memMb': p[4] // p[2], 'startedAt': n - (i + 1) * 1800e3, 'locked': p[0] == 'explorer',
             'session': ['api-gateway', 'docs-site', 'infra'][i] if p[0] == 'claude' and i < 3 else None, 'window': None,
             'args': '--type=renderer' if i else None} for i in range(p[2])]
    return {'name': p[0], 'title': p[1], 'locked': p[0] == 'explorer', 'instances': inst}


PUSH = {
    'temperature': {'data': 21.5, 'at': now_ms() - 180_000, 'expiresAt': None},
    'build': {'data': {'value': 'passed', 'sub': 'main · 3m 12s'}, 'at': now_ms() - 240_000, 'expiresAt': None},
    'deploy': {'data': {'value': 'running', 'sub': 'production'}, 'at': now_ms() - 60_000, 'expiresAt': None},
    'progress': {'data': {'value': 64}, 'at': now_ms() - 30_000, 'expiresAt': None},
    'todo': {'data': {'items': [{'label': 'Release 0.6.2', 'value': 'today', 'level': 'warn'}, {'label': 'Widget editor', 'value': 'done', 'level': 'ok'},
                                {'label': 'Webhooks', 'value': 'done', 'level': 'ok'}, 'Water the plants']}, 'at': now_ms() - 600_000, 'expiresAt': None},
}

CATALOG = None


def catalog():
    global CATALOG
    if CATALOG is None:
        CATALOG = {'cols': 12, 'rows': 6, 'widgets': json.loads((AGENT / 'widgets.json').read_text(encoding='utf-8')),
                   'templates': json.loads((AGENT / 'templates.json').read_text(encoding='utf-8'))}
    return CATALOG


def api(method, path, query, body):
    """Returns (status, content type, bytes)."""
    js = lambda o, code=200: (code, 'application/json; charset=utf-8', json.dumps(o, ensure_ascii=False).encode())
    if path == '/api/state': return js(state(int(query['ev'][0]) if 'ev' in query else None))
    # webhook API, as on the real agent: POST /api/push/<key>, /api/notify, /api/goto/<page>, /api/refresh
    if path.startswith('/api/push/') and method in ('POST', 'PUT'):
        raw = (body or b'').decode('utf-8').strip()
        try: data = json.loads(raw)
        except ValueError: data = raw
        PUSH[path[len('/api/push/'):]] = {'data': data, 'at': now_ms(), 'expiresAt': None}
        PUSH_VERSION[0] += 1
        return js({'ok': True})
    if path == '/api/notify' and method == 'POST':
        raw = (body or b'').decode('utf-8').strip()
        try: data = json.loads(raw)
        except ValueError: data = {'text': raw}
        return js(emit('notify', data))
    if path.startswith('/api/goto/') and method == 'POST': return js(emit('goto', {'page': path[len('/api/goto/'):]}))
    if path == '/api/refresh' and method == 'POST': return js(emit('reload' if query.get('reload') else 'refresh'))
    if path == '/api/config': return js({'version': CFG_VERSION[0], 'config': public_config()})
    if path == '/api/art': return 200, 'image/svg+xml', ART.encode()
    if path == '/api/quotas': return js(quotas())
    if path == '/api/calendar': return js(calendar())
    if path == '/api/ha/states':
        ids = [i for i in query.get('ids', [''])[0].split(',') if i] or list(HA)
        ents = [{'id': i, 'unit': None, 'deviceClass': None, 'icon': None, 'changed': int(time.time() * 1000) - 600000, **HA[i]} for i in ids if i in HA]
        return js({'configured': True, 'error': None, 'entities': ents})
    if path.startswith('/api/ha/toggle/') and method == 'POST':
        e = HA.get(path[len('/api/ha/toggle/'):])
        if e and e['state'] in ('on', 'off'): e['state'] = 'off' if e['state'] == 'on' else 'on'
        elif e and e['state'] in ('locked', 'unlocked'): e['state'] = 'unlocked' if e['state'] == 'locked' else 'locked'
        return js({'ok': True})
    if path == '/api/batteries':
        return js({'devices': [
            {'name': 'WH-1000XM5', 'level': 64, 'kind': 'headphones', 'charging': None},
            {'name': 'MX Master 3S', 'level': 85, 'kind': 'mouse', 'charging': None},
            {'name': 'MX Keys', 'level': 18, 'kind': 'keyboard', 'charging': None},
            {'name': 'Xbox Wireless Controller', 'level': 40, 'kind': 'controller', 'charging': None},
        ]})
    if path == '/api/docker':
        return js({'control': True, 'snapshot': {'available': True, 'error': None, 'containers': CONTAINERS, 'updated': int(time.time() * 1000)}})
    if path.startswith('/api/docker/') and method == 'POST':
        _, _, _, cid, action = path.split('/')
        for c in CONTAINERS:
            if c['id'] == cid and action == 'stop':
                c.update(state='exited', status='Exited (0) 1 second ago', cpu=None, mem=None, health=None)
            elif c['id'] == cid and action in ('start', 'restart'):
                c.update(state='running', status='Up 1 second', cpu=2.0, mem='40MiB', health=None)
        return js({'ok': True})
    if path == '/api/work': return js(work())
    if path == '/api/procs': return js(procs())
    if path == '/api/procs/detail': return js(proc_detail(query.get('name', [''])[0]))
    if path == '/api/procs/icon': return 404, 'text/plain', b''
    if path.startswith('/api/widget/'): return js({'output': str(random.choice([3, 4, 5])), 'at': now_ms() - 8000, 'ok': True, 'running': False, 'durationMs': 412})
    if path == '/api/push' and method == 'GET': return js({'version': 1, 'items': PUSH})
    if path == '/api/catalog': return js(catalog())
    if path == '/api/settings':
        if method == 'PUT':
            global CONFIG
            CONFIG = json.loads(body or b'{}')
            CFG_VERSION[0] += 1
            return js({'version': CFG_VERSION[0]})
        return js(CONFIG)
    if path == '/api/info':
        return js({'version': 'demo', 'configPath': r'%APPDATA%\PhoneBox\config.json', 'logPath': r'%LOCALAPPDATA%\PhoneBox\agent.log',
                   'adb': r'C:\platform-tools\adb.exe', 'connected': True, 'devices': ['DEMO0001']})
    if path == '/api/lan':
        return js({'configured': True, 'active': True, 'port': 8766, 'restartNeeded': False, 'hosts': ['192.168.1.20', '100.101.102.103'], 'fingerprint': 'demo'})
    if path == '/api/update':
        return js({'status': {'agentVersion': 'demo', 'appVersion': 'demo', 'latest': 'demo', 'agentUpdate': False, 'appUpdate': False,
                              'lastCheck': now_ms() - 3600e3, 'state': 'idle', 'error': None}, 'auto': True, 'installed': True})
    if method in ('POST', 'PUT', 'DELETE'): return js({'ok': True})
    return 404, 'text/plain', b'not found'


class Handler(BaseHTTPRequestHandler):
    def send(self, code, ctype, data):
        self.send_response(code)
        self.send_header('Content-Type', ctype)
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Cache-Control', 'no-store')
        self.end_headers()
        self.wfile.write(data)

    def handle_any(self):
        u = urlparse(self.path)
        path, query = u.path, parse_qs(u.query)
        body = self.rfile.read(int(self.headers.get('Content-Length') or 0)) or None
        if path.startswith('/agent/api/'): return self.send(*api(self.command, path[len('/agent'):], query, body))
        if path.startswith('/api/'): return self.send(*api(self.command, path, query, body))
        if path in ('/settings', '/settings/'): return self.send(200, 'text/html; charset=utf-8', (AGENT / 'settings.html').read_bytes())
        if path == '/favicon.ico': return self.send(200, 'image/x-icon', (AGENT / 'phonebox.ico').read_bytes())
        if path in ('/', '/index.html'):
            html = (WWW / 'index.html').read_text(encoding='utf-8').replace('<script src="app.js">', FAKE_BRIDGE + '\n<script src="app.js">')
            return self.send(200, 'text/html; charset=utf-8', html.encode())
        f = (WWW / path.lstrip('/')).resolve()
        if WWW in f.parents and f.is_file():
            ctype = {'.js': 'text/javascript', '.css': 'text/css', '.png': 'image/png', '.svg': 'image/svg+xml'}.get(f.suffix, 'application/octet-stream')
            return self.send(200, ctype + '; charset=utf-8' if ctype.startswith('text') else ctype, f.read_bytes())
        self.send(404, 'text/plain', b'not found')

    do_GET = do_POST = do_PUT = do_DELETE = handle_any

    def log_message(self, *a):
        pass


if __name__ == '__main__':
    print(f'PhoneBox demo: panel http://127.0.0.1:{PORT}/   settings http://127.0.0.1:{PORT}/settings')
    ThreadingHTTPServer(('127.0.0.1', PORT), Handler).serve_forever()
