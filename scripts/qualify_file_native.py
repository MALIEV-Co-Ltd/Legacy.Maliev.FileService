"""Protected exact-source File Linux qualifier; original Root grants only."""
import argparse
import base64
from datetime import datetime, timezone
import importlib.util
import json
import os
from pathlib import Path
import py_compile
import shutil
import select
import time
import subprocess
import sys
import tempfile

from materialize_file_native import BASE, REPOSITORY, ROOT, load_policy
from sealed_source_capsule import digest, fetch_git_blob, parse_json, reject_links, write_new

PHASES = ('build','focused','full','format','audit')
MAX_BUNDLE_BYTES = 32768


def sealed_read(path, expected_bytes):
    path=Path(path);reject_links(path)
    if not path.is_file() or path.stat().st_size!=expected_bytes:
        raise ValueError('Original bounded source file size differs')
    with path.open('rb') as stream:raw=stream.read(expected_bytes+1)
    if len(raw)!=expected_bytes:raise ValueError('Original source changed while read')
    return raw


def sealed_sources(policy):
    root=Path(ROOT);reject_links(root)
    sizes={row['path']:row['bytes'] for row in policy['entries']}
    candidate_raw=sealed_read(root/'source/manifest.json',sizes['candidate/manifest.json'])
    candidate=parse_json(candidate_raw)
    if digest(candidate_raw) != policy['candidateManifestSha256']:
        raise ValueError('Original candidate manifest differs')
    for row in candidate['files']:
        path=root/'candidate'/row['path'];reject_links(path)
        if path.stat().st_size != row['bytes'] or digest(sealed_read(path,row['bytes'])) != row['preparedSha256']:
            raise ValueError('Actual materialized source bytes differ')
    supervisor_raw=sealed_read(root/'supervisor/manifest.json',sizes['supervisor/manifest.json'])
    supervisor=parse_json(supervisor_raw)
    if digest(supervisor_raw) != policy['supervisorManifestSha256'] or supervisor['files'] != policy['supervisorFiles']:
        raise ValueError('Original Linux supervisor manifest differs')
    for row in policy['supervisorFiles']:
        sealed=root/'supervisor'/row['sealedCopy'];actual=root/'candidate'/row['path'];reject_links(sealed)
        if digest(sealed_read(sealed,row['bytes'])) != row['sha256'] or sealed_read(sealed,row['bytes']) != sealed_read(actual,row['bytes']):
            raise ValueError('Actual runtime helper is not independently sealed source')
    raw=sealed_read(root/'dependency-manifest.json',sizes['dependency-manifest.json'])
    if digest(raw)!=policy['dependencyManifestSha256']:raise ValueError('Original dependencies differ')
    for row in parse_json(raw)['files']:
        path=root/'dependencies'/row['repository']/row['path'];reject_links(path)
        if digest(sealed_read(path,sizes['dependencies/'+row['repository']+'/'+row['path']]))!=row['sha256']:raise ValueError('Pinned dependency bytes differ')
    return root/'candidate'


def validate_grant_bundle(raw, expected_sha, policy, validator, now):
    if len(raw)>MAX_BUNDLE_BYTES or digest(raw)!=expected_sha:
        raise ValueError('Exact bounded original Root grant bundle required')
    bundle=parse_json(raw)
    if not isinstance(bundle,dict) or set(bundle)!= {'schemaVersion','grants'} or type(bundle['schemaVersion']) is not int or bundle['schemaVersion']!=1:
        raise ValueError('Closed original Root bundle schema required')
    if not isinstance(bundle['grants'],list) or len(bundle['grants'])!=5:
        raise ValueError('Exactly five distinct original Root phase grants required')
    bindings={key:policy[key] for key in ('candidateManifestSha256','supervisorManifestSha256','dependencyManifestSha256')}
    result={};leases=set()
    for phase,row in zip(PHASES,bundle['grants']):
        if not isinstance(row,dict) or set(row)!= {'phase','rawBase64','rawSha256'} or row['phase']!=phase or not isinstance(row['rawBase64'],str) or len(row['rawBase64'])>5464:
            raise ValueError('Closed fixed ordered Root phase rows required')
        original=base64.b64decode(row['rawBase64'],validate=True)
        if base64.b64encode(original).decode()!=row['rawBase64']:
            raise ValueError('Canonical original Root bytes required')
        authority=validator(original,row['rawSha256'],phase,bindings,now)
        if authority.lease_id in leases:raise ValueError('Original Root lease cannot authorize multiple phases')
        leases.add(authority.lease_id);result[phase]=(original,row['rawSha256'])
    return result


def retain_evidence(candidate,evidence):
    source=candidate/'runner-results'
    if not source.exists():return
    rows=[];total=0
    for path in sorted(source.rglob('*')):
        reject_links(path)
        if not path.is_file():continue
        relative=path.relative_to(source).as_posix()
        # Retain owned ledgers, SDK output and actual test/coverage artifacts only.
        # Private NuGet home/packages are not portable qualification evidence.
        if '/home/' in '/'+relative:continue
        size=path.stat().st_size;total+=size
        if size>64*1024*1024 or total>256*1024*1024 or len(rows)>=2048:
            raise ValueError('Native evidence quota exceeded; original retained')
        raw=path.read_bytes();write_new(evidence/'native',relative,raw)
        rows.append({'path':relative,'bytes':len(raw),'sha256':digest(raw)})
    write_new(evidence,'native-evidence-index.json',(json.dumps(rows,indent=2)+'\n').encode())


def run_control(command, evidence, name):
    child=None; failure=None; readers=set(); eof=set(); count=0; output=bytearray()
    grace=None; cleanup_errors=0; deadline=time.monotonic()+30
    limit=2*1024*1024
    try:
        child=subprocess.Popen(command,stdout=subprocess.PIPE,stderr=subprocess.PIPE,
            env={key:value for key,value in os.environ.items() if key in {'PATH','LANG','LC_ALL','HOME','TMPDIR'}})
        birth=None; executable=None
        try:
            text=Path('/proc/'+str(child.pid)+'/stat').read_text()
            birth=text[text.rfind(')')+2:].split()[19]
            executable=os.readlink('/proc/'+str(child.pid)+'/exe')
        except FileNotFoundError:
            if child.poll() is None:raise RuntimeError('Pure child metadata unavailable while live')
        write_new(evidence,name+'-running.json',(json.dumps({'pid':child.pid,'actualStartTicks':birth,
            'executable':executable,'purpose':'fixed frozen pure controls','timeoutSeconds':30,'SDKStarted':False})+'\n').encode())
        for stream in (child.stdout,child.stderr):
            os.set_blocking(stream.fileno(),False);readers.add(stream.fileno())
        while readers or child.poll() is None:
            if time.monotonic()>=deadline:raise TimeoutError('Finite pure control deadline')
            ready,_,_=select.select(list(readers),[],[],.1)
            for descriptor in ready:
                block=os.read(descriptor,min(65536,limit-count+1))
                if not block:readers.remove(descriptor);eof.add(descriptor);continue
                if count+len(block)>limit:raise ValueError('Pure control output exceeds active cap')
                output.extend(block);count+=len(block)
            if not readers:time.sleep(.01)
        write_new(evidence,name+'.log',bytes(output))
        if child.returncode:raise RuntimeError('Frozen pure source controls failed')
    except BaseException as error:failure=error
    finally:
        # Retain the SAME unreaped Popen and its pipe wrappers until verified exit,
        # EOF and closure. Cleanup interruptions never abandon this custody.
        # Original five-second grace is never renewed; outer hosted lease is65min.
        while child is not None:
            try:
                if child.poll() is None:
                    if grace is None:grace=time.monotonic()+5
                    if time.monotonic()<grace:child.terminate()
                    else:child.kill()
                for stream in (child.stdout,child.stderr):
                    if stream is not None and not stream.closed:
                        descriptor=stream.fileno()
                        os.set_blocking(descriptor,False)
                        if descriptor not in eof:readers.add(descriptor)
                # Failure cleanup discards bounded chunks; never accumulates output.
                ready,_,_=select.select(list(readers),[],[],.1)
                for descriptor in ready:
                    if not os.read(descriptor,65536):readers.remove(descriptor);eof.add(descriptor)
                if child.poll() is None or readers:
                    time.sleep(.05);continue
                for stream in (child.stdout,child.stderr):
                    if stream is not None and not stream.closed:stream.close()
                if any(stream is not None and not stream.closed for stream in (child.stdout,child.stderr)):
                    raise RuntimeError('Exact pure control streams remain open')
                write_new(evidence,name+'-resource.json',(json.dumps({'pid':child.pid,
                    'purpose':'fixed frozen pure controls','exitCode':child.returncode,
                    'exited':True,'readersSettled':True,'handlesReleased':True,'SDKStarted':False})+'\n').encode())
                break
            except BaseException as error:
                if failure is None:failure=error
                cleanup_errors+=1
                try:
                    if cleanup_errors<=16:
                        write_new(evidence,name+'-quarantine-'+str(cleanup_errors)+'.json',
                            (json.dumps({'pid':child.pid,'attempt':cleanup_errors,
                                'failureType':type(error).__name__,'released':False,'custody':'same-Popen'})+'\n').encode())
                except BaseException:pass
                try:time.sleep(.05)
                except BaseException as error:
                    if failure is None:failure=error
    if failure is not None:raise failure


def observe_vstest_provider(owner, sdk, evidence, policy):
    if sdk is None:
        raise RuntimeError('Exact provider host unavailable; no installation or SDK invocation')
    executable=Path(sdk).resolve(strict=True)
    try:
        provider=owner.bind_vstest_provider(executable)
        if owner.bind_vstest_provider(executable)!=provider:
            raise RuntimeError('Provider changed during read-only preflight')
    except BaseException as failure:
        try:
            witness=owner.project_vstest_trust_failure(failure)
            diagnostic={'schemaVersion':1,'SDKStarted':False,'nativeTestsExecuted':False,
                'candidateManifestSha256':policy['candidateManifestSha256'],
                'supervisorManifestSha256':policy['supervisorManifestSha256'],
                'dependencyManifestSha256':policy['dependencyManifestSha256'],'witness':witness}
            raw=(json.dumps(diagnostic,sort_keys=True)+'\n').encode()
            if len(raw)>4096:raise ValueError('Provider trust diagnostic exceeds quota')
            write_new(evidence,'provider-trust-failure.json',raw)
        except BaseException:
            pass  # Optional observation cannot replace the original trust refusal.
        raise
    receipt={'schemaVersion':1,'SDKStarted':False,'nativeTestsExecuted':False,
        'provider':provider,'candidateManifestSha256':policy['candidateManifestSha256'],
        'supervisorManifestSha256':policy['supervisorManifestSha256'],
        'dependencyManifestSha256':policy['dependencyManifestSha256']}
    write_new(evidence,'provider-preflight.json',(json.dumps(receipt,sort_keys=True)+'\n').encode())
    return receipt


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--policy',required=True);parser.add_argument('--evidence',required=True)
    parser.add_argument('--mode',choices=('preflight-only','qualification'),default='preflight-only')
    parser.add_argument('--grant-bundle-blob');parser.add_argument('--grant-bundle-sha256');args=parser.parse_args()
    if sys.platform!='linux' or os.geteuid()!=0:raise RuntimeError('Reviewed root-owned Linux cgroup boundary required')
    if args.mode=='qualification' and not(args.grant_bundle_blob and args.grant_bundle_sha256):raise ValueError('Original Root phase bundle required')
    if args.mode=='preflight-only' and(args.grant_bundle_blob or args.grant_bundle_sha256):raise ValueError('Read-only preflight never consumes authority')
    if args.mode=='qualification':
        raise ValueError('Qualification disabled until Root-reviewed provider hashes are sealed')
    policy=load_policy(args.policy);candidate=sealed_sources(policy);evidence=Path(args.evidence).resolve();reject_links(evidence);evidence.mkdir(parents=True,exist_ok=True)
    sys.path.insert(0,str(candidate/'scripts'))
    import file_native_admission as authority_source
    from file_root_phase_grant import validate_grant
    head=authority_source.git_bytes(candidate,['rev-parse','HEAD']).decode().strip()
    if head!=BASE:raise ValueError('Plain Git candidate identity differs')
    with tempfile.TemporaryDirectory(prefix='file-native-compile-') as temporary:
        for source in (candidate/'scripts').glob('*.py'):py_compile.compile(str(source),cfile=str(Path(temporary)/(source.name+'.pyc')),doraise=True)
    # Actual Linux pure controls are finite; no SDK/container mutation occurs.
    for name in ('test-file-owned-qualification.py','test_file_native_admission.py','test_file_root_phase_grant.py'):
        run_control([sys.executable,'-B',str(candidate/'scripts'/name)],evidence,name)
    spec=importlib.util.spec_from_file_location('file_linux_owner',candidate/'scripts/run-file-owned-qualification.py')
    owner=importlib.util.module_from_spec(spec);spec.loader.exec_module(owner)
    owner.RPC_DEADLINE=owner.time.monotonic()+10
    status,daemon=owner.docker('GET','/info')
    if status!=200 or daemon.get('OSType')!='linux' or not daemon.get('ID'):raise RuntimeError('Existing local Linux Docker daemon unavailable; no setup/restart authorized')
    memory=dict(line.split(':',1) for line in Path('/proc/meminfo').read_text().splitlines())
    if int(memory['MemAvailable'].split()[0])<4096*1024 or not Path('/sys/fs/cgroup/cgroup.controllers').is_file():raise RuntimeError('Linux memory/cgroup admission unavailable')
    sdk=shutil.which('dotnet')
    observe_vstest_provider(owner,sdk,evidence,policy)
    write_new(evidence,'preflight.json',(json.dumps({'state':'LinuxReadOnlyPreflightPassed','plainGitHead':head,
        'candidateManifestSha256':policy['candidateManifestSha256'],'supervisorManifestSha256':policy['supervisorManifestSha256'],
        'daemonId':daemon['ID'],'memoryFloorMiB':4096,'SDKStarted':False,'nativeTestsExecuted':False})+'\n').encode())
    if args.mode=='preflight-only':print('Read-only Linux preflight and frozen pure controls passed; no SDK ran');return
    bundle=fetch_git_blob(REPOSITORY,args.grant_bundle_blob,MAX_BUNDLE_BYTES)
    grants=validate_grant_bundle(bundle,args.grant_bundle_sha256,policy,validate_grant,datetime.now(timezone.utc))
    bindings={key:policy[key] for key in ('candidateManifestSha256','supervisorManifestSha256','dependencyManifestSha256')}
    raw=(json.dumps(bindings,sort_keys=True)+'\n').encode();bindingpath=write_new(Path(ROOT),'root-source-bindings.json',raw)
    os.environ.update(MALIEV_FILE_ADMISSION_MODE='root-candidate',MalievWorkspaceRoot=str(Path(ROOT)/'dependencies'),
        MALIEV_FILE_BINDINGS_PATH=str(bindingpath),MALIEV_FILE_BINDINGS_SHA256=digest(raw))
    sdk=shutil.which('dotnet')
    if sdk is None:raise RuntimeError('Existing reviewed SDK executable unavailable; no installation attempted')
    # Read-only fetch credential and caller configuration never reach SDK children.
    os.environ.pop('GH_TOKEN',None)
    original_directory=Path.cwd();original_argv=sys.argv;failure=None
    try:
        os.chdir(candidate)
        for phase in PHASES:
            original,seal=grants[phase]
            validate_grant(original,seal,phase,bindings,datetime.now(timezone.utc))
            path=write_new(Path(ROOT),'root-phase-'+phase+'.json',original)
            os.environ.update(MALIEV_FILE_ROOT_GRANT_PATH=str(path),MALIEV_FILE_ROOT_GRANT_SHA256=seal)
            sys.argv=['run-file-owned-qualification.py',phase,sdk]
            owner.main()
    except BaseException as error:failure=error
    finally:
        sys.argv=original_argv;os.chdir(original_directory)
        try:retain_evidence(candidate,evidence)
        except BaseException as error:
            if failure is None:failure=error
    if failure is not None:raise failure
    print('Five original Root-bound phases passed with exact resource release; no deployment occurred')


if __name__=='__main__':main()
