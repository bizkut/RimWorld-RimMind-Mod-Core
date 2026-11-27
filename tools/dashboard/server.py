#!/usr/bin/env python3
"""Local, dependency-free RimMind event dashboard."""
import argparse
import datetime as dt
import json
import mimetypes
import re
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import unquote, urlsplit

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
DAYFILE = re.compile(r'day-(\d+)-state\.json$')
SHOT = re.compile(r'day-\d+\.png$')


def load(path):
    try:
        with path.open(encoding='utf-8-sig') as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def report(folder):
    candidates = sorted(folder.glob('playthrough-*days-report.json'), key=lambda p: p.stat().st_mtime, reverse=True)
    return next((data for p in candidates if (data := load(p))), {})


def states(folder):
    return {int(m.group(1)): load(p) for p in folder.glob('day-*-state.json') if (m := DAYFILE.fullmatch(p.name))}


def event_metadata(path):
    result = {'model': None, 'status': 'streaming', 'days': 0}
    try:
        with path.open(encoding='utf-8-sig') as f:
            for line in f:
                try:
                    event = json.loads(line)
                except ValueError:
                    continue
                if event.get('type') == 'session_start':
                    result['model'] = event.get('model')
                elif event.get('type') == 'session_end':
                    result['status'] = event.get('reason') or 'ended'
                if isinstance(event.get('day'), (int, float)):
                    result['days'] = max(result['days'], event['day'])
    except OSError:
        pass
    return result


def stamp(path):
    return dt.datetime.fromtimestamp(path.stat().st_mtime, dt.timezone.utc).isoformat()


def envelope(kind, tick, ts):
    return {'v': 1, 'type': kind, 'tick': tick, 'day': round(tick / 60000, 2), 'ts': ts}


def legacy(folder, data):
    ts = stamp(folder)
    yield dict(envelope('session_start', 0, ts), map_id=None, map_size=None, biome=None, colony=None, model=data.get('Model'), run_id=folder.name, speed=None)
    by_day = {d.get('DayNumber', d.get('Day')): d for d in data.get('Days', [])}
    day_states = states(folder)
    events = []
    for day in sorted(day for day in set(by_day) | set(day_states) if isinstance(day, int)):
        d, s = by_day.get(day, {}), day_states.get(day, {})
        people = s.get('colonists', d.get('Colonists', []))
        colony = d.get('Colony') or {}
        stats = s.get('mapStats') or {}
        alive = colony.get('ColonistsAlive', len(people))
        moods = [p['Mood'] for p in people if isinstance(p.get('Mood'), (int, float))]
        nutrition = stats.get('totalAvailableNutrition')
        c = {'alive': alive, 'downed': colony.get('ColonistsDowned', sum(bool(p.get('IsDowned')) for p in people)),
             'mood': colony.get('AverageMood', sum(moods) / len(moods) if moods else None),
             'food_days': colony.get('FoodDays', nutrition / (alive * 1.6) if nutrition is not None and alive else None),
             'wealth': colony.get('Wealth'), 'research_done': colony.get('ResearchCompleted'),
             'active_bills': colony.get('ActiveBills'), 'pending_construction': colony.get('PendingConstruction', stats.get('pendingConstructionCount')),
             'buildings': stats.get('colonistBuildingsCount'), 'hostiles': colony.get('Hostiles'), 'fires': colony.get('Fires'),
             'outdoor_temp': stats.get('outdoorTemp'), 'speed': None, 'paused': None}
        pawns = [{'id': None, 'name': p.get('Name'), 'x': None, 'z': None, 'mood': p.get('Mood'), 'food': p.get('Food'),
                  'rest': p.get('Rest'), 'health': p.get('HealthSummary'), 'downed': p.get('IsDowned'),
                  'mental': p.get('MentalState') or None, 'job': p.get('CurrentJob'),
                  'agent': (p.get('AgentMode') or 'Active') if p.get('IsAgentActive') else 'Inactive', 'drafted': None} for p in people]
        events.append(dict(envelope('snapshot', day * 60000, ts), colony=c, colonists=pawns))
        for a in d.get('AgentActions', s.get('agentActions', [])):
            tick = a.get('Tick') or day * 60000
            events.append(dict(envelope('action', tick, ts), source='agent', npc=None, pawn=a.get('PawnName'), tool=a.get('Action'), args='', ok=a.get('Success'), result=a.get('ResultReason') or '', reason=a.get('Reason')))
    yield from sorted(events, key=lambda e: e['tick'])


class Dashboard(ThreadingHTTPServer):
    daemon_threads = True
    def __init__(self, address, runs, live):
        self.runs, self.live = runs.resolve(), live.expanduser().resolve()
        super().__init__(address, Handler)

    def live_file(self):
        try:
            p = Path((self.live / 'current.txt').read_text(encoding='utf-8-sig').strip()).expanduser()
            return p.resolve() if p.is_file() else None
        except OSError:
            return None

    def list_runs(self):
        items = []
        if self.runs.is_dir():
            for folder in self.runs.iterdir():
                if not folder.is_dir() or folder.is_symlink():
                    continue
                data = report(folder)
                ev = folder / 'events.ndjson'
                meta = event_metadata(ev) if ev.is_file() else {}
                info = {'id': folder.name, 'kind': 'events' if ev.is_file() else 'legacy', 'is_control': folder.name.endswith('-control'),
                        'model': meta.get('model') or data.get('Model'), 'status': meta.get('status') or data.get('OverallStatus'),
                        'days': meta.get('days', data.get('TotalDaysCompleted', len(data.get('Days', [])))),
                        'mtime': max([folder.stat().st_mtime] + [p.stat().st_mtime for p in folder.glob('day-*-state.json')] + ([ev.stat().st_mtime] if ev.is_file() else [])),
                        'screenshots': sorted(p.name for p in folder.glob('day-*.png') if SHOT.fullmatch(p.name))}
                items.append(info)
        live = self.live_file()
        if live:
            items.append(dict(event_metadata(live), id='__live__', kind='events', is_control=False, mtime=live.stat().st_mtime, screenshots=[]))
        return sorted(items, key=lambda x: x['mtime'], reverse=True)

    def folder(self, ident):
        if not ident or ident in ('.', '..') or '/' in ident or '\\' in ident:
            return None
        folder = self.runs / ident
        return folder if folder.is_dir() and not folder.is_symlink() else None


class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    def send_bytes(self, data, content_type, status=200):
        self.send_response(status)
        self.send_header('Content-Type', content_type)
        self.send_header('Content-Length', str(len(data)))
        self.send_header('X-Content-Type-Options', 'nosniff')
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        path = unquote(urlsplit(self.path).path)
        if path == '/api/runs':
            self.send_bytes(json.dumps(self.server.list_runs()).encode(), 'application/json; charset=utf-8')
            return
        match = re.fullmatch(r'/api/runs/([^/]+)/(events|screenshots/([^/]+))', path)
        if match:
            ident, operation, shot = match.groups()
            folder = self.server.folder(ident) if ident != '__live__' else None
            if operation == 'events':
                event_file = self.server.live_file() if ident == '__live__' else (folder / 'events.ndjson' if folder else None)
                if not folder and not event_file:
                    self.send_error(404); return
                self.send_response(200)
                self.send_header('Content-Type', 'text/event-stream; charset=utf-8')
                self.send_header('Cache-Control', 'no-cache')
                self.send_header('Connection', 'close')
                self.end_headers()
                self.close_connection = True
                try:
                    if event_file and event_file.is_file():
                        self.tail(event_file)
                    elif folder:
                        for event in legacy(folder, report(folder)):
                            self.wfile.write(('data: ' + json.dumps(event, ensure_ascii=False) + '\n\n').encode())
                        self.wfile.flush()
                except (BrokenPipeError, ConnectionResetError, OSError):
                    pass
                return
            if folder and SHOT.fullmatch(shot or ''):
                file = folder / shot
                if file.is_file() and not file.is_symlink():
                    self.send_bytes(file.read_bytes(), 'image/png'); return
            self.send_error(404); return
        name = 'index.html' if path == '/' else path.lstrip('/')
        if name in ('index.html', 'app.js', 'style.css') and (HERE / name).is_file():
            self.send_bytes((HERE / name).read_bytes(), (mimetypes.guess_type(name)[0] or 'application/octet-stream') + '; charset=utf-8')
        else:
            self.send_error(404)

    def tail(self, path):
        with path.open('rb') as f:
            pending = b''
            while True:
                chunk = f.read()
                if chunk:
                    lines = (pending + chunk).split(b'\n')
                    pending = lines.pop()
                    for line in lines:
                        try:
                            event = json.loads(line.decode('utf-8-sig'))
                        except (UnicodeDecodeError, ValueError):
                            continue
                        self.wfile.write(('data: ' + json.dumps(event, ensure_ascii=False) + '\n\n').encode())
                    self.wfile.flush()
                else:
                    self.wfile.write(b': keepalive\n\n')
                    self.wfile.flush()
                    time.sleep(1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=8790)
    parser.add_argument('--runs', type=Path, default=REPO / '.playthrough' / 'runs')
    parser.add_argument('--live', type=Path, default=Path('~/Library/Application Support/RimWorld/RimMind/Live'))
    args = parser.parse_args()
    server = Dashboard(('127.0.0.1', args.port), args.runs, args.live)
    print(f'Dashboard: http://127.0.0.1:{args.port}/', flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == '__main__':
    main()
