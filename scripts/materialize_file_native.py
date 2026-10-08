"""Exact V28 Linux source intake. No SDK, Docker mutation or Root authority minting."""
import argparse
import json
from pathlib import Path

from materialize_file_candidate import read_base
from sealed_source_capsule import canonical_path, digest, fetch_git_blob, parse_json, reject_links, validate_zip, write_new

REPOSITORY = 'MALIEV-Co-Ltd/Legacy.Maliev.FileService'
BASE = '07f0b5b721e3df758a9ec742c6f167491f899e44'
ROOT = '/tmp/maliev-file-native-v28-20261009'
POLICY_SHA256 = 'b2da9ac0fc17a5c70501c6507e63936e5f624dda140e0107ea8715ce5743f947'


def load_policy(path):
    path = Path(path); reject_links(path)
    if not path.is_file() or path.stat().st_size > 512*1024:
        raise ValueError('Bounded original Linux policy required')
    with path.open('rb') as stream: raw = stream.read(512*1024+1)
    if len(raw) > 512*1024: raise ValueError('Linux policy quota exceeded')
    if digest(raw) != POLICY_SHA256:
        raise ValueError('Exact reviewed Linux policy bytes required')
    value = parse_json(raw)
    if value['repository'] != REPOSITORY or value['acceptedBase'] != BASE or value['root'] != ROOT:
        raise ValueError('Fixed Linux source profile differs')
    if value['candidateFiles'] != 36 or len(value['entries']) != 204 or len(value['baseFiles']) != 179:
        raise ValueError('Closed source inventory differs')
    if value['phases'] != ['build','focused','full','format','audit'] or value['sdkAuthorizedWithoutOriginalRootGrant'] is not False:
        raise ValueError('Closed phase and original authority profile differs')
    return value


def validate_bindings(files, policy):
    for path, key in [('candidate/manifest.json','candidateManifestSha256'), ('supervisor/manifest.json','supervisorManifestSha256'),
                      ('dependency-manifest.json','dependencyManifestSha256'), ('transport-index.json','transportIndexSha256')]:
        if digest(files[path]) != policy[key]: raise ValueError('Original nested source seal differs')
    candidate = parse_json(files['candidate/manifest.json'])
    if candidate['base'] != BASE or str(candidate['worktree']).replace(chr(92),'/') != policy['frozenCandidateWorktree'] or len(candidate['files']) != 36:
        raise ValueError('Frozen source identity differs')
    seen = set()
    for row in candidate['files']:
        path = canonical_path(row['path'])
        if path in seen or digest(files['candidate/raw/'+path]) != row['preparedSha256']:
            raise ValueError('Duplicate or changed candidate source')
        seen.add(path)
    if digest(files['candidate/complete.diff']) != candidate['patchSha256']:
        raise ValueError('Frozen complete patch differs')
    supervisor = parse_json(files['supervisor/manifest.json'])
    if supervisor['candidateManifestSha256'] != policy['candidateManifestSha256'] or supervisor['dependencyManifestSha256'] != policy['dependencyManifestSha256']:
        raise ValueError('Original supervisor source bindings differ')
    if supervisor['files'] != policy['supervisorFiles'] or len(supervisor['files']) != 4:
        raise ValueError('Closed Linux supervisor inventory differs')
    for row in supervisor['files']:
        if digest(files['supervisor/'+canonical_path(row['sealedCopy'])]) != row['sha256'] or files['supervisor/'+row['sealedCopy']] != files['candidate/raw/'+row['path']]:
            raise ValueError('Runtime helper differs between source and supervisor seals')
    dependencies = parse_json(files['dependency-manifest.json'])
    if len(dependencies['files']) != 130:
        raise ValueError('Pinned dependency inventory differs')
    for row in dependencies['files']:
        if digest(files['dependencies/'+canonical_path(row['repository']+'/'+row['path'])]) != row['sha256']:
            raise ValueError('Pinned raw dependency source differs')
    return candidate


def materialize(root, source, metadata, files, policy):
    root = Path(root); reject_links(root)
    if root.exists(): raise ValueError('Fresh isolated Linux root required; previous evidence preserved')
    candidate = validate_bindings(files, policy)
    root.mkdir(parents=True, mode=0o700)
    target = root/'candidate'
    replacements = {row['path'] for row in candidate['files']}
    for relative, raw in source.items():
        if relative not in replacements: write_new(target, relative, raw)
    # Git metadata is trusted only from the verified clean detached checkout,
    # never from archive entries (whose canonical path guard forbids .git).
    for relative, raw in metadata.items():
        if not relative.startswith('.git/') or '..' in Path(relative).parts:
            raise ValueError('Foreign Git metadata coordinate')
        path=target/relative;reject_links(path);path.parent.mkdir(parents=True,exist_ok=True)
        with path.open('xb') as stream:stream.write(raw)
        if digest(path.read_bytes()) != digest(raw):raise ValueError('Original Git metadata copy differs')
    for name in ('objects','refs'):
        path = target/'.git'/name; reject_links(path); path.mkdir(parents=True,exist_ok=True)
    for row in candidate['files']:write_new(target,row['path'],files['candidate/raw/'+row['path']])
    for relative,raw in files.items():
        if relative.startswith('dependencies/'):write_new(root/'dependencies',relative.removeprefix('dependencies/'),raw)
        elif relative.startswith('candidate/'):write_new(root/'source',relative.removeprefix('candidate/'),raw)
        elif relative.startswith('supervisor/'):write_new(root/'supervisor',relative.removeprefix('supervisor/'),raw)
    write_new(root,'dependency-manifest.json',files['dependency-manifest.json'])
    write_new(root,'transport-index.json',files['transport-index.json'])
    return target


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--policy',required=True);parser.add_argument('--base-checkout',required=True)
    parser.add_argument('--capsule-blob',required=True);parser.add_argument('--receipt',required=True)
    args=parser.parse_args();policy=load_policy(args.policy)
    source,metadata=read_base(args.base_checkout,policy)
    files=validate_zip(fetch_git_blob(REPOSITORY,args.capsule_blob),policy['bundleSha256'],policy['bundleBytes'],policy['entries'])
    target=materialize(ROOT,source,metadata,files,policy)
    receipt={'state':'LinuxSourceMaterialized','root':ROOT,'candidate':str(target),'candidateManifestSha256':policy['candidateManifestSha256'],
        'supervisorManifestSha256':policy['supervisorManifestSha256'],'dependencyManifestSha256':policy['dependencyManifestSha256'],
        'sourceEntries':len(files),'candidateFiles':36,'baselineFiles':179,'dependencyFiles':130,'sdkStarted':False,'nativeTestsExecuted':False}
    path=Path(args.receipt);reject_links(path);path.parent.mkdir(parents=True,exist_ok=True)
    with path.open('xb') as stream:stream.write((json.dumps(receipt,indent=2)+'\n').encode())
    print('Exact Linux File source materialized; no SDK or native test executed')


if __name__=='__main__':main()
