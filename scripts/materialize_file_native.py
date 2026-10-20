"""Exact V35 Linux source intake. No SDK, Docker mutation or Root authority minting."""
import argparse
import json
from pathlib import Path

from materialize_file_candidate import read_base
from sealed_source_capsule import canonical_path, digest, fetch_git_blob, parse_json, reject_links, validate_zip, write_new

REPOSITORY = 'MALIEV-Co-Ltd/Legacy.Maliev.FileService'
BASE = '07f0b5b721e3df758a9ec742c6f167491f899e44'
ROOT = '/tmp/maliev-file-native-v35-20261009'
POLICY_SHA256 = '01b8b4547f48c1939b26fa0c4e51e49a8e658f851c7b37703050186fc697991c'


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



# Current File business-candidate source binding only. No SDK/authority dispatch.
CURRENT_BASE = 'ce183871469381eca13b359d997ad68042c4aa46'
CURRENT_POLICY_SHA256 = '2eebb1c82619d89ff073ecdf345c43a0f58cf6091899a331fb5dee82f5625643'
CURRENT_PRODUCER_COMMIT = 'e5732037fe94b7ed6e5be2cd4c23dbf6ef5e2617'
CURRENT_CONSUMER_PATHS = {'.github/workflows/_build-and-test.yml',
    'Legacy.Maliev.FileService.Tests/Workflows/WorkflowContractTests.cs'}
CURRENT_PATHS = {
    'Legacy.Maliev.FileService.Application/Services/FileApplicationService.cs',
    'Legacy.Maliev.FileService.Tests/Application/FileApplicationServiceTests.cs',
    'Legacy.Maliev.FileService.Tests/Integration/LegacyFileConsumerWriteHttpBoundaryTests.cs',
    'Legacy.Maliev.FileService.Tests/Integration/LegacyFileMoveHttpBoundaryTests.cs',
}


def load_current_policy(raw):
    if not isinstance(raw, bytes) or len(raw) > 256*1024 or digest(raw) != CURRENT_POLICY_SHA256:
        raise ValueError('Exact sealed current File source policy required')
    policy = parse_json(raw)
    if policy['acceptedBase'] != CURRENT_BASE or policy['repository'] != REPOSITORY:
        raise ValueError('Current File repository/base differs')
    if policy['candidateRoot'] != 'file-literal-delete-move-current-integration-v1/files':
        raise ValueError('Current File candidate path differs')
    for name in ('executionEnabled',):
        if policy[name] is not False:
            raise ValueError('Source intake cannot enable execution')
    for name in ('signingIdentity','rootAllocation','grantBundle','qualifiedSharedSecurityProducer'):
        if policy[name] is not None:
            raise ValueError('Source intake cannot carry execution authority')
    _current_inventory(policy['baseFiles'])
    _current_inventory(policy['dependencyFiles'])
    fixture = _current_inventory(policy['fixtureFiles'])
    if len(fixture) != 11 or set(fixture) & CURRENT_PATHS:
        raise ValueError('Exact separate eleven-file fixture overlay required')
    consumers = _current_inventory(policy['producerConsumerFiles'])
    if set(consumers) != CURRENT_CONSUMER_PATHS or set(consumers) & (set(fixture) | CURRENT_PATHS):
        raise ValueError('Exact separate current producer consumer postimages required')
    producer = policy['publishedProducerObservation']
    rows = [row for row in policy['dependencyFiles'] if row['path'].startswith('Legacy.Maliev.Workflows/')]
    if len(policy['dependencyFiles']) != 140 or len(rows) != 10 or rows != producer['files']:
        raise ValueError('Closed140 dependency map with ten actual producer files required')
    if producer['revision'] != CURRENT_PRODUCER_COMMIT or any(row['commit'] != CURRENT_PRODUCER_COMMIT for row in rows):
        raise ValueError('Exact published producer revision required')
    if producer['ci']['head_sha'] != CURRENT_PRODUCER_COMMIT or producer['ci']['status'] != 'completed' or producer['ci']['conclusion'] != 'success':
        raise ValueError('Exact published producer CI observation required')
    if producer['consumerAcceptance'] is not False or producer['nativeAcceptance'] is not False:
        raise ValueError('Producer publication cannot confer consumer acceptance')
    candidate = _current_inventory(policy['candidateFiles'])
    if set(candidate) != CURRENT_PATHS:
        raise ValueError('Exactly four current postimages required')
    return policy


def _current_inventory(rows, allow_current_git=False):
    import re
    result = {}
    folded = set()
    for row in rows:
        path = _current_path(row['path'],allow_current_git)
        if path.casefold() in folded or type(row['bytes']) is not int or not 0 <= row['bytes'] <= 2*1024*1024:
            raise ValueError('Duplicate or unbounded source inventory')
        if not re.fullmatch('[0-9a-f]{64}', row['sha256']):
            raise ValueError('Source hash shape differs')
        result[path] = row
        folded.add(path.casefold())
    return result


def _current_path(path, allow_current_git=False):
    import re
    if allow_current_git and isinstance(path,str) and re.fullmatch(
            r'candidate/\.git/(?:HEAD|config|index|objects/[0-9a-f]{2}/[0-9a-f]{38})',path):
        return path
    return canonical_path(path)


def _current_bytes(actual, rows, allow_current_git=False):
    expected = _current_inventory(rows,allow_current_git)
    if not isinstance(actual, dict) or set(actual) != set(expected):
        raise ValueError('Closed current source inventory differs')
    for path, raw in actual.items():
        _current_path(path,allow_current_git)
        row = expected[path]
        if not isinstance(raw, bytes) or len(raw) != row['bytes'] or digest(raw) != row['sha256']:
            raise ValueError('Exact current source bytes differ')


def _current_baseline(archive, witness):
    import io
    import zipfile
    from xml.etree import ElementTree as ET
    if not isinstance(archive, bytes) or len(archive) != witness['archiveBytes'] or digest(archive) != witness['archiveSha256']:
        raise ValueError('Exact retained baseline archive required')
    with zipfile.ZipFile(io.BytesIO(archive)) as zipped:
        entries = zipped.infolist()
        names = [row.filename for row in entries]
        if len(names) != len(set(names)) or names.count(witness['trxPath']) != 1:
            raise ValueError('Ambiguous baseline archive')
        entry = zipped.getinfo(witness['trxPath'])
        if entry.file_size != witness['trxBytes'] or entry.file_size > 4*1024*1024:
            raise ValueError('Bounded baseline TRX required')
        raw = zipped.read(entry)
    if digest(raw) != witness['trxSha256']:
        raise ValueError('Exact retained raw TRX required')
    ns = {'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    xml = ET.fromstring(raw)
    definitions = xml.findall('t:TestDefinitions/t:UnitTest',ns)
    entries = xml.findall('t:TestEntries/t:TestEntry',ns)
    results = xml.findall('t:Results/t:UnitTestResult',ns)
    count = witness['identityCount']
    if len(definitions) != count or len(entries) != count or len(results) != count:
        raise ValueError('Baseline roster cardinality differs')
    defs = {row.attrib['id']:row for row in definitions}
    joined = {row.attrib['testId']:row for row in entries}
    if len(defs) != count or len(joined) != count:
        raise ValueError('Duplicate baseline identity')
    identities = []; seen = set(); executions = set()
    for row in results:
        test_id = row.attrib['testId']; execution = row.attrib['executionId']
        if test_id in seen or execution in executions or row.attrib['outcome'] != 'Passed':
            raise ValueError('Duplicate or nonpassed baseline result')
        definition = defs.get(test_id); entry = joined.get(test_id)
        if definition is None or entry is None or definition.find('t:Execution',ns).attrib['id'] != execution or entry.attrib['executionId'] != execution:
            raise ValueError('Baseline identity join differs')
        method = definition.find('t:TestMethod',ns)
        identities.append([definition.attrib['storage'],method.attrib['className'],method.attrib['name'],row.attrib['testName']])
        seen.add(test_id); executions.add(execution)
    identities.sort()
    roster = (json.dumps(identities,separators=(',',':'),ensure_ascii=True)+'\n').encode()
    if digest(roster) != witness['identityRosterSha256']:
        raise ValueError('Baseline semantic identities differ')
    return count


def validate_current_bindings(raw_policy, accepted_base, base_source, candidate_source, dependency_source, baseline_archive):
    """Validate sealed byte maps; cannot execute, grant, fetch, or materialize."""
    policy = load_current_policy(raw_policy)
    if accepted_base != CURRENT_BASE:
        raise ValueError('Current accepted checkout base differs')
    _current_bytes(base_source,policy['baseFiles'])
    _current_bytes(candidate_source,policy['candidateFiles'])
    _current_bytes(dependency_source,policy['dependencyFiles'])
    identities = _current_baseline(baseline_archive,policy['baseline'])
    return {'state':'CurrentFileSourceBindingVerified','base':CURRENT_BASE,
            'baseFiles':len(base_source),'candidateFiles':4,'dependencyFiles':len(dependency_source),
            'baselineIdentities':identities,'sdkStarted':False,'executionEnabled':False,
            'nativeAcceptance':False,'grantBundle':None}


def _current_read(raw_path, maximum):
    path = Path(raw_path); reject_links(path)
    if not path.is_file() or path.stat().st_size > maximum:
        raise ValueError('Bounded ordinary current source file required')
    with path.open('rb') as stream:
        raw = stream.read(maximum+1)
    if len(raw) > maximum:
        raise ValueError('Current source read quota')
    return raw


def current_git_read(checkout, policy):
    """Read only authenticated public objects; never copy caller Git config."""
    import os
    import subprocess
    checkout=Path(checkout); reject_links(checkout)
    env=dict(os.environ);env.update(GIT_CONFIG_NOSYSTEM='1',GIT_CONFIG_GLOBAL=os.devnull,
                                   GIT_NO_REPLACE_OBJECTS='1',GIT_OPTIONAL_LOCKS='0')
    def git(*arguments, input=None):
        result=subprocess.run(['git','-C',str(checkout),*arguments],input=input,
                              capture_output=True,timeout=30,env=env)
        if result.returncode or len(result.stdout)>16*1024*1024 or len(result.stderr)>65536:
            raise ValueError('Bounded current Git source read failed')
        return result.stdout
    if git('rev-parse','HEAD').decode('ascii').strip()!=CURRENT_BASE or git('status','--porcelain=v1','--untracked-files=all'):
        raise ValueError('Clean exact current accepted checkout required')
    rows=[(row['gitBlob'],'blob',row['bytes'],row['sha256'],row['path']) for row in policy['baseFiles']]
    rows += [(row['oid'],row['kind'],row['bytes'],row['sha256'],row['oid']) for row in policy['gitMetadata']['objects']]
    output=git('cat-file','--batch',input=('\n'.join(row[0] for row in rows)+'\n').encode())
    source={};metadata={}
    import hashlib
    for oid,kind,size,seal,key in rows:
        header,output=output.split(b'\n',1)
        if header.decode()!=oid+' '+kind+' '+str(size) or output[size:size+1]!=b'\n':
            raise ValueError('Exact Git object framing differs')
        raw=output[:size];output=output[size+1:]
        if digest(raw)!=seal or hashlib.sha1(kind.encode()+b' '+str(size).encode()+b'\0'+raw).hexdigest()!=oid:
            raise ValueError('Authenticated current Git object differs')
        (source if kind=='blob' else metadata)[key]=raw
    if output:
        raise ValueError('Git batch trailing bytes')
    return source,metadata


def current_directory_read(root, rows):
    import os
    import stat
    root=Path(root);reject_links(root)
    expected=_current_inventory(rows);actual={}
    for directory,folders,names in os.walk(root,followlinks=False):
        for name in [*folders,*names]:reject_links(Path(directory)/name)
        for name in names:
            path=Path(directory)/name
            if not stat.S_ISREG(path.lstat().st_mode):
                raise ValueError('Nonregular current source')
            relative=path.relative_to(root).as_posix()
            if relative not in expected:
                raise ValueError('Foreign current source path')
            actual[relative]=_current_read(path,expected[relative]['bytes'])
    _current_bytes(actual,rows)
    return actual


def _current_metadata_check(metadata, policy):
    import hashlib
    rows=policy['gitMetadata']['objects']
    expected={row['oid']:row for row in rows}
    if len(expected)!=len(rows) or set(metadata)!=set(expected):
        raise ValueError('Closed Git metadata inventory differs')
    for oid,raw in metadata.items():
        row=expected[oid]
        if len(raw)!=row['bytes'] or digest(raw)!=row['sha256'] or hashlib.sha1(
                row['kind'].encode()+b' '+str(len(raw)).encode()+b'\0'+raw).hexdigest()!=oid:
            raise ValueError('Current Git metadata identity differs')


def validate_current_dependency_manifest(policy,dependencies,raw):
    import hashlib
    if not isinstance(raw,bytes) or len(raw)>128*1024 or digest(raw)!=policy['dependencyManifestSha256']:
        raise ValueError('Exact current published dependency manifest required')
    manifest=parse_json(raw)
    if manifest['sourceOnly'] is not True or manifest['publishedProducerRevision']!=CURRENT_PRODUCER_COMMIT or manifest['files']!=policy['dependencyFiles']:
        raise ValueError('Exact current dependency source association required')
    _current_bytes(dependencies,manifest['files'])
    for row in policy['publishedProducerObservation']['files']:
        data=dependencies[row['path']]
        if hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()!=row['gitBlob']:
            raise ValueError('Actual published producer Git blob differs')


def materialize_current(root, raw_policy, base_source, metadata, posts, dependencies, baseline, phase, fixtures, consumers, dependency_manifest):
    """Create only exact source and sanitized public Git metadata; no SDK."""
    import os
    import subprocess
    import zlib
    policy=load_current_policy(raw_policy)
    validate_current_bindings(raw_policy,CURRENT_BASE,base_source,posts,dependencies,baseline)
    _current_metadata_check(metadata,policy)
    _current_bytes(fixtures,policy['fixtureFiles'])
    _current_bytes(consumers,policy['producerConsumerFiles'])
    validate_current_dependency_manifest(policy,dependencies,dependency_manifest)
    if phase not in ('RED','GREEN'):
        raise ValueError('Closed current source phase required')
    root=Path(root);reject_links(root)
    if root.name!=policy['reconstruction']['rootDirectoryName']+'-'+phase.lower() or root.exists():
        raise ValueError('Fresh exact current source root required')
    root.mkdir(mode=0o700)
    created=root.lstat()
    provenance={'owner':'01a1009d-1c8e-7821-a1f2-fded3ac6928d','root':str(root.absolute()),
                'device':created.st_dev,'inode':created.st_ino,'createdNs':created.st_ctime_ns,
                'phase':phase,'policySha256':digest(raw_policy),'sdkStarted':False}
    # Durable creation evidence precedes any reconstruction. Partial failure
    # remains in the exact owned root; never broad-delete uncertain content.
    write_new(root,'ownership.json',(json.dumps(provenance,sort_keys=True)+'\n').encode())
    replacements=dict(posts)
    replacements.update(fixtures)
    replacements.update(consumers)
    if phase=='RED':
        app='Legacy.Maliev.FileService.Application/Services/FileApplicationService.cs'
        replacements[app]=base_source[app]
    candidate=root/'candidate'
    for path,raw in base_source.items():
        write_new(candidate,path,replacements.get(path,raw))
    for path,raw in replacements.items():
        if path not in base_source:write_new(candidate,path,raw)
    objects={}
    for row in policy['baseFiles']:
        objects[row['gitBlob']]=('blob',base_source[row['path']])
    for row in policy['gitMetadata']['objects']:
        objects[row['oid']]=(row['kind'],metadata[row['oid']])
    for oid,(kind,raw) in objects.items():
        write_new(candidate/'.git','objects/'+oid[:2]+'/'+oid[2:],zlib.compress(kind.encode()+b' '+str(len(raw)).encode()+b'\0'+raw))
    write_new(candidate/'.git','HEAD',(CURRENT_BASE+'\n').encode())
    write_new(candidate/'.git','config',b'[core]\n\trepositoryformatversion = 0\n\tbare = false\n\tfilemode = false\n')
    (candidate/'.git/refs').mkdir(parents=True)
    env=dict(os.environ);env.update(GIT_CONFIG_NOSYSTEM='1',GIT_CONFIG_GLOBAL=os.devnull,GIT_NO_REPLACE_OBJECTS='1',GIT_OPTIONAL_LOCKS='0')
    result=subprocess.run(['git','-C',str(candidate),'read-tree',CURRENT_BASE],
                          capture_output=True,timeout=30,env=env)
    if result.returncode or result.stdout or len(result.stderr)>65536:
        raise ValueError('Exact current source Git index reconstruction failed')
    for path,raw in dependencies.items():
        write_new(root/'dependencies',path,raw)
        if path.startswith('Legacy.Maliev.Workflows/'):
            # Existing real File identity/scanner consumers use this exact path.
            write_new(candidate,'.dependencies/'+path,raw)
    write_new(root,'dependency-manifest.json',dependency_manifest)
    write_new(root,'current-source-policy.json',raw_policy)
    write_new(root,'baseline-artifact.zip',baseline)
    receipt={'state':'CurrentFileSourceMaterialized','base':CURRENT_BASE,'phase':phase,
             'policySha256':digest(raw_policy),'baseFiles':len(base_source),'postimages':4,'fixturePostimages':11,'sourceFiles':319,
             'dependencyFiles':len(dependencies),'producerConsumerPostimages':2,'producerProjectionFiles':10,
             'publishedProducerRevision':CURRENT_PRODUCER_COMMIT,'dependencyManifestSha256':digest(dependency_manifest),
             'baselineIdentityCount':1555,'sdkStarted':False,
             'nativeAcceptance':False,'executionEnabled':False,'ownership':provenance}
    # Retain the closed physical inventory including newly made sanitized Git
    # objects/index. A later qualifier must re-observe it before any use.
    inventory=[]
    for path in sorted(root.rglob('*')):
        reject_links(path)
        if path.is_file():
            raw=_current_read(path,16*1024*1024)
            inventory.append({'path':path.relative_to(root).as_posix(),'bytes':len(raw),'sha256':digest(raw)})
    receipt['files']=inventory
    write_new(root,'materialization-receipt.json',(json.dumps(receipt,sort_keys=True)+'\n').encode())
    receipt['receiptSha256']=digest(_current_read(root/'materialization-receipt.json',512*1024))
    return receipt


def verify_current_materialization(root, raw_policy, expected_receipt_sha256):
    import os
    import stat
    root=Path(root);reject_links(root)
    policy=load_current_policy(raw_policy)
    receipt_raw=_current_read(root/'materialization-receipt.json',512*1024)
    if digest(receipt_raw)!=expected_receipt_sha256:
        raise ValueError('Independently held materialization receipt seal differs')
    receipt=parse_json(receipt_raw)
    owner=receipt['ownership'];held=root.lstat()
    if (held.st_dev,held.st_ino)!=(owner['device'],owner['inode']) or str(root.absolute())!=owner['root']:
        raise ValueError('Current reconstruction root custody differs')
    if receipt['policySha256']!=digest(raw_policy) or receipt['base']!=CURRENT_BASE or receipt['phase'] not in ('RED','GREEN'):
        raise ValueError('Current reconstructed association differs')
    expected=_current_inventory(receipt['files'],allow_current_git=True);observed={}
    for directory,folders,names in os.walk(root,followlinks=False):
        for name in [*folders,*names]:reject_links(Path(directory)/name)
        for name in names:
            path=Path(directory)/name;relative=path.relative_to(root).as_posix()
            if relative=='materialization-receipt.json':continue
            if not stat.S_ISREG(path.lstat().st_mode) or relative not in expected:
                raise ValueError('Foreign materialized path')
            observed[relative]=_current_read(path,expected[relative]['bytes'])
    _current_bytes(observed,receipt['files'],allow_current_git=True)
    if observed['current-source-policy.json']!=raw_policy:
        raise ValueError('Actual current policy bytes differ')
    all_source_paths={row['path'] for row in policy['baseFiles']} | {row['path'] for row in policy['fixtureFiles']}
    effective={path:observed['candidate/'+path] for path in all_source_paths}
    expected_sources={row['path']:row for row in policy['baseFiles']}
    for row in policy['candidateFiles']:
        if receipt['phase']=='GREEN' or not row['path'].startswith('Legacy.Maliev.FileService.Application/'):
            expected_sources[row['path']]=row
    for row in policy['fixtureFiles']:expected_sources[row['path']]=row
    for row in policy['producerConsumerFiles']:expected_sources[row['path']]=row
    _current_bytes(effective,list(expected_sources.values()))
    dependencies={row['path']:observed['dependencies/'+row['path']] for row in policy['dependencyFiles']}
    validate_current_dependency_manifest(policy,dependencies,observed['dependency-manifest.json'])
    for row in policy['publishedProducerObservation']['files']:
        if observed['candidate/.dependencies/'+row['path']]!=dependencies[row['path']]:
            raise ValueError('Exact physical current producer consumer projection differs')
    if (receipt['producerConsumerPostimages'],receipt['producerProjectionFiles'],receipt['publishedProducerRevision'],receipt['dependencyManifestSha256'])!=(2,10,CURRENT_PRODUCER_COMMIT,policy['dependencyManifestSha256']):
        raise ValueError('Current producer receipt association differs')
    _current_baseline(observed['baseline-artifact.zip'],policy['baseline'])
    if receipt['executionEnabled'] is not False or receipt['sdkStarted'] is not False or receipt['nativeAcceptance'] is not False:
        raise ValueError('Source reconstruction cannot claim native authority')
    return receipt


def cleanup_current_materialization(root, raw_policy, expected_receipt_sha256):
    """Remove only the reverified disposable source reconstruction, never data."""
    root=Path(root)
    verify_current_materialization(root,raw_policy,expected_receipt_sha256)
    # Verification and exact ordinary-file deletion are source-only ownership
    # controls. Unknown/changed content refuses before mutation.
    for path in sorted(root.rglob('*'),key=lambda value:len(value.parts),reverse=True):
        reject_links(path)
        if path.is_file():path.unlink()
        elif path.is_dir():path.rmdir()
        else:raise ValueError('Unknown source cleanup entry')
    root.rmdir()
    if root.exists():raise ValueError('Owned reconstruction absence unverified')


def current_source_main(argv):
    parser=argparse.ArgumentParser()
    parser.add_argument('--current-policy',required=True)
    parser.add_argument('--base-checkout',required=True)
    parser.add_argument('--candidate-source',required=True)
    parser.add_argument('--dependency-source',required=True)
    parser.add_argument('--dependency-manifest',required=True)
    parser.add_argument('--producer-consumer-source',required=True)
    parser.add_argument('--fixture-source',required=True)
    parser.add_argument('--baseline-archive',required=True)
    parser.add_argument('--root',required=True)
    parser.add_argument('--phase',choices=('RED','GREEN'),required=True)
    args=parser.parse_args(argv)
    raw=_current_read(args.current_policy,256*1024);policy=load_current_policy(raw)
    if '/'.join(Path(args.candidate_source).parts[-2:])!=policy['candidateRoot']:
        raise ValueError('Exact selected candidate source path required')
    base,metadata=current_git_read(args.base_checkout,policy)
    posts=current_directory_read(args.candidate_source,policy['candidateFiles'])
    if '/'.join(Path(args.dependency_source).parts[-2:])!=policy['dependencyRoot']:
        raise ValueError('Exact current published dependency path required')
    dependencies=current_directory_read(args.dependency_source,policy['dependencyFiles'])
    dependency_manifest=_current_read(args.dependency_manifest,128*1024)
    validate_current_dependency_manifest(policy,dependencies,dependency_manifest)
    if '/'.join(Path(args.producer_consumer_source).parts[-2:])!=policy['producerConsumerRoot']:
        raise ValueError('Exact current producer consumer postimage path required')
    consumers=current_directory_read(args.producer_consumer_source,policy['producerConsumerFiles'])
    if '/'.join(Path(args.fixture_source).parts[-2:])!=policy['fixtureRoot']:
        raise ValueError('Exact selected fixture overlay source path required')
    fixtures=current_directory_read(args.fixture_source,policy['fixtureFiles'])
    baseline=_current_read(args.baseline_archive,policy['baseline']['archiveBytes'])
    receipt=materialize_current(args.root,raw,base,metadata,posts,dependencies,baseline,args.phase,fixtures,consumers,dependency_manifest)
    print(json.dumps({key:value for key,value in receipt.items() if key not in ('files','ownership')},sort_keys=True))

if __name__=='__main__':
    import sys
    if '--current-policy' in sys.argv[1:]:current_source_main(sys.argv[1:])
    else:main()
