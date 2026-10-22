"""One reviewed ordinary File diagnostic; no native custody or grants."""
import argparse
import base64
import json
import os
from pathlib import Path
import subprocess
from sealed_source_capsule import digest, fetch_git_blob, parse_json, reject_links

REPOSITORY = 'MALIEV-Co-Ltd/Legacy.Maliev.FileService'
BASE = '0d7a329f2360a6143314af8cce8c13ec1441232a'
SOURCE = 'Legacy.Maliev.FileService.Tests/Data/RedisBootstrapDiscriminatorTests.cs'
SOURCE_BYTES = 16220
SOURCE_SHA256 = '850f2ea9e4d3a7dc6ddb85e1285f7c069bd163f8c9c44b6288d52fd12b839123'


def decode(raw):
    if len(raw) > 32768:
        raise ValueError('capsule exceeds bound')
    capsule = parse_json(raw)
    if set(capsule) != {'base', 'files'} or capsule['base'] != BASE:
        raise ValueError('wrong capsule authority')
    if type(capsule['files']) is not list or len(capsule['files']) != 1:
        raise ValueError('exactly one source required')
    row = capsule['files'][0]
    if set(row) != {'path', 'bytes', 'sha256', 'contentBase64'}:
        raise ValueError('unexpected source properties')
    if row['path'] != SOURCE or type(row['bytes']) is not int or row['bytes'] != SOURCE_BYTES or row['sha256'] != SOURCE_SHA256:
        raise ValueError('source authority differs')
    source = base64.b64decode(row['contentBase64'], validate=True)
    if base64.b64encode(source).decode() != row['contentBase64'] or len(source) != SOURCE_BYTES or digest(source) != SOURCE_SHA256:
        raise ValueError('source bytes differ')
    return source


def materialize(root, source):
    root = Path(root).resolve(strict=True)
    for args, expected in [(['rev-parse', 'HEAD'], BASE), (['status', '--porcelain', '--untracked-files=all'], '')]:
        actual = subprocess.run(['git', '-C', str(root), *args], capture_output=True, check=True, timeout=20).stdout.decode().strip()
        if actual != expected:
            raise ValueError('checkout is not clean exact base')
    if len(source) != SOURCE_BYTES or digest(source) != SOURCE_SHA256:
        raise ValueError('postimage differs')
    target = root / SOURCE
    reject_links(target)
    if not target.parent.is_dir():
        raise ValueError('expected source directory absent')
    owned_identity = None
    try:
        with target.open('xb') as stream:
            stat = os.fstat(stream.fileno())
            owned_identity = (stat.st_dev, stat.st_ino)
            stream.write(source)
            stream.flush()
            if digest(target.read_bytes()) != SOURCE_SHA256:
                raise ValueError('source write/readback differs')
        return target
    except BaseException:
        if owned_identity is not None:
            reject_links(target)
            stat = target.lstat()
            if (stat.st_dev, stat.st_ino) != owned_identity:
                raise ValueError('source rollback ownership uncertain') from None
            target.unlink()
            if target.exists():
                raise ValueError('owned source rollback not verified') from None
        raise


def main():
    if os.environ.get('GITHUB_REPOSITORY') != REPOSITORY or os.environ.get('GITHUB_REF') != 'refs/heads/main' or os.environ.get('GITHUB_EVENT_NAME') != 'workflow_dispatch':
        raise ValueError('protected-main dispatch required')
    parser = argparse.ArgumentParser()
    parser.add_argument('--blob', required=True)
    parser.add_argument('--checkout', required=True)
    parser.add_argument('--receipt', required=True)
    args = parser.parse_args()
    raw = fetch_git_blob(REPOSITORY, args.blob, maximum=32768)
    source = decode(raw)
    materialize(args.checkout, source)
    target = Path(args.receipt)
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open('x', encoding='utf-8') as receipt:
        json.dump({'base': BASE, 'blob': args.blob, 'capsuleSha256': digest(raw), 'path': SOURCE,
                   'bytes': len(source), 'sha256': digest(source), 'expectedFull': 1074,
                   'nativeAcceptance': False}, receipt, indent=2)


if __name__ == '__main__':
    main()
