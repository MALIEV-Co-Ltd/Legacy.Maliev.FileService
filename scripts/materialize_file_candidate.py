"""File-specific sealed V8 intake. No SDK, tests, deployment, or permit generation."""
import argparse
import os
from pathlib import Path
import stat
from sealed_source_capsule import canonical_path, digest, fetch_git_blob, parse_json, reject_links, validate_zip, write_new

REPOSITORY = 'MALIEV-Co-Ltd/Legacy.Maliev.FileService'
BASE = '07f0b5b721e3df758a9ec742c6f167491f899e44'
FIXED_ROOT = 'D:/codex-temp/2026-10-03/legacy-code-files-20261003'
POLICY_SHA256 = 'ddc5244633ed6763c5699a1bca270a3aab55c634ed3d610503aeefd2a926c0fc'
OWNER = '01a1009d-1c8e-7821-a1f2-fded3ac6928d'
COORDINATOR = '019fc21e-50f0-7112-834f-9fb3b35b9dfe'
MAX_METADATA_BYTES = 16*1024*1024
MAX_METADATA_FILES = 256


def load_policy(path):
    raw = Path(path).read_bytes()
    if digest(raw) != POLICY_SHA256:
        raise ValueError('Exact reviewed File policy bytes required')
    policy = parse_json(raw)
    validate_profile(policy)
    return policy


def validate_profile(policy):
    if policy['schemaVersion'] != 1 or policy['repository'] != REPOSITORY or policy['acceptedBase'] != BASE or policy['root'] != FIXED_ROOT:
        raise ValueError('File owner/base/path profile differs')
    if policy['owner'] != OWNER or policy['coordinator'] != COORDINATOR:
        raise ValueError('File owner/coordinator differs')
    if len(policy['entries']) != 178 or len(policy['baseFiles']) != 179 or policy['executionMode'] != 'restore-build-only' or policy['nativeTestsAuthorized'] is not False:
        raise ValueError('File source inventory/mode differs')


def read_base(checkout, policy):
    checkout=Path(checkout); reject_links(checkout)
    metadata=checkout/'.git'
    reject_links(metadata)
    if not metadata.is_dir() or (metadata/'HEAD').read_text(encoding='ascii').strip() != BASE:
        raise ValueError('Exact detached File checkout required')
    expected={row['path']:row for row in policy['baseFiles']}
    source={}; git_files={}; metadata_size=0
    for directory,folders,names in os.walk(checkout,followlinks=False):
        for name in [*folders,*names]:reject_links(Path(directory)/name)
        for name in names:
            path=Path(directory)/name;relative=path.relative_to(checkout).as_posix()
            if not stat.S_ISREG(path.lstat().st_mode):raise ValueError('Nonregular checkout file')
            if relative.startswith('.git/'):
                if path.stat().st_size > MAX_METADATA_BYTES:raise ValueError('Git metadata file cap')
                raw=path.read_bytes();metadata_size+=len(raw)
                if metadata_size>MAX_METADATA_BYTES or len(git_files)>=MAX_METADATA_FILES:raise ValueError('Git metadata graph cap')
                git_files[relative]=raw
            else:
                canonical_path(relative)
                if relative not in expected:raise ValueError('Untracked/ignored checkout file')
                row=expected[relative]
                if path.stat().st_size != row['checkoutBytes']:raise ValueError('Base source size differs')
                raw=path.read_bytes()
                if digest(raw)!=row['checkoutSha256']:raise ValueError('Base raw checkout projection differs')
                source[relative]=raw
    if set(source)!=set(expected):raise ValueError('Base source missing')
    config=git_files.get('.git/config',b'').lower()
    if any(marker in config for marker in [b'extraheader',b'password',b'access_token',b'credential']):
        raise ValueError('Checkout retained credential configuration')
    return source,git_files


def validate_bindings(files,policy):
    bindings=[('candidate/manifest.json','candidateManifestSha256'),('supervisor/manifest.json','supervisorManifestSha256'),('dependency-manifest.json','dependencyManifestSha256'),('transport-index.json','transportIndexSha256')]
    for path,key in bindings:
        if digest(files[path])!=policy[key]:raise ValueError('Nested reviewed manifest differs')
    candidate=parse_json(files['candidate/manifest.json'])
    if candidate['base']!=BASE:raise ValueError('Frozen candidate base differs')
    if str(candidate['worktree']).replace('\\','/')!=FIXED_ROOT+'/work/file-literal-upload-mutation-v8':raise ValueError('Frozen candidate path differs')
    if len(candidate['files'])!=21:raise ValueError('Candidate inventory differs')
    for row in candidate['files']:
        if digest(files['candidate/raw/'+canonical_path(row['path'])])!=row['preparedSha256']:raise ValueError('Candidate raw seal differs')
    supervisor=parse_json(files['supervisor/manifest.json'])
    if len(supervisor['files'])!=5 or supervisor['candidateManifestSha256']!=policy['candidateManifestSha256']:raise ValueError('Supervisor graph differs')
    for row in supervisor['files']:
        if digest(files['supervisor/'+canonical_path(row['sealedCopy'])])!=row['sha256']:raise ValueError('Supervisor raw seal differs')
    deps=parse_json(files['dependency-manifest.json'])
    if len(deps['files'])!=130:raise ValueError('Pinned dependency graph differs')
    for row in deps['files']:
        key='dependencies/'+canonical_path(row['repository']+'/'+row['path'])
        if digest(files[key])!=row['sha256']:raise ValueError('Pinned dependency raw seal differs')
    return candidate,supervisor


def materialize(root,base_source,git_files,files,policy):
    root=Path(root);reject_links(root)
    if root.exists():raise ValueError('Fresh isolated qualification root required')
    candidate,supervisor=validate_bindings(files,policy)
    root.mkdir(parents=True)
    candidate_root=root/'work/file-literal-upload-mutation-v8'
    replacements={row['path'] for row in candidate['files']}
    for path,raw in base_source.items():
        if path not in replacements:write_new(candidate_root,path,raw)
    # Metadata comes only from the clean pinned checkout, never from capsule data.
    for path,raw in git_files.items():
        target=candidate_root/path;reject_links(target);target.parent.mkdir(parents=True,exist_ok=True)
        with target.open('xb') as stream:stream.write(raw)
        if digest(target.read_bytes())!=digest(raw):raise ValueError('Git metadata copy differs')
    for row in candidate['files']:write_new(candidate_root,row['path'],files['candidate/raw/'+row['path']])
    for path,raw in files.items():
        if path.startswith('dependencies/'):write_new(root/'work/file-prefix-native-pinned-dependencies-20261008',path[len('dependencies/'):],raw)
        elif path.startswith('candidate/'):write_new(root/'outputs/file-literal-upload-mutation-source-v8',path[len('candidate/'):],raw)
        elif path.startswith('supervisor/'):write_new(root/'outputs/file-build-supervisor-source-v2',path[len('supervisor/'):],raw)
        else:write_new(root/'outputs/file-v8-native-transport-20261008',path,raw)
    write_new(root/'outputs','file-prefix-native-pinned-dependencies-20261008.json',files['dependency-manifest.json'])
    for row in supervisor['files']:write_new(root/'outputs',row['sealedCopy'],files['supervisor/'+row['sealedCopy']])
    return candidate_root


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--policy',required=True);parser.add_argument('--base-checkout',required=True);parser.add_argument('--capsule-blob',required=True);parser.add_argument('--receipt',required=True)
    args=parser.parse_args();policy=load_policy(args.policy)
    source,metadata=read_base(args.base_checkout,policy)
    capsule=fetch_git_blob(REPOSITORY,args.capsule_blob)
    files=validate_zip(capsule,policy['bundleSha256'],policy['bundleBytes'],policy['entries'])
    candidate=materialize(FIXED_ROOT,source,metadata,files,policy)
    receipt={'schemaVersion':1,'base':BASE,'bundleSha256':policy['bundleSha256'],'candidateManifestSha256':policy['candidateManifestSha256'],'supervisorManifestSha256':policy['supervisorManifestSha256'],'root':FIXED_ROOT,'rawEntriesVerified':178,'candidateFiles':21,'pinnedDependencyFiles':130,'gitBaselineFiles':179,'sdkStarted':False,'nativeTestsExecuted':False,'state':'SourceMaterialized'}
    target=Path(args.receipt);reject_links(target);target.parent.mkdir(parents=True,exist_ok=True)
    with target.open('xb') as stream:stream.write((__import__('json').dumps(receipt,indent=2)+'\n').encode())
    print('Sealed File source reconstructed; SDK execution has not started.')

if __name__=='__main__':main()
