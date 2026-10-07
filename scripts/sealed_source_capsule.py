"""Narrow raw-source decoding. Callers own fixed repository, graph and execution authority."""
import base64
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import subprocess
import sys
import time
import unicodedata
import urllib.request
import zipfile
import zlib

MAX_API_BYTES = 2 * 1024 * 1024
MAX_ARCHIVE_BYTES = 1024 * 1024
MAX_FILE_BYTES = 2 * 1024 * 1024
MAX_EXPANDED_BYTES = 8 * 1024 * 1024
MAX_ENTRIES = 256


def digest(data):
    return hashlib.sha256(data).hexdigest()


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('duplicate JSON key')
        result[key] = value
    return result


def parse_json(data):
    return json.loads(data, object_pairs_hook=unique_object,
                      parse_constant=lambda _: (_ for _ in ()).throw(ValueError('nonfinite JSON value')))


def canonical_path(value):
    if not isinstance(value, str) or not value or len(value) > 512 or '\\' in value or any(c in value for c in ':<>"|?*'):
        raise ValueError('invalid canonical source path')
    if unicodedata.normalize('NFC', value) != value:
        raise ValueError('noncanonical Unicode path')
    parts = value.split('/')
    reserved = {'con', 'prn', 'aux', 'nul', *('com'+str(i) for i in range(1,10)), *('lpt'+str(i) for i in range(1,10)), *('com'+i for i in '\u00b9\u00b2\u00b3'), *('lpt'+i for i in '\u00b9\u00b2\u00b3')}
    if any(p in ('', '.', '..') or p.casefold() == '.git' or p.endswith((' ', '.')) or
           p.split('.')[0].casefold() in reserved or any(ord(c) < 32 for c in p) for p in parts):
        raise ValueError('noncanonical source path')
    if str(PurePosixPath(value)) != value:
        raise ValueError('noncanonical source path')
    return value


def decode_git_blob(response, oid, maximum=MAX_ARCHIVE_BYTES):
    if not re.fullmatch('[0-9a-f]{40}', oid) or len(response) > MAX_API_BYTES:
        raise ValueError('invalid/bounded Git blob response')
    obj = parse_json(response)
    if obj.get('sha') != oid or obj.get('encoding') != 'base64' or not isinstance(obj.get('content'), str):
        raise ValueError('Git blob identity/encoding mismatch')
    content = obj['content'].replace('\n', '')
    data = base64.b64decode(content, validate=True)
    if base64.b64encode(data).decode() != content or type(obj.get('size')) is not int or obj['size'] != len(data) or len(data) > maximum:
        raise ValueError('noncanonical/bounded Git blob size')
    actual = hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()
    if actual != oid:
        raise ValueError('Git blob object hash mismatch')
    return data


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise ValueError('Git blob redirects prohibited')


def read_deadline(response, deadline, clock=time.monotonic):
    chunks = []; size = 0
    while True:
        remaining = deadline - clock()
        if remaining <= 0:
            raise TimeoutError('Git blob total deadline exceeded')
        if response.isclosed():
            if response.length not in (None, 0):
                raise ValueError('Git blob HTTP body truncated')
            return b''.join(chunks)
        # urllib HTTPSResponse exposes its connected socket through this chain.
        # Fail closed if the runtime cannot enforce the remaining blocking budget.
        response.fp.raw._sock.settimeout(remaining)
        chunk = response.read1(min(65536, MAX_API_BYTES + 1 - size))
        if clock() >= deadline:
            raise TimeoutError('Git blob total deadline exceeded')
        if not chunk:
            return b''.join(chunks)
        chunks.append(chunk); size += len(chunk)
        if size > MAX_API_BYTES:
            raise ValueError('Git blob response exceeds bound')


def _fetch_response(repository, oid):
    # Repository authority is fixed by the reviewed caller, never taken from a URL.
    if not re.fullmatch('[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository) or not re.fullmatch('[0-9a-f]{40}', oid):
        raise ValueError('invalid Git blob address')
    headers = {'Accept':'application/vnd.github+json', 'User-Agent':'sealed-source-intake'}
    token = os.environ.get('GH_TOKEN')
    if token:
        headers['Authorization'] = 'Bearer '+token
    req = urllib.request.Request(f'https://api.github.com/repos/{repository}/git/blobs/{oid}', headers=headers)
    deadline = time.monotonic() + 20
    with urllib.request.build_opener(NoRedirect()).open(req, timeout=20) as response:
        data = read_deadline(response, deadline)
    return data


def fetch_git_blob(repository, oid, maximum=MAX_ARCHIVE_BYTES):
    if not re.fullmatch('[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository) or not re.fullmatch('[0-9a-f]{40}', oid):
        raise ValueError('invalid Git blob address')
    deadline = time.monotonic() + 20
    # Retain the exact Popen process handle until exit. The child receives no
    # token argument, writes bounded API bytes only, and has no descendants.
    with subprocess.Popen([sys.executable, '-B', str(Path(__file__).resolve()),
                           '--fetch-response', repository, oid],
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE) as owned:
        try:
            response, _ = owned.communicate(timeout=max(0.001, deadline-time.monotonic()))
            if time.monotonic() >= deadline:
                raise TimeoutError('Git blob total deadline exceeded')
            if owned.returncode != 0:
                raise ValueError('Git blob fetch worker failed')
            return decode_git_blob(response, oid, maximum)
        except subprocess.TimeoutExpired:
            raise TimeoutError('Git blob total deadline exceeded') from None
        finally:
            if owned.poll() is None:
                owned.kill()
            owned.wait()
            # Popen context closes both pipe handles after verified exit.


def validate_zip(data, expected_sha256, expected_bytes, rows):
    if len(data) != expected_bytes or len(data) > MAX_ARCHIVE_BYTES or digest(data) != expected_sha256:
        raise ValueError('sealed archive size/digest mismatch')
    if not 0 < len(rows) <= MAX_ENTRIES or sum(row['bytes'] for row in rows) > MAX_EXPANDED_BYTES:
        raise ValueError('expanded source inventory exceeds bounds')
    expected = {}; aliases = set()
    for row in rows:
        name = canonical_path(row['path']); alias=name.casefold()
        if name in expected or alias in aliases or type(row['bytes']) is not int or not 0 <= row['bytes'] <= MAX_FILE_BYTES or not re.fullmatch('[0-9a-f]{64}', row['sha256']):
            raise ValueError('invalid/aliased source inventory')
        aliases.add(alias); expected[name]=row
    files = {}
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        if len(archive.infolist()) != len(expected) or archive.comment:
            raise ValueError('archive inventory/comment differs')
        for info in archive.infolist():
            name=canonical_path(info.filename)
            if name not in expected or name in files:
                raise ValueError('extra/duplicate archive source')
            row=expected[name]
            mode=info.external_attr >> 16
            # Frozen DOS entries with mode0600/type0 are regular files; no directory/reparse/symlink interpretation.
            if info.is_dir() or stat.S_IFMT(mode) not in (0,stat.S_IFREG) or info.external_attr & 0x10:
                raise ValueError('archive member is not a regular file')
            actual=(info.file_size,info.compress_size,info.CRC,info.create_system,info.external_attr,info.flag_bits,info.compress_type,list(info.date_time))
            declared=(row['bytes'],row['compressedBytes'],row['crc32'],row['createSystem'],row['externalAttributes'],row['flags'],row['compression'],row['dateTime'])
            if actual != declared or info.flag_bits & 1 or info.extra or info.comment or info.compress_type != zipfile.ZIP_DEFLATED:
                raise ValueError('archive canonical header differs')
            with archive.open(info) as source:
                raw=source.read(row['bytes']+1)
            if len(raw) != row['bytes'] or digest(raw) != row['sha256'] or zlib.crc32(raw) & 0xffffffff != row['crc32']:
                raise ValueError('archive raw bytes/CRC differ')
            files[name]=raw
    if set(files) != set(expected):
        raise ValueError('archive source missing')
    return files


def reject_links(path):
    path=Path(path)
    for target in (path,*path.parents):
        if target.exists() or target.is_symlink():
            metadata=target.lstat()
            if stat.S_ISLNK(metadata.st_mode) or getattr(metadata,'st_file_attributes',0) & 0x400:
                raise ValueError('symlink/reparse source or target')


def write_new(root, relative, raw):
    root=Path(root); canonical_path(relative)
    target=root/relative
    reject_links(target)
    target.parent.mkdir(parents=True,exist_ok=True)
    reject_links(target.parent)
    with target.open('xb') as stream:
        stream.write(raw)
    if digest(target.read_bytes()) != digest(raw):
        raise ValueError('raw write/readback mismatch')
    return target


if __name__ == '__main__':
    if len(sys.argv) != 4 or sys.argv[1] != '--fetch-response':
        raise SystemExit('Only bounded fetch worker mode is supported')
    try:
        sys.stdout.buffer.write(_fetch_response(sys.argv[2], sys.argv[3]))
    except Exception:
        # Provider errors may contain sensitive headers; never emit them.
        raise SystemExit('Bounded Git blob fetch failed') from None
