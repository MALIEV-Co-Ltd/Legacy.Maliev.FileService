"""Portable protected-consumer controls. No SDK, Docker or real Root grants."""
import base64
import copy
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

SCRIPTS=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(SCRIPTS))
import materialize_file_native as intake
import qualify_file_native as qualifier


class Controls(unittest.TestCase):
    def test_exact_policy_profile_is_linux_source_only_without_authority(self):
        policy=intake.load_policy(SCRIPTS/'file-native-policy.json')
        self.assertEqual(36,policy['candidateFiles'])
        self.assertEqual(204,len(policy['entries']))
        self.assertEqual(179,len(policy['baseFiles']))
        self.assertEqual('/tmp/maliev-file-native-v27-20261008',policy['root'])
        self.assertIs(False,policy['sdkAuthorizedWithoutOriginalRootGrant'])
        self.assertEqual((600,900,2147483648,64,'100000 100000',4096),
            tuple(policy[key] for key in ('sdkSeconds','rootSecondsMaximum','sdkMemoryBytes','sdkTasks','sdkCpuMax','memoryFloorMiB')))
        self.assertTrue(all(row['checkoutSha256']==row['sha256'] and row['checkoutBytes']==row['bytes'] for row in policy['baseFiles'] if not row['path'].endswith('.ps1')))
        self.assertEqual(7, sum(row['path'].endswith('.ps1') for row in policy['baseFiles']))
        with tempfile.TemporaryDirectory() as temporary:
            path=Path(temporary)/'altered.json';path.write_bytes((SCRIPTS/'file-native-policy.json').read_bytes()+b' ')
            with self.assertRaises(ValueError):intake.load_policy(path)
            path.write_bytes(b'x'*(512*1024+1))
            with self.assertRaises(ValueError):intake.load_policy(path)

    def test_frozen_windows_worktree_mapping_uses_single_separator(self):
        files, policy = self.fixture()
        candidate = json.loads(files['candidate/manifest.json'])
        candidate['worktree'] = policy['frozenCandidateWorktree'].replace('/', chr(92))
        files['candidate/manifest.json'] = json.dumps(candidate).encode()
        policy['candidateManifestSha256'] = hashlib.sha256(files['candidate/manifest.json']).hexdigest()
        supervisor = json.loads(files['supervisor/manifest.json'])
        supervisor['candidateManifestSha256'] = policy['candidateManifestSha256']
        files['supervisor/manifest.json'] = json.dumps(supervisor).encode()
        policy['supervisorManifestSha256'] = hashlib.sha256(files['supervisor/manifest.json']).hexdigest()
        self.assertEqual(candidate, intake.validate_bindings(files, policy))

    def test_checkout_reader_accepts_explicit_crlf_projection_and_rejects_raw_lf(self):
        # Synthetic detached metadata exercises read_base only, not Git authenticity.
        from materialize_file_candidate import read_base
        raw=b'Write-Output example\n'; projected=b'Write-Output example\r\n'
        policy={'baseFiles':[{'path':'sample.ps1','bytes':len(raw),
            'sha256':hashlib.sha256(raw).hexdigest(),'checkoutBytes':len(projected),
            'checkoutSha256':hashlib.sha256(projected).hexdigest()}]}
        with tempfile.TemporaryDirectory() as temporary:
            checkout=Path(temporary);(checkout/'.git/objects').mkdir(parents=True);(checkout/'.git/refs').mkdir()
            (checkout/'.git/HEAD').write_text(intake.BASE);(checkout/'.git/config').write_text('[core]\n')
            path=checkout/'sample.ps1';path.write_bytes(projected)
            self.assertEqual(projected,read_base(checkout,policy)[0]['sample.ps1'])
            path.write_bytes(raw)
            with self.assertRaisesRegex(ValueError,'Base source size differs'):read_base(checkout,policy)

    def bundle(self):
        # Deliberately NOT Root's nine-field grant shape; the validator is a pure stub.
        rows=[]
        for phase in qualifier.PHASES:
            raw=json.dumps({'syntheticTestLease':phase}).encode()
            rows.append({'phase':phase,'rawBase64':base64.b64encode(raw).decode(),'rawSha256':hashlib.sha256(raw).hexdigest()})
        return {'schemaVersion':1,'grants':rows}

    def validate(self,bundle,validator=None):
        raw=json.dumps(bundle).encode();policy=intake.load_policy(SCRIPTS/'file-native-policy.json')
        def verify(original,seal,phase,bindings,now):
            self.assertEqual(hashlib.sha256(original).hexdigest(),seal)
            self.assertEqual(set(bindings),{'candidateManifestSha256','supervisorManifestSha256','dependencyManifestSha256'})
            self.assertEqual({'syntheticTestLease':phase},json.loads(original))
            return SimpleNamespace(lease_id=phase)
        return qualifier.validate_grant_bundle(raw,hashlib.sha256(raw).hexdigest(),policy,validator or verify,datetime(2026,10,8,tzinfo=timezone.utc))

    def test_closed_phase_bundle_preserves_original_byte_rows(self):
        bundle=self.bundle();actual=self.validate(bundle)
        self.assertEqual(tuple(actual),qualifier.PHASES)
        for row in bundle['grants']:self.assertEqual(base64.b64decode(row['rawBase64']),actual[row['phase']][0])

    def test_bundle_hash_quota_schema_and_missing_phase_reject(self):
        policy=intake.load_policy(SCRIPTS/'file-native-policy.json')
        for raw,seal in ((b'{}','0'*64),(b'x'*32769,hashlib.sha256(b'x'*32769).hexdigest())):
            with self.assertRaises(ValueError):qualifier.validate_grant_bundle(raw,seal,policy,None,datetime.now(timezone.utc))
        for change in (lambda value:value.update(extra=True),lambda value:value.update(schemaVersion=True),
                       lambda value:value['grants'].pop(),lambda value:value['grants'].reverse()):
            value=self.bundle();change(value)
            with self.assertRaises(ValueError):self.validate(value)

    def test_bundle_unknown_or_changed_phase_bytes_and_duplicate_lease_reject(self):
        for key,value in (('phase','arbitrary'),('rawBase64','%%%'),('extra','ignored')):
            bundle=self.bundle();bundle['grants'][0][key]=value
            with self.assertRaises(ValueError):self.validate(bundle)
        with self.assertRaises(ValueError):self.validate(self.bundle(),lambda *_:SimpleNamespace(lease_id='same-lease'))

    def test_root_default_no_sdk_and_no_daemon_setup_are_structural(self):
        source=(SCRIPTS/'qualify_file_native.py').read_text()
        self.assertIn("default='preflight-only'",source)
        self.assertIn("os.environ.pop('GH_TOKEN',None)",source)
        self.assertIn("for phase in PHASES:",source)
        self.assertNotIn('dotnet test',source)
        workflow=(SCRIPTS.parent/'.github/workflows/file-native-qualification.yml').read_text()
        for forbidden in ('setup-dotnet','docker system prune','docker volume rm','systemctl restart','sudo apt'):
            self.assertNotIn(forbidden,workflow)
        self.assertIn('permissions:\n  contents: read',workflow)
        self.assertIn('cancel-in-progress: false',workflow)

    def control_child(self, close_interrupt=False):
        class Stream:
            def __init__(self, descriptor):self.descriptor=descriptor;self.closed=False;self.closes=0
            def fileno(self):return self.descriptor
            def close(self):
                self.closes+=1
                if close_interrupt and self.closes==1:raise KeyboardInterrupt('injected close interruption')
                self.closed=True
        class Child:
            pid=12345;returncode=None
            def __init__(self):self.stdout=Stream(10);self.stderr=Stream(11);self.terms=0;self.kills=0
            def poll(self):return self.returncode
            def terminate(self):
                self.terms+=1
                if self.terms==1:raise KeyboardInterrupt('injected TERM interruption')
            def kill(self):self.kills+=1;self.returncode=0
            def communicate(self,*args,**kwargs):raise AssertionError('Unbounded communicate forbidden')
        return Child()

    def test_actual_control_timeout_retains_child_through_term_and_close_interruptions(self):
        child=self.control_child(close_interrupt=True)
        ticks=iter([0,31,32,33,38,39,40,41,42,43])
        with tempfile.TemporaryDirectory() as temporary:
            evidence=Path(temporary)
            with patch.object(qualifier.subprocess,'Popen',return_value=child), \
                 patch.object(Path,'read_text',return_value='1 (python) '+' '.join(['0']*19+['123'])), \
                 patch.object(qualifier.os,'readlink',return_value='/usr/bin/python3'), \
                 patch.object(qualifier.os,'set_blocking'), \
                 patch.object(qualifier.os,'read',return_value=b''), \
                 patch.object(qualifier.select,'select',side_effect=lambda readers,*_: (readers,[],[])), \
                 patch.object(qualifier.time,'monotonic',side_effect=lambda:next(ticks,44)), \
                 patch.object(qualifier.time,'sleep'):
                with self.assertRaisesRegex(TimeoutError,'Finite pure control deadline'):
                    qualifier.run_control(['fixed-control'],evidence,'timeout')
            self.assertEqual(1,child.terms);self.assertEqual(1,child.kills)
            self.assertTrue(child.stdout.closed and child.stderr.closed)
            self.assertGreaterEqual(child.stdout.closes,2)
            receipt=json.loads((evidence/'timeout-resource.json').read_text())
            self.assertTrue(receipt['exited'] and receipt['readersSettled'] and receipt['handlesReleased'])
            self.assertEqual(3,len(list(evidence.glob('*quarantine*.json'))))

    def test_actual_control_overflow_is_active_and_releases_before_original_error(self):
        child=self.control_child();reads=[];clock=[0]
        def read(descriptor,size):
            reads.append(size)
            return b'' if child.poll() is not None else b'x'*size
        def now():clock[0]+=.1;return clock[0]
        with tempfile.TemporaryDirectory() as temporary:
            evidence=Path(temporary)
            with patch.object(qualifier.subprocess,'Popen',return_value=child), \
                 patch.object(Path,'read_text',return_value='1 (python) '+' '.join(['0']*19+['123'])), \
                 patch.object(qualifier.os,'readlink',return_value='/usr/bin/python3'), \
                 patch.object(qualifier.os,'set_blocking'),patch.object(qualifier.os,'read',side_effect=read), \
                 patch.object(qualifier.select,'select',side_effect=lambda readers,*_: (readers,[],[])), \
                 patch.object(qualifier.time,'monotonic',side_effect=now),patch.object(qualifier.time,'sleep'):
                with self.assertRaisesRegex(ValueError,'active cap'):
                    qualifier.run_control(['fixed-control'],evidence,'overflow')
            self.assertIn(1,reads);self.assertLessEqual(max(reads),65536)
            self.assertEqual(1,child.kills);self.assertTrue(child.stdout.closed and child.stderr.closed)
            self.assertTrue((evidence/'overflow-resource.json').is_file())
            self.assertFalse((evidence/'overflow.log').exists())

    def fixture(self):
        policy=intake.load_policy(SCRIPTS/'file-native-policy.json');files={};rows=[]
        for number in range(36):
            path='probe/file'+str(number)+'.txt';raw=('public'+str(number)).encode();files['candidate/raw/'+path]=raw
            rows.append({'path':path,'preparedSha256':hashlib.sha256(raw).hexdigest()})
        policy['supervisorFiles']=[]
        for number in range(4):
            path=rows[number]['path'];copyname='helper'+str(number)+'.py';raw=files['candidate/raw/'+path]
            files['supervisor/'+copyname]=raw;policy['supervisorFiles'].append({'path':path,'sealedCopy':copyname,'sha256':hashlib.sha256(raw).hexdigest()})
        files['candidate/complete.diff']=b'synthetic source patch'
        candidate={'base':intake.BASE,'worktree':policy['frozenCandidateWorktree'],'files':rows,'patchSha256':hashlib.sha256(files['candidate/complete.diff']).hexdigest()}
        files['candidate/manifest.json']=json.dumps(candidate).encode();policy['candidateManifestSha256']=hashlib.sha256(files['candidate/manifest.json']).hexdigest()
        supervisor={'candidateManifestSha256':policy['candidateManifestSha256'],'dependencyManifestSha256':'pending','files':policy['supervisorFiles']}
        dependencies=[]
        for number in range(130):
            raw=('dependency'+str(number)).encode();path='file'+str(number)+'.txt';files['dependencies/PublicDependency/'+path]=raw
            dependencies.append({'repository':'PublicDependency','path':path,'sha256':hashlib.sha256(raw).hexdigest()})
        files['dependency-manifest.json']=json.dumps({'files':dependencies}).encode();policy['dependencyManifestSha256']=hashlib.sha256(files['dependency-manifest.json']).hexdigest();supervisor['dependencyManifestSha256']=policy['dependencyManifestSha256'];files['supervisor/manifest.json']=json.dumps(supervisor).encode();policy['supervisorManifestSha256']=hashlib.sha256(files['supervisor/manifest.json']).hexdigest();files['transport-index.json']=b'{}';policy['transportIndexSha256']=hashlib.sha256(files['transport-index.json']).hexdigest()
        return files,policy

    def test_actual_materializer_keeps_original_git_dirs_and_refuses_second_root(self):
        files,policy=self.fixture();source={'base.txt':b'public baseline'};metadata={'.git/HEAD':intake.BASE.encode(),'.git/config':b'[core]\n'}
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp)/'isolated';candidate=intake.materialize(root,source,metadata,files,policy)
            self.assertTrue((candidate/'.git/objects').is_dir());self.assertTrue((candidate/'.git/refs').is_dir())
            self.assertEqual(intake.BASE.encode(),(candidate/'.git/HEAD').read_bytes())
            self.assertEqual(b'public baseline',(candidate/'base.txt').read_bytes())
            with self.assertRaises(ValueError):intake.materialize(root,source,metadata,files,policy)

    def test_nested_source_and_runtime_helper_tampering_reject(self):
        for key in ('candidate/raw/probe/file0.txt','supervisor/helper0.py','dependencies/PublicDependency/file0.txt'):
            files,policy=self.fixture();files[key]+=b'altered'
            with self.assertRaises(ValueError):intake.validate_bindings(files,policy)

    def test_new_checkpoint_source_missing_duplicate_or_changed_rejects(self):
        for change in ('missing','duplicate','changed'):
            files,policy=self.fixture()
            candidate=json.loads(files['candidate/manifest.json'])
            row=candidate['files'][-1]
            oldpath='candidate/raw/'+row['path']
            path='Legacy.Maliev.FileService.Tests/Infrastructure/FileStageCheckpoint.cs'
            row['path']=path
            files['candidate/raw/'+path]=files.pop(oldpath)
            files['candidate/manifest.json']=json.dumps(candidate).encode()
            policy['candidateManifestSha256']=hashlib.sha256(files['candidate/manifest.json']).hexdigest()
            supervisor=json.loads(files['supervisor/manifest.json'])
            supervisor['candidateManifestSha256']=policy['candidateManifestSha256']
            files['supervisor/manifest.json']=json.dumps(supervisor).encode()
            policy['supervisorManifestSha256']=hashlib.sha256(files['supervisor/manifest.json']).hexdigest()
            intake.validate_bindings(files,policy)
            if change=='changed':files['candidate/raw/'+path]+=b'changed'
            else:
                if change=='missing':candidate['files'].pop()
                else:candidate['files'][-1]=copy.deepcopy(candidate['files'][-2])
                files['candidate/manifest.json']=json.dumps(candidate).encode()
                policy['candidateManifestSha256']=hashlib.sha256(files['candidate/manifest.json']).hexdigest()
            with self.assertRaises(ValueError):intake.validate_bindings(files,policy)

    def test_evidence_does_not_copy_private_package_home(self):
        with tempfile.TemporaryDirectory() as tmp:
            candidate=Path(tmp)/'candidate';owned=candidate/'runner-results/owned-build-test';(owned/'home').mkdir(parents=True)
            (owned/'home/package.data').write_bytes(b'private package home');(owned/'sdk.log').write_bytes(b'public SDK proof')
            evidence=Path(tmp)/'evidence';qualifier.retain_evidence(candidate,evidence)
            self.assertTrue((evidence/'native/owned-build-test/sdk.log').is_file())
            self.assertFalse((evidence/'native/owned-build-test/home').exists())


if __name__=='__main__':unittest.main()
