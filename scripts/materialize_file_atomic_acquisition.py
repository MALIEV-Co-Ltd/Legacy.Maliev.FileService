"""Two reviewed tracked postimages for ordinary atomic-acquisition RED/GREEN only."""
import argparse
import base64
import json
import os
from pathlib import Path
import subprocess
import tempfile
from sealed_source_capsule import digest, fetch_git_blob, parse_json, reject_links

REPOSITORY = 'MALIEV-Co-Ltd/Legacy.Maliev.FileService'
BASE = '37a08862831a78cd36c7459ab24dd0594d07cb47'
SOURCES = {
    'Legacy.Maliev.FileService.Data/RedisUploadIdempotencyStore.cs':
        (8174, '78e58385d62a6c3cc5b37a177ba91b8545883d320fc9b43940b508f1060d35c7',
         8569, '4365715ddf8e1c023691c5fe5bed53d56907c0b1150d3f445224b1168a922240'),
    'Legacy.Maliev.FileService.Tests/Data/RedisUploadIdempotencyStoreTests.cs':
        (8170, '50dd0acfb5f3f52c6189fe09a46e079388893f57a8b3a9ae97e8c4c3d859905a',
         29355, '7862e0be198e4030b91e056e0ef4053af3bee080de07bd9c8c96aeb0bb6065a9'),
}


def decode(raw):
    if len(raw) > 65536:
        raise ValueError('capsule exceeds bound')
    capsule = parse_json(raw)
    if type(capsule) is not dict or set(capsule) != {'base', 'files'} or capsule['base'] != BASE:
        raise ValueError('wrong capsule authority')
    if type(capsule['files']) is not list or len(capsule['files']) != 2:
        raise ValueError('exactly two reviewed sources required')
    decoded = {}
    for row, (name, authority) in zip(capsule['files'], SOURCES.items()):
        if type(row) is not dict or set(row) != {'path', 'bytes', 'sha256', 'contentBase64'}:
            raise ValueError('unexpected source properties')
        if row['path'] != name or type(row['bytes']) is not int or row['bytes'] != authority[2] or row['sha256'] != authority[3]:
            raise ValueError('source authority differs')
        source = base64.b64decode(row['contentBase64'], validate=True)
        if base64.b64encode(source).decode() != row['contentBase64'] or len(source) != authority[2] or digest(source) != authority[3]:
            raise ValueError('source bytes differ')
        decoded[name] = source
    return decoded


def identity(path):
    reject_links(path)
    stat = path.lstat()
    if not path.is_file():
        raise ValueError('source is not a regular file')
    return stat.st_dev, stat.st_ino


def temporary(target, source):
    with tempfile.NamedTemporaryFile(prefix='.codex-file-atomic-', dir=target.parent, delete=False) as stream:
        path = Path(stream.name)
        owned = (os.fstat(stream.fileno()).st_dev, os.fstat(stream.fileno()).st_ino)
        try:
            stream.write(source)
            stream.flush()
            os.fsync(stream.fileno())
            if digest(path.read_bytes()) != digest(source):
                raise ValueError('temporary write differs')
        except BaseException:
            stream.close()
            if identity(path) != owned:
                raise ValueError('temporary rollback ownership uncertain') from None
            path.unlink()
            if path.exists():
                raise ValueError('temporary rollback not verified') from None
            raise
    return path, owned


def materialize(root, sources, phase):
    if phase not in {'red', 'green'} or list(sources) != list(SOURCES):
        raise ValueError('fixed phase and exact sources required')
    root = Path(root).absolute()
    reject_links(root)
    root = root.resolve(strict=True)
    for args, expected in [(['rev-parse', 'HEAD'], BASE), (['status', '--porcelain', '--untracked-files=all'], '')]:
        actual = subprocess.run(['git', '-C', str(root), *args], capture_output=True, check=True, timeout=20).stdout.decode().strip()
        if actual != expected:
            raise ValueError('checkout is not clean exact base')
    snapshots = {}
    for name, authority in SOURCES.items():
        data = sources[name]
        if len(data) != authority[2] or digest(data) != authority[3]:
            raise ValueError('postimage differs')
        target = root / name
        owned = identity(target)
        original = target.read_bytes()
        if len(original) != authority[0] or digest(original) != authority[1] or identity(target) != owned:
            raise ValueError('tracked preimage differs')
        snapshots[name] = (target, owned, original)
    selected = list(SOURCES) if phase == 'green' else [list(SOURCES)[1]]
    applied = []
    pending = []
    try:
        for name in selected:
            target, before, original = snapshots[name]
            path, owned = temporary(target, sources[name])
            pending.append((path, owned))
            if identity(target) != before or target.read_bytes() != original:
                raise ValueError('preimage ownership changed')
            os.replace(path, target)
            pending.pop()
            applied.append((target, owned, original, sources[name]))
            if identity(target) != owned or target.read_bytes() != sources[name]:
                raise ValueError('source write/readback differs')
    except BaseException as primary:
        for target, owned, original, postimage in reversed(applied):
            if identity(target) != owned or target.read_bytes() != postimage:
                raise ValueError('source rollback ownership uncertain') from primary
            path, rollback_owned = temporary(target, original)
            pending.append((path, rollback_owned))
            if identity(target) != owned or target.read_bytes() != postimage:
                raise ValueError('source rollback ownership changed') from primary
            os.replace(path, target)
            pending.pop()
            if target.read_bytes() != original:
                raise ValueError('source rollback not verified') from primary
        raise
    finally:
        for path, owned in pending:
            if identity(path) != owned:
                raise ValueError('temporary cleanup ownership uncertain')
            path.unlink()
            if path.exists():
                raise ValueError('temporary cleanup not verified')
    return selected


def main():
    if os.environ.get('GITHUB_REPOSITORY') != REPOSITORY or os.environ.get('GITHUB_REF') != 'refs/heads/main' or os.environ.get('GITHUB_EVENT_NAME') != 'workflow_dispatch':
        raise ValueError('protected-main dispatch required')
    parser = argparse.ArgumentParser()
    parser.add_argument('--blob', required=True)
    parser.add_argument('--checkout', required=True)
    parser.add_argument('--phase', required=True, choices=['red', 'green'])
    parser.add_argument('--receipt', required=True)
    args = parser.parse_args()
    raw = fetch_git_blob(REPOSITORY, args.blob, maximum=65536)
    selected = materialize(args.checkout, decode(raw), args.phase)
    target = Path(args.receipt)
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open('x', encoding='utf-8') as receipt:
        json.dump({'base': BASE, 'blob': args.blob, 'capsuleSha256': digest(raw), 'phase': args.phase,
                   'paths': selected, 'expectedFocused': 1 if args.phase == 'red' else 16,
                   'expectedFull': 1562, 'runtimeLockCycleProven': False, 'nativeAcceptance': False}, receipt, indent=2)


if __name__ == '__main__':
    main()
