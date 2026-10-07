"""Fixed File supervisor successor smoke/build transport; Root supplies all SDK grants."""
import argparse
from datetime import datetime, timezone
import importlib.util
import os
from pathlib import Path
import re
import subprocess
import sys
import unittest
from materialize_file_candidate import BASE, FIXED_ROOT, OWNER, COORDINATOR, load_policy as load_intake_policy
from sealed_source_capsule import digest, fetch_git_blob, parse_json, reject_links, write_new
from smoke_file_windows_job import run_smoke, EXPECTED_LIMITS

POLICY_SHA256 = 'ad3495063723fef3524a5ad210d5f0634b4634a287c7b1c47d56984a3329c27b'
REPOSITORY = 'MALIEV-Co-Ltd/Legacy.Maliev.FileService'
MAX_GRANT_BYTES = 16384


def verify_git_provenance(evidence):
    candidate = Path(FIXED_ROOT)/'work/file-literal-upload-mutation-v9'
    metadata = candidate/'.git'; reject_links(metadata)
    receipt = {'schemaVersion': 1, 'expectedHead': BASE, 'sdkStarted': False,
               'metadataDirectoryPresent': metadata.is_dir(), 'category': 'metadata-missing'}
    if metadata.is_dir():
        head = metadata/'HEAD'; reject_links(head)
        if head.is_file() and head.stat().st_size <= 256:
            raw = head.read_bytes(); receipt['metadataHeadSha256'] = digest(raw)
            receipt['metadataHeadMatches'] = raw.decode('ascii', errors='replace').strip() == BASE
        else: receipt['metadataHeadMatches'] = False
        receipt['category'] = 'metadata-head-mismatch'
        if receipt['metadataHeadMatches']:
            # Plain Git, exactly as the frozen controller uses it. No ownership override.
            try:
                result = subprocess.run(['git', '-C', str(candidate), 'rev-parse', 'HEAD'],
                                        capture_output=True, timeout=5, check=False)
                receipt['exitCode'] = result.returncode
                if result.returncode == 0 and result.stdout.strip() == BASE.encode('ascii'):
                    receipt['category'] = 'verified'; receipt['actualHead'] = BASE
                elif result.returncode == 0: receipt['category'] = 'resolved-head-mismatch'
                elif b'detected dubious ownership' in result.stderr[:16384]: receipt['category'] = 'dubious-ownership'
                elif b'not a git repository' in result.stderr[:16384]: receipt['category'] = 'not-repository'
                else: receipt['category'] = 'git-refusal'
            except subprocess.TimeoutExpired: receipt['category'] = 'git-timeout'
            except OSError: receipt['category'] = 'git-unavailable'
    # Only fixed categories and identity facts; never publish arbitrary stderr or URLs.
    import json
    write_new(evidence, 'git-provenance.json', json.dumps(receipt, indent=2).encode('utf-8'))
    if receipt['category'] != 'verified':
        raise RuntimeError('Candidate Git provenance refused: '+receipt['category'])
    return receipt


def load_policy(path):
    reject_links(path)
    if Path(path).stat().st_size > 65536: raise ValueError('SDK policy exceeds bound')
    raw = Path(path).read_bytes()
    if digest(raw) != POLICY_SHA256: raise ValueError('Exact reviewed SDK policy bytes required')
    policy = parse_json(raw)
    if policy['repository'] != REPOSITORY or policy['owner'] != OWNER or policy['coordinator'] != COORDINATOR or policy['root'] != FIXED_ROOT:
        raise ValueError('SDK owner/repository/path differs')
    if len(policy['supervisorFiles']) != 5: raise ValueError('Supervisor graph differs')
    return policy


def validate_grant(raw, expected_sha256, policy, now=None):
    if len(raw) > MAX_GRANT_BYTES or not re.fullmatch('[0-9a-f]{64}', expected_sha256) or digest(raw) != expected_sha256:
        raise ValueError('Exact bounded Root grant bytes required')
    grant = parse_json(raw)
    for key, expected in [('owner', OWNER), ('issuedBy', COORDINATOR), ('phase', 'build'),
                          ('candidateManifestSha256', policy['candidateManifestSha256']),
                          ('supervisorManifestSha256', policy['supervisorManifestSha256']),
                          ('dependencyManifestSha256', policy['dependencyManifestSha256'])]:
        if grant.get(key) != expected: raise ValueError('Root grant identity/phase/hash binding differs')
    if not isinstance(grant.get('leaseId'), str) or not grant['leaseId'] or len(grant['leaseId']) > 128:
        raise ValueError('Root grant lease identity differs')
    issued = datetime.fromisoformat(grant['issuedUtc']); expiry = datetime.fromisoformat(grant['expiresUtc'])
    now = now or datetime.now(timezone.utc)
    if issued.tzinfo is None or expiry.tzinfo is None or not issued <= now < expiry or not 0 < (expiry-issued).total_seconds() <= 900:
        raise ValueError('Root grant expired/not live/too long')
    return grant


def verified_blob(row):
    raw = fetch_git_blob(REPOSITORY, row['gitBlob'])
    if len(raw) != row['bytes'] or digest(raw) != row['sha256']:
        raise ValueError('Reviewed supervisor blob differs')
    return raw


def install_supervisor(policy):
    root = Path(FIXED_ROOT)/'outputs'; reject_links(root)
    manifest_raw = verified_blob(policy['supervisorManifest'])
    if digest(manifest_raw) != policy['supervisorManifestSha256']: raise ValueError('Supervisor manifest differs')
    manifest = parse_json(manifest_raw)
    if manifest['candidateManifestSha256'] != policy['candidateManifestSha256'] or manifest['dependencyManifestSha256'] != policy['dependencyManifestSha256']:
        raise ValueError('Supervisor nested source binding differs')
    expected = {row['sealedCopy']: row for row in manifest['files']}
    if len(expected) != 5 or set(expected) != {row['path'] for row in policy['supervisorFiles']}:
        raise ValueError('Supervisor file inventory differs')
    # Fetch and verify the entire bounded graph before writing any successor file.
    files = {}
    for row in policy['supervisorFiles']:
        frozen = expected[row['path']]
        if frozen['sha256'] != row['sha256'] or str(frozen['path']).replace('\\','/') != FIXED_ROOT+'/outputs/'+row['path']:
            raise ValueError('Supervisor absolute path/raw seal differs')
        files[row['path']] = verified_blob(row)
    for name, raw in files.items():
        target = root/name; reject_links(target)
        if target.exists() and target.read_bytes() != raw: raise ValueError('Existing helper differs; preserved')
    for name, raw in files.items():
        if not (root/name).exists(): write_new(root, name, raw)
    write_new(root/'file-build-supervisor-source-v6', 'manifest.json', manifest_raw)
    sys.path.insert(0, str(root))
    spec = importlib.util.spec_from_file_location('file_sdk_supervisor_v4', root/'file_build_supervisor_draft_v4.py')
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    module.REPO = Path(FIXED_ROOT)/'work/file-literal-upload-mutation-v9'
    return module


def source_controls(policy):
    suite = unittest.TestSuite()
    for index,row in enumerate(policy['supervisorFiles']):
        if not row['path'].startswith('test_'): continue
        path=Path(FIXED_ROOT)/'outputs'/row['path'];reject_links(path)
        if digest(path.read_bytes()) != row['sha256']:raise ValueError('Frozen control raw seal differs')
        spec=importlib.util.spec_from_file_location('file_frozen_control_'+str(index),path)
        module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        suite.addTests(unittest.defaultTestLoader.loadTestsFromModule(module))
    result = unittest.TextTestRunner(verbosity=1).run(suite)
    if not result.wasSuccessful() or result.testsRun != policy['sourceControlsExpected'] or result.skipped:
        raise RuntimeError('Frozen successor source controls failed or incomplete')


def validate_smoke(receipt, policy):
    if receipt['state'] != 'WindowsOwnedPythonSmokePassed' or receipt['supervisorSha256'] != policy['supervisorManifestSha256'] or receipt['sourceSha256'] != policy['candidateManifestSha256']:
        raise ValueError('Current successor Windows smoke differs')
    if receipt['casesPassed'] != 4 or receipt['helpersRemaining'] or receipt['nativeSdkSpawned'] is not False or receipt['rootSdkPermitMinted'] is not False:
        raise ValueError('Current Windows smoke custody differs')
    if {row['case'] for row in receipt['cases']} != {'success','timeout','partial-start','pre-phase-final-close'}:
        raise ValueError('Current Windows smoke cases differ')
    for row in receipt['cases']:
        if not row['smokePassed'] or not row['verifiedNoOwnedProcesses'] or not row['verifiedHandlesReleased'] or row['unreleasedExactHandles'] != 0 or row['nativeSdkSpawned'] is not False:
            raise ValueError('Current Windows owned resource proof incomplete')
        if row['case'] != 'pre-phase-final-close':
            if row['admissionFreeMiB'] < 4096 or row['admissionSdkJobs'] or row['actualJobLimits'] != EXPECTED_LIMITS:
                raise ValueError('Actual Windows job/admission proof differs')


def execute_build(module, policy, grant_path):
    root = Path(FIXED_ROOT); previous = sys.argv
    sys.argv = [str(root/'outputs/file_build_supervisor_draft_v4.py'),
                '--manifest', str(root/'outputs/file-literal-upload-mutation-source-v9/manifest.json'),
                '--manifest-sha256', policy['candidateManifestSha256'],
                '--dependency-root', str(root/'work/file-prefix-native-pinned-dependencies-20261008'),
                '--dependency-manifest', str(root/'outputs/file-prefix-native-pinned-dependencies-20261008.json'),
                '--sole-lane', str(grant_path),
                '--supervisor-manifest', str(root/'outputs/file-build-supervisor-source-v6/manifest.json'),
                '--supervisor-manifest-sha256', policy['supervisorManifestSha256']]
    try: module.main()
    finally: sys.argv = previous


def retain_build_evidence(evidence):
    root = Path(FIXED_ROOT)/'outputs'
    for owned in root.glob('file-build-owned-*'):
        reject_links(owned)
        for name in ('resource-ledger.json','restore.log','build.log'):
            source = owned/name; reject_links(source)
            if not source.exists(): continue
            if source.stat().st_size > 16*1024*1024: raise ValueError('Build evidence exceeds frozen cap')
            write_new(evidence/'build', owned.name+'/'+name, source.read_bytes())


def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--policy',required=True)
    parser.add_argument('--intake-policy',required=True); parser.add_argument('--evidence',required=True)
    parser.add_argument('--mode',choices=('smoke-only','restore-build'),default='smoke-only')
    parser.add_argument('--grant-blob'); parser.add_argument('--grant-sha256'); args=parser.parse_args()
    if os.name != 'nt': raise RuntimeError('Actual Windows host required')
    policy=load_policy(args.policy); load_intake_policy(args.intake_policy)
    if args.mode == 'restore-build' and not (args.grant_blob and args.grant_sha256): raise ValueError('Root grant transport required')
    if args.mode == 'smoke-only' and (args.grant_blob or args.grant_sha256): raise ValueError('Smoke does not consume an SDK grant')
    evidence=Path(args.evidence); reject_links(evidence); evidence.mkdir(parents=True,exist_ok=True)
    verify_git_provenance(evidence)
    module=install_supervisor(policy); source_controls(policy)
    smoke=run_smoke(module,evidence/'successor-windows-smoke',policy); validate_smoke(smoke,policy)
    if args.mode == 'smoke-only':
        print('Successor actual Windows Python smoke passed; SDK phase not started.'); return
    raw=fetch_git_blob(REPOSITORY,args.grant_blob,MAX_GRANT_BYTES)
    validate_grant(raw,args.grant_sha256,policy)
    grant_path=write_new(Path(FIXED_ROOT)/'outputs','file-sdk-root-grant.json',raw)
    # Recheck the immutable finite grant immediately before unchanged SDK commands.
    validate_grant(grant_path.read_bytes(),args.grant_sha256,policy)
    os.environ.pop('GH_TOKEN',None)  # Read-only GitHub fetch credential never reaches SDK children.
    failure=None
    try:execute_build(module,policy,grant_path)
    except BaseException as error:failure=error
    try:retain_build_evidence(evidence)
    except BaseException as error:
        if failure is None:failure=error
        else:
            try:sys.stderr.write('Build evidence retention failed after an earlier stage failure.\n')
            except BaseException:pass
    if failure is not None:raise failure
    print('Frozen restore/build stage passed; no File tests or deployment ran.')

if __name__=='__main__': main()
