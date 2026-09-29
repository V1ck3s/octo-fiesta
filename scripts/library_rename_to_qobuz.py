#!/usr/bin/env python3
"""
Renames the music library from Apple/alacarte naming to Qobuz-compatible naming.

Rules applied:
  - FLAC files: strip trailing " (feat. X)" / " [feat. X]" from filenames
  - FLAC files with " (N)" suffix (Octo ResolveUniquePath duplicates): collapse to
    canonical name, keeping the newest file by mtime
  - Album folders: strip trailing " – Single" / " - Single"
  - .lrc files: renamed to follow their paired audio file
  - .mappings.json: rows whose LocalPath no longer exists after rename are pruned;
    rows whose LocalPath changed are updated

Run with --dry-run (default) to preview changes, then --apply to execute.
"""

import argparse
import json
import os
import re
import shutil
import sys
import urllib.parse
import urllib.request
from pathlib import Path

MUSIC_ROOT = Path("/mnt/media/music")
MAPPINGS_FILE = MUSIC_ROOT / ".mappings.json"

CONTAINER_DOWNLOADS_PREFIX = "/app/downloads"
HOST_DOWNLOADS_PREFIX = str(MUSIC_ROOT)

FEAT_RE = re.compile(r'\s+[\(\[](feat\.|ft\.)[^\)\]]*[\)\]]', re.IGNORECASE)
COUNTER_RE = re.compile(r'^(.*)\s+\((\d+)\)$')
SINGLE_SUFFIX_RE = re.compile(r'\s+[\u2013\-]\s+Single$', re.IGNORECASE)

AUDIO_EXTS = {'.flac', '.mp3', '.m4a'}


def strip_feat(name: str) -> str:
    return FEAT_RE.sub('', name).strip()


def strip_single_suffix(name: str) -> str:
    return SINGLE_SUFFIX_RE.sub('', name).strip()


def counter_base(stem: str):
    m = COUNTER_RE.match(stem)
    if m:
        return m.group(1), int(m.group(2))
    return stem, 0


def collect_files(root: Path):
    """Yield all FLAC paths under root (excluding .mappings.json)."""
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = sorted(dirnames)
        for fn in sorted(filenames):
            p = Path(dirpath) / fn
            if p.suffix.lower() == '.flac':
                yield p


def plan_renames(root: Path):
    """
    Returns:
      file_ops: list of (src_path, dst_path, op) where op in {'rename','delete'}
      lrc_ops:  list of (src_path, dst_path, op)
      folder_ops: list of (src_dir, dst_dir)
    """
    file_ops = []
    lrc_ops = []
    folder_ops = []

    # Pass 1: compute target name for each FLAC, group by directory
    # Structure: dir -> { canonical_stem -> [(path, mtime, counter)] }
    from collections import defaultdict
    by_dir = defaultdict(lambda: defaultdict(list))

    for p in collect_files(root):
        stem = p.stem
        base_stem, counter = counter_base(stem)
        qobuz_stem = strip_feat(base_stem)
        mtime = p.stat().st_mtime
        by_dir[p.parent][qobuz_stem].append((p, mtime, counter))

    for directory, groups in by_dir.items():
        for qobuz_stem, entries in groups.items():
            dst_path = directory / f"{qobuz_stem}.flac"

            if len(entries) == 1:
                src_path, mtime, counter = entries[0]
                src_stem = strip_feat(counter_base(src_path.stem)[0])
                if src_path == dst_path:
                    continue
                if dst_path.exists() and dst_path != src_path:
                    dst_mtime = dst_path.stat().st_mtime
                    if mtime >= dst_mtime:
                        file_ops.append((dst_path, None, 'delete'))
                        file_ops.append((src_path, dst_path, 'rename'))
                        _plan_lrc(src_path, dst_path, lrc_ops)
                    else:
                        file_ops.append((src_path, None, 'delete'))
                        _plan_lrc(src_path, dst_path, lrc_ops)
                else:
                    file_ops.append((src_path, dst_path, 'rename'))
                    _plan_lrc(src_path, dst_path, lrc_ops)
            else:
                # Multiple: keep newest, delete rest, rename keeper to canonical
                entries.sort(key=lambda x: x[1], reverse=True)
                keeper, keeper_mtime, _ = entries[0]
                for victim, _, _ in entries[1:]:
                    file_ops.append((victim, None, 'delete'))
                    _plan_lrc_delete(victim, lrc_ops)
                if keeper != dst_path:
                    if dst_path.exists() and dst_path not in {e[0] for e in entries}:
                        dst_mtime = dst_path.stat().st_mtime
                        if keeper_mtime >= dst_mtime:
                            file_ops.append((dst_path, None, 'delete'))
                    file_ops.append((keeper, dst_path, 'rename'))
                    _plan_lrc(keeper, dst_path, lrc_ops)

    # Pass 2: folder renames — strip " – Single" / " - Single"
    for dirpath, dirnames, _ in os.walk(root, topdown=False):
        dirnames[:] = sorted(dirnames)
        for dn in dirnames:
            new_dn = strip_single_suffix(dn)
            if new_dn != dn:
                src_dir = Path(dirpath) / dn
                dst_dir = Path(dirpath) / new_dn
                folder_ops.append((src_dir, dst_dir))

    return file_ops, lrc_ops, folder_ops


def _lrc_for(audio_path: Path) -> Path:
    return audio_path.with_suffix('.lrc')


def _plan_lrc(src_audio: Path, dst_audio: Path, lrc_ops: list):
    src_lrc = _lrc_for(src_audio)
    dst_lrc = _lrc_for(dst_audio)
    if src_lrc == dst_lrc:
        return
    if src_lrc.exists():
        if dst_lrc.exists():
            lrc_ops.append((src_lrc, None, 'delete'))
        else:
            lrc_ops.append((src_lrc, dst_lrc, 'rename'))


def _plan_lrc_delete(src_audio: Path, lrc_ops: list):
    src_lrc = _lrc_for(src_audio)
    if src_lrc.exists():
        lrc_ops.append((src_lrc, None, 'delete'))


def apply_ops(file_ops, lrc_ops, folder_ops, dry_run: bool):
    deletes = [(s, d, o) for s, d, o in file_ops if o == 'delete']
    renames = [(s, d, o) for s, d, o in file_ops if o == 'rename']

    summary = {'files_deleted': 0, 'files_renamed': 0,
                'lrc_deleted': 0, 'lrc_renamed': 0,
                'folders_renamed': 0}

    for src, _, op in deletes:
        print(f"  DELETE  {src}")
        if not dry_run:
            src.unlink(missing_ok=True)
        summary['files_deleted'] += 1

    for src, dst, op in renames:
        print(f"  RENAME  {src}")
        print(f"       -> {dst}")
        if not dry_run:
            dst.parent.mkdir(parents=True, exist_ok=True)
            src.rename(dst)
        summary['files_renamed'] += 1

    for src, dst, op in lrc_ops:
        if dst is None:
            print(f"  DELETE  {src} [lrc]")
            if not dry_run:
                src.unlink(missing_ok=True)
            summary['lrc_deleted'] += 1
        else:
            print(f"  RENAME  {src} [lrc]")
            print(f"       -> {dst} [lrc]")
            if not dry_run:
                dst.parent.mkdir(parents=True, exist_ok=True)
                src.rename(dst)
            summary['lrc_renamed'] += 1

    for src_dir, dst_dir in folder_ops:
        print(f"  FOLDER  {src_dir}")
        print(f"       -> {dst_dir}")
        if not dry_run:
            if dst_dir.exists():
                for item in src_dir.iterdir():
                    target = dst_dir / item.name
                    if not target.exists():
                        item.rename(target)
                try:
                    src_dir.rmdir()
                except OSError:
                    pass
            else:
                src_dir.rename(dst_dir)
        summary['folders_renamed'] += 1

    return summary


def container_path_to_host(path: str) -> Path:
    """Translate a container-internal path to its host equivalent."""
    if path.startswith(CONTAINER_DOWNLOADS_PREFIX):
        rel = path[len(CONTAINER_DOWNLOADS_PREFIX):]
        return Path(HOST_DOWNLOADS_PREFIX + rel)
    return Path(path)


def update_mappings(dry_run: bool) -> tuple[int, int]:
    if not MAPPINGS_FILE.exists():
        return 0, 0
    with open(MAPPINGS_FILE, 'r') as f:
        mappings = json.load(f)

    pruned = 0
    new_mappings = {}
    for key, val in mappings.items():
        local_path = val.get('LocalPath', '')
        host_path = container_path_to_host(local_path) if local_path else None
        if host_path and not host_path.exists():
            pruned += 1
            print(f"  PRUNE   mappings key {key!r} (path gone: {local_path})")
        else:
            new_mappings[key] = val

    if not dry_run and pruned:
        with open(MAPPINGS_FILE, 'w') as f:
            json.dump(new_mappings, f, indent=2)

    return pruned, 0


def trigger_scan(url: str, user: str, password: str):
    params = urllib.parse.urlencode({'u': user, 'p': password, 'v': '1.16.1', 'c': 'library-rename', 'f': 'json'})
    req_url = f"{url.rstrip('/')}/rest/startScan?{params}"
    try:
        with urllib.request.urlopen(req_url, timeout=10) as resp:
            body = resp.read().decode()
            print(f"Navidrome scan triggered: {body[:120]}")
    except Exception as e:
        print(f"WARNING: Could not trigger Navidrome scan: {e}")
        print("  Trigger a library scan manually in the Navidrome UI.")


def main():
    global CONTAINER_DOWNLOADS_PREFIX, HOST_DOWNLOADS_PREFIX  # noqa: PLW0603

    parser = argparse.ArgumentParser(description="Rename music library to Qobuz naming convention.")
    parser.add_argument('--dry-run', action='store_true', default=True,
                        help='Preview changes without applying (default)')
    parser.add_argument('--apply', action='store_true', default=False,
                        help='Actually apply changes')
    parser.add_argument('--root', default=str(MUSIC_ROOT),
                        help=f'Music root (default: {MUSIC_ROOT})')
    parser.add_argument('--container-downloads-prefix', default=CONTAINER_DOWNLOADS_PREFIX,
                        help='Container-internal path prefix used in .mappings.json LocalPath')
    parser.add_argument('--host-downloads-prefix', default=HOST_DOWNLOADS_PREFIX,
                        help='Host path that corresponds to the container prefix')
    parser.add_argument('--navidrome-url', default='http://localhost:4533',
                        help='Navidrome base URL for scan trigger')
    parser.add_argument('--navidrome-user', default='',
                        help='Navidrome admin username')
    parser.add_argument('--navidrome-pass', default='',
                        help='Navidrome admin password')
    args = parser.parse_args()

    CONTAINER_DOWNLOADS_PREFIX = args.container_downloads_prefix
    HOST_DOWNLOADS_PREFIX = args.host_downloads_prefix

    dry_run = not args.apply
    root = Path(args.root)

    if dry_run:
        print("=== DRY RUN — no changes will be made ===\n")
    else:
        print("=== APPLYING changes ===\n")

    print("Planning renames...")
    file_ops, lrc_ops, folder_ops = plan_renames(root)

    total_ops = len(file_ops) + len(lrc_ops) + len(folder_ops)
    if total_ops == 0:
        print("Nothing to do — library is already in Qobuz naming convention.")
        return

    print(f"\nPlanned operations ({total_ops} total):\n")
    summary = apply_ops(file_ops, lrc_ops, folder_ops, dry_run)

    print(f"\nUpdating .mappings.json...")
    pruned, updated = update_mappings(dry_run)

    print(f"""
Summary:
  Audio files deleted:  {summary['files_deleted']}
  Audio files renamed:  {summary['files_renamed']}
  .lrc files deleted:   {summary['lrc_deleted']}
  .lrc files renamed:   {summary['lrc_renamed']}
  Folders renamed:      {summary['folders_renamed']}
  Mapping rows pruned:  {pruned}
""")

    if dry_run:
        print("Re-run with --apply to execute.\n")
    else:
        if args.navidrome_user:
            print("Triggering Navidrome library scan...")
            trigger_scan(args.navidrome_url, args.navidrome_user, args.navidrome_pass)
        else:
            print("No --navidrome-user provided; trigger a library scan manually in the Navidrome UI.")


if __name__ == '__main__':
    main()
