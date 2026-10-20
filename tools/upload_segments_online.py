#!/usr/bin/env python3
"""Upload validated _checked.jsonl to the Jellyfin IntroDB Online Writer admin endpoint.

Default DRY RUN. --apply is required for actual writes. Will NOT touch SQLite directly.
"""
import argparse
import json
import os
import sys
import urllib.error
import urllib.request
from pathlib import Path


def request(base, token, endpoint, payload=None, timeout=90):
    url = base.rstrip('/') + '/Plugins/IntroDBOnlineWriter/' + endpoint
    data = json.dumps(payload, separators=(',', ':')).encode() if payload is not None else None
    req = urllib.request.Request(url, data=data, method='POST' if data is not None else 'GET', headers={
        'Authorization': f'MediaBrowser Client="IntroDBOnlineWriter", Device="JellyfinCLI", DeviceId="introdb-online-upload", Version="0.1.0", Token="{token}"',
        'Accept': 'application/json',
        'Content-Type': 'application/json',
    })
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.load(resp)
    except urllib.error.HTTPError as e:
        message=e.read(1024).decode('utf-8','replace')
        raise RuntimeError(f'HTTP {e.code} at {endpoint}: {message}') from e


def main():
    ap=argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--input', required=True, help='ONLY a validated *_checked.jsonl file')
    ap.add_argument('--url', default=os.getenv('JELLYFIN_URL','http://127.0.0.1:8096'))
    ap.add_argument('--batch-size', type=int, default=25)
    ap.add_argument('--max-items', type=int, default=5, help='0 = unlimited; default 5 for initial pilot')
    ap.add_argument('--apply', action='store_true')
    args=ap.parse_args()
    if not (1 <= args.batch_size <= 100):
        ap.error('batch-size must be 1-100')
    if args.max_items < 0:
        ap.error('max-items must be >= 0')
    if not args.input.endswith('_checked.jsonl'):
        ap.error('Use validated *_checked.jsonl, not raw or quarantine')
    token=os.getenv('JELLYFIN_API_KEY','').strip()
    if not token:
        ap.error('Export JELLYFIN_API_KEY first')
    records=[]
    seen=set()
    for lineno,line in enumerate(Path(args.input).read_text(encoding='utf-8').splitlines(),1):
        if not line.strip():
            continue
        obj=json.loads(line)
        item_id=str(obj.get('item_id','')).replace('-','').lower()
        if len(item_id)!=32 or not all(c in '0123456789abcdef' for c in item_id):
            ap.error(f'Bad item ID on line {lineno}')
        if item_id in seen:
            ap.error(f'Duplicate episode ID at line {lineno}; resolve before uploading')
        if not isinstance(obj.get('segments'),list) or not obj['segments']:
            ap.error(f'No segments at line {lineno}')
        seen.add(item_id)
        records.append({'item_id': obj['item_id'], 'segments': obj['segments']})
        if args.max_items and len(records) >= args.max_items:
            break
    if not records:
        print('No validated records to upload.'); return
    print('Plugin status:',json.dumps(request(args.url,token,'Status')))
    print(f'Mode: {"APPLY" if args.apply else "DRY RUN"}; candidates={len(records)}')
    totals={'EpisodesToInsert':0,'SegmentsToInsert':0,'EpisodesInserted':0,'SegmentsInserted':0}
    for start in range(0,len(records),args.batch_size):
        chunk=records[start:start+args.batch_size]
        payload={'records':chunk}
        result=request(args.url,token,'Import?dryRun=true',payload)
        print(f'Batch {start//args.batch_size+1}: preflight {result}')
        for key in ('EpisodesToInsert','SegmentsToInsert'):
            totals[key]+=int(result.get(key,0))
        if args.apply and result.get('EpisodesToInsert',0):
            result=request(args.url,token,'Import?dryRun=false',payload)
            print(f'Batch {start//args.batch_size+1}: applied {result}')
            for key in ('EpisodesInserted','SegmentsInserted'):
                totals[key]+=int(result.get(key,0))
    print('Totals:', json.dumps(totals,indent=2))
    print('NOTE: Jellyfin service remains running. Verify native /MediaSegments/{itemId} and client playback.')

if __name__=='__main__':
    try:
        main()
    except (RuntimeError,urllib.error.URLError,ValueError) as e:
        print('ERROR:', e, file=sys.stderr)
        sys.exit(1)
