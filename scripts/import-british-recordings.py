"""Supplement an existing Lexi database with licensed British human recordings.

Requires: python -m pip install requests brotli
Default is a preview; --apply backs up the database before inserting audio-only
entries. Commons responses and per-file attribution are kept in --cache.
Audio itself follows Lexi's existing download-on-first-play policy.
"""
import argparse
import hashlib
import json
import re
import sqlite3
import struct
import time
from collections import defaultdict
from contextlib import closing
from datetime import datetime, timezone
from pathlib import Path

import brotli
import requests

API = 'https://commons.wikimedia.org/w/api.php'
# Accent evidence: Back ache's Commons user page explicitly says native British
# English, South London. Vealhurl's English Wiktionary recordings are marked SSB
# (Standard Southern British) in the Kaikki source already used by Lexi.
SPEAKERS = ('Vealhurl', 'Back ache')
LICENSES = {'CC0', 'CC BY-SA 4.0', 'CC BY-SA 3.0', 'CC BY 4.0', 'CC BY 3.0'}
SOURCE_PREFIX = 'commons-uk-'


def decode(blob):
    if len(blob) < 5 or blob[0] not in (0, 1):
        raise ValueError('Unsupported Lexi entry codec')
    raw = brotli.decompress(blob[5:]) if blob[0] else blob[5:]
    if len(raw) != struct.unpack('<I', blob[1:5])[0]:
        raise ValueError('Invalid Lexi entry length')
    return json.loads(raw)


def encode(entry):
    raw = json.dumps(entry, ensure_ascii=False, separators=(',', ':')).encode()
    return b'\0' + struct.pack('<I', len(raw)) + raw


def headword(title, speaker):
    prefix = f'File:LL-Q1860 (eng)-{speaker}-'
    if not title.startswith(prefix) or not title.endswith('.wav'):
        return None
    # Match literal headwords only. Never remove numeric variant suffixes,
    # parenthetical qualifiers, or change case to guess which word was spoken.
    return title[len(prefix):-4]


class Commons:
    def __init__(self, cache):
        self.cache = cache
        self.cache.mkdir(parents=True, exist_ok=True)
        self.session = requests.Session()
        self.session.headers.update({'User-Agent': 'Lexi/0.1 (personal dictionary; British pronunciation import)',
                                     'Accept': 'application/json'})
        self.last_request = 0.0

    def query(self, **params):
        params.update(action='query', format='json')
        key = hashlib.sha256(json.dumps(params, sort_keys=True).encode()).hexdigest()
        path = self.cache / (key + '.json')
        if path.exists():
            return json.loads(path.read_text(encoding='utf-8'))
        for attempt in range(5):
            time.sleep(max(0, 1 - (time.monotonic() - self.last_request)))
            self.last_request = time.monotonic()
            response = self.session.get(API, params=params, timeout=60)
            if response.status_code == 429 or response.status_code >= 500:
                delay = response.headers.get('Retry-After', '')
                time.sleep(min(600, max(1, int(delay) if delay.isdigit() else 5 * (attempt + 1))))
                continue
            response.raise_for_status()
            data = response.json()
            if 'error' in data:
                raise RuntimeError(data['error'])
            path.write_text(json.dumps(data, ensure_ascii=False), encoding='utf-8')
            return data
        raise RuntimeError('Commons remains unavailable; retry later (completed requests are cached).')

    def files(self, speaker):
        continuation = {}
        result = []
        while True:
            data = self.query(list='categorymembers', cmtitle=f'Category:Lingua Libre pronunciation by {speaker}',
                              cmtype='file', cmlimit=500, **continuation)
            result.extend(data['query']['categorymembers'])
            if 'continue' not in data:
                break
            continuation = data['continue']
        print(f'{speaker}: {len(result)} Commons files', flush=True)
        return result


def inventory(db):
    words = {word: wid for wid, word in db.execute('SELECT id,word FROM words WHERE lang=1')}
    existing = defaultdict(set)
    british = set()
    for wid, blob in db.execute('SELECT e.word_id,e.data FROM entries e JOIN words w ON w.id=e.word_id WHERE w.lang=1'):
        for audio in decode(blob).get('au', []):
            existing[wid].add(audio['f'])
            if audio.get('r') in ('UK', 'UK/US'):
                british.add(wid)
    return words, existing, british


def prepare(db, api):
    words, existing, british = inventory(db)
    print(f'Before: {len(british)} English headwords with British recordings', flush=True)
    candidates = {}
    for speaker in SPEAKERS:
        for item in api.files(speaker):
            word = headword(item['title'], speaker)
            wid = words.get(word)
            if wid is not None and item['title'][5:] not in existing[wid]:
                candidates[item['pageid']] = (wid, word, speaker, item['title'])
    print(f'{len(candidates)} new recording candidates; checking per-file licenses', flush=True)
    recordings = []
    ids = list(candidates)
    for start in range(0, len(ids), 50):
        data = api.query(pageids='|'.join(map(str, ids[start:start + 50])), prop='imageinfo',
                         iiprop='url|extmetadata|mime', iiextmetadatafilter='Artist|LicenseShortName|LicenseUrl|Restrictions')
        for page in data['query']['pages'].values():
            info = page.get('imageinfo', [{}])[0]
            meta = info.get('extmetadata', {})
            license_name = meta.get('LicenseShortName', {}).get('value')
            if (license_name not in LICENSES or meta.get('Restrictions', {}).get('value')
                    or not info.get('mime', '').startswith('audio/') or not info.get('descriptionurl')):
                continue
            wid, word, speaker, title = candidates[page['pageid']]
            recordings.append(dict(word_id=wid, word=word, speaker=speaker, file=title[5:],
                                   license=license_name, license_url=meta.get('LicenseUrl', {}).get('value'),
                                   artist=meta.get('Artist', {}).get('value'), page=info['descriptionurl']))
        if start % 500 == 0:
            print(f'Checked {min(start + 50, len(ids))}/{len(ids)}', flush=True)
    new_words = {r['word_id'] for r in recordings} - british
    report = dict(before_words=len(british), added_recordings=len(recordings), added_words=len(new_words),
                  after_words=len(british | {r['word_id'] for r in recordings}),
                  newly_covered_words=sorted({r['word'] for r in recordings if r['word_id'] in new_words}),
                  recordings=recordings)
    return report


def apply(db, report, backup):
    if backup.exists():
        raise FileExistsError(f'Backup already exists: {backup}')
    with closing(sqlite3.connect(backup)) as destination:
        db.backup(destination)
    groups = defaultdict(list)
    for recording in report['recordings']:
        groups[(recording['speaker'], recording['license'], recording['word_id'])].append(recording)
    with db:
        for (speaker, license_name, wid), recordings in groups.items():
            code = SOURCE_PREFIX + re.sub('[^a-z0-9]+', '-', (speaker + '-' + license_name).lower()).strip('-')
            url = 'https://commons.wikimedia.org/wiki/Category:Lingua_Libre_pronunciation_by_' + speaker.replace(' ', '_')
            db.execute('INSERT OR IGNORE INTO sources(code,name,license,url,version,attribution,redistributable) VALUES(?,?,?,?,?,?,?)',
                       (code, f'Wikimedia Commons / Lingua Libre ({speaker})', license_name, url,
                        datetime.now(timezone.utc).date().isoformat(), speaker + '; see each Commons file page for attribution', 1))
            source_id = db.execute('SELECT id FROM sources WHERE code=?', (code,)).fetchone()[0]
            # Merge with a prior supplement, so subsequent runs are additive.
            row = db.execute('SELECT data FROM entries WHERE word_id=? AND source_id=? AND seq=0', (wid, source_id)).fetchone()
            entry = decode(row[0]) if row else {'h': recordings[0]['word'], 'au': []}
            known = {a['f'] for a in entry['au']}
            entry['au'].extend({'f': r['file'], 'r': 'UK'} for r in recordings if r['file'] not in known)
            entry['ref'] = url
            db.execute('INSERT OR REPLACE INTO entries(word_id,source_id,seq,pos,data) VALUES(?,?,0,NULL,?)',
                       (wid, source_id, encode(entry)))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--database', required=True, type=Path)
    parser.add_argument('--cache', required=True, type=Path)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    db = sqlite3.connect(args.database.resolve().as_uri() + '?mode=' + ('rw' if args.apply else 'ro'), uri=True, timeout=30)
    api = Commons(args.cache)
    report = prepare(db, api)
    (args.cache / 'manifest.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({k: v for k, v in report.items() if k not in ('recordings', 'newly_covered_words')}), flush=True)
    if args.apply and report['recordings']:
        backup = args.database.with_name(args.database.name + '.before-british-' + datetime.now().strftime('%Y%m%d-%H%M%S') + '.db')
        apply(db, report, backup)
        print(f'Applied. Backup: {backup}. Restart Lexi / Command Palette to reload sources.', flush=True)
    db.close()


if __name__ == '__main__':
    main()
