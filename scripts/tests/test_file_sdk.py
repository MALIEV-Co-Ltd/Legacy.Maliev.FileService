import copy
import contextlib
import io
from types import SimpleNamespace
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import qualify_file_sdk as q
from sealed_source_capsule import digest

class GrantControls(unittest.TestCase):
 def setUp(self):
  self.policy=q.load_policy(Path(__file__).resolve().parents[1]/'file-sdk-policy.json')
  self.now=datetime(2026,10,8,tzinfo=timezone.utc)
  self.grant={'owner':q.OWNER,'issuedBy':q.COORDINATOR,'phase':'build','leaseId':'test-only-not-a-permit',
   'issuedUtc':self.now.isoformat(),'expiresUtc':(self.now+timedelta(minutes=15)).isoformat(),
   **{k:self.policy[k] for k in ('candidateManifestSha256','supervisorManifestSha256','dependencyManifestSha256')}}
 def validate(self, grant=None):
  raw=json.dumps(self.grant if grant is None else grant).encode();return q.validate_grant(raw,digest(raw),self.policy,self.now)
 def test_exact_live_finite_bindings(self):self.assertEqual(self.validate()['phase'],'build')
 def test_each_foreign_owner_phase_and_hash_rejected(self):
  for key in ('owner','issuedBy','phase','candidateManifestSha256','supervisorManifestSha256','dependencyManifestSha256'):
   changed=dict(self.grant);changed[key]='foreign'
   with self.subTest(key=key),self.assertRaises(ValueError):self.validate(changed)
 def test_raw_grant_digest_rejected(self):
  raw=json.dumps(self.grant).encode()
  with self.assertRaises(ValueError):q.validate_grant(raw,'0'*64,self.policy,self.now)
 def test_expired_future_and_overlong_grants(self):
  for start,end in [(-901,-1),(1,60),(0,901)]:
   changed=dict(self.grant,issuedUtc=(self.now+timedelta(seconds=start)).isoformat(),expiresUtc=(self.now+timedelta(seconds=end)).isoformat())
   with self.subTest(start=start,end=end),self.assertRaises(ValueError):self.validate(changed)
 def test_naive_or_empty_lease_rejected(self):
  for changed in [dict(self.grant,issuedUtc='2026-10-08T00:00:00'),dict(self.grant,leaseId='')]:
   with self.assertRaises(ValueError):self.validate(changed)
 def test_duplicate_keys_and_oversized_grant_rejected(self):
  for raw in [b'{"owner":1,"owner":2}',b' '*16385]:
   with self.assertRaises(ValueError):q.validate_grant(raw,digest(raw),self.policy,self.now)
 def test_policy_exact_raw_seal(self):
  original=Path(__file__).resolve().parents[1]/'file-sdk-policy.json'
  with tempfile.TemporaryDirectory() as owned:
   path=Path(owned)/'policy.json';path.write_bytes(original.read_bytes()+b' ')
   with self.assertRaises(ValueError):q.load_policy(path)

class SmokeControls(unittest.TestCase):
 def setUp(self):
  self.policy={'supervisorManifestSha256':'s','candidateManifestSha256':'c'}
  self.receipt={'state':'WindowsOwnedPythonSmokePassed','supervisorSha256':'s','sourceSha256':'c','casesPassed':4,'helpersRemaining':[], 'nativeSdkSpawned':False,'rootSdkPermitMinted':False,'cases':[]}
  for name in ('success','timeout','partial-start','pre-phase-final-close'):
   row={'case':name,'smokePassed':True,'verifiedNoOwnedProcesses':True,'verifiedHandlesReleased':True,'unreleasedExactHandles':0,'nativeSdkSpawned':False}
   if name!='pre-phase-final-close':row.update(admissionFreeMiB=4096,admissionSdkJobs=[],actualJobLimits=dict(q.EXPECTED_LIMITS))
   self.receipt['cases'].append(row)
 def test_complete_current_successor_smoke_accepted(self):q.validate_smoke(self.receipt,self.policy)
 def test_old_supervisor_or_incomplete_cleanup_rejected(self):
  for mutate in [lambda r:r.update(supervisorSha256='old'),lambda r:r['cases'][0].update(verifiedHandlesReleased=False),lambda r:r['cases'][0].update(admissionFreeMiB=4095),lambda r:r['cases'][1].update(admissionSdkJobs=[{'pid':1}]),lambda r:r['cases'][2]['actualJobLimits'].update(cpuRate=10000)]:
   changed=copy.deepcopy(self.receipt);mutate(changed)
   with self.assertRaises(ValueError):q.validate_smoke(changed,self.policy)
 def test_duplicate_cases_rejected(self):
  self.receipt['cases'][3]['case']='success'
  with self.assertRaises(ValueError):q.validate_smoke(self.receipt,self.policy)

class MainGateControls(unittest.TestCase):
 def setUp(self):SmokeControls.setUp(self)
 def test_smoke_mode_never_fetches_grant_or_calls_sdk_controller(self):
  with tempfile.TemporaryDirectory() as owned:
   argv=['qualify','--policy','test-only','--intake-policy','test-only','--evidence',owned,'--mode','smoke-only']
   with patch.object(sys,'argv',argv),patch.object(q,'os',SimpleNamespace(name='nt',environ={})),patch.object(q,'load_policy',return_value=self.policy),patch.object(q,'load_intake_policy'),patch.object(q,'install_supervisor'),patch.object(q,'source_controls'),patch.object(q,'run_smoke',return_value=self.receipt),patch.object(q,'fetch_git_blob') as fetch,patch.object(q,'execute_build') as sdk,contextlib.redirect_stdout(io.StringIO()):
    q.main();fetch.assert_not_called();sdk.assert_not_called()
 def test_bad_raw_root_grant_never_calls_sdk_controller(self):
  with tempfile.TemporaryDirectory() as owned:
   argv=['qualify','--policy','test-only','--intake-policy','test-only','--evidence',owned,'--mode','restore-build','--grant-blob','a'*40,'--grant-sha256','0'*64]
   with patch.object(sys,'argv',argv),patch.object(q,'os',SimpleNamespace(name='nt',environ={})),patch.object(q,'load_policy',return_value=self.policy),patch.object(q,'load_intake_policy'),patch.object(q,'install_supervisor'),patch.object(q,'source_controls'),patch.object(q,'run_smoke',return_value=self.receipt),patch.object(q,'fetch_git_blob',return_value=b'{}'),patch.object(q,'execute_build') as sdk,self.assertRaises(ValueError):
    q.main()
   sdk.assert_not_called()
 def test_fetch_credential_removed_before_sdk_entry(self):
  with tempfile.TemporaryDirectory() as owned:
   argv=['qualify','--policy','test-only','--intake-policy','test-only','--evidence',str(Path(owned)/'evidence'),'--mode','restore-build','--grant-blob','a'*40,'--grant-sha256','0'*64]
   environment={'GH_TOKEN':'synthetic-readonly'};calls=[]
   def sdk(*unused):self.assertNotIn('GH_TOKEN',environment);calls.append(True)
   with patch.object(sys,'argv',argv),patch.object(q,'os',SimpleNamespace(name='nt',environ=environment)),patch.object(q,'FIXED_ROOT',str(Path(owned)/'native')),patch.object(q,'load_policy',return_value=self.policy),patch.object(q,'load_intake_policy'),patch.object(q,'install_supervisor'),patch.object(q,'source_controls'),patch.object(q,'run_smoke',return_value=self.receipt),patch.object(q,'fetch_git_blob',return_value=b'{}'),patch.object(q,'validate_grant',return_value={}),patch.object(q,'execute_build',side_effect=sdk),patch.object(q,'retain_build_evidence'),contextlib.redirect_stdout(io.StringIO()):q.main()
   self.assertEqual(calls,[True])
 def test_sdk_cancellation_not_masked_by_evidence_or_reporting_fault(self):
  with tempfile.TemporaryDirectory() as owned:
   argv=['qualify','--policy','test-only','--intake-policy','test-only','--evidence',str(Path(owned)/'evidence'),'--mode','restore-build','--grant-blob','a'*40,'--grant-sha256','0'*64];first=KeyboardInterrupt('test-only SDK cancellation')
   with patch.object(sys,'argv',argv),patch.object(q,'os',SimpleNamespace(name='nt',environ={})),patch.object(q,'FIXED_ROOT',str(Path(owned)/'native')),patch.object(q,'load_policy',return_value=self.policy),patch.object(q,'load_intake_policy'),patch.object(q,'install_supervisor'),patch.object(q,'source_controls'),patch.object(q,'run_smoke',return_value=self.receipt),patch.object(q,'fetch_git_blob',return_value=b'{}'),patch.object(q,'validate_grant',return_value={}),patch.object(q,'execute_build',side_effect=first),patch.object(q,'retain_build_evidence',side_effect=OSError('evidence fault')),patch.object(q.sys.stderr,'write',side_effect=SystemExit('report fault')),self.assertRaises(KeyboardInterrupt) as caught:q.main()
   self.assertIs(caught.exception,first)

class InstallerControls(unittest.TestCase):
 def fixture(self, native):
  names=['file_build_supervisor_draft_v4.py','file_native_windows_v1.py','document_native_controls_v3.py','test_file_build_supervisor_draft_v4.py','test_file_build_controller_v4.py']
  files={name:(b'REPO=None\n' if name==names[0] else b'# fixture only\n') for name in names}
  manifest={'candidateManifestSha256':'c','dependencyManifestSha256':'d','files':[{'sealedCopy':name,'path':str(native/'outputs'/name),'sha256':digest(raw)} for name,raw in files.items()]}
  raw=json.dumps(manifest).encode();files['manifest.json']=raw
  policy={'candidateManifestSha256':'c','dependencyManifestSha256':'d','supervisorManifestSha256':digest(raw),'supervisorManifest':{'path':'manifest.json'},'supervisorFiles':[{'path':name,'sha256':digest(files[name])} for name in names]}
  return policy,files
 def test_successor_install_preserves_existing_exact_helpers(self):
  with tempfile.TemporaryDirectory() as owned:
   native=Path(owned)/'native';policy,files=self.fixture(native)
   (native/'outputs').mkdir(parents=True);existing=native/'outputs/file_native_windows_v1.py';existing.write_bytes(files[existing.name])
   old_path=list(sys.path)
   try:
    with patch.object(q,'FIXED_ROOT',str(native).replace('\\','/')),patch.object(q,'verified_blob',side_effect=lambda row:files[row['path']]):module=q.install_supervisor(policy)
    self.assertEqual(existing.read_bytes(),files[existing.name]);self.assertEqual(module.REPO,native/'work/file-literal-upload-mutation-v8')
   finally:sys.path[:]=old_path
 def test_missing_last_blob_leaves_no_partial_writes(self):
  with tempfile.TemporaryDirectory() as owned:
   native=Path(owned)/'native';policy,files=self.fixture(native)
   def fetch(row):
    if row['path']=='test_file_build_controller_v4.py':raise ValueError('missing final sealed blob')
    return files[row['path']]
   with patch.object(q,'FIXED_ROOT',str(native).replace('\\','/')),patch.object(q,'verified_blob',side_effect=fetch),self.assertRaises(ValueError):q.install_supervisor(policy)
   self.assertFalse((native/'outputs').exists())
 def test_existing_changed_helper_is_preserved_before_any_write(self):
  with tempfile.TemporaryDirectory() as owned:
   native=Path(owned)/'native';policy,files=self.fixture(native);output=native/'outputs';output.mkdir(parents=True);existing=output/'file_native_windows_v1.py';existing.write_bytes(b'foreign')
   with patch.object(q,'FIXED_ROOT',str(native).replace('\\','/')),patch.object(q,'verified_blob',side_effect=lambda row:files[row['path']]),self.assertRaises(ValueError):q.install_supervisor(policy)
   self.assertEqual(existing.read_bytes(),b'foreign');self.assertEqual([p.name for p in output.iterdir()],[existing.name])

class ControlSelectorTests(unittest.TestCase):
 def test_only_declared_control_files_are_loaded(self):
  with tempfile.TemporaryDirectory() as owned:
   root=Path(owned);output=root/'outputs';output.mkdir()
   good=output/'test_declared.py';good.write_text('import unittest\nclass Test(unittest.TestCase):\n def test_ok(self):self.assertTrue(True)\n',encoding='utf-8')
   (output/'test_foreign_v4.py').write_text('raise RuntimeError("must not import undeclared old control")',encoding='utf-8')
   policy={'sourceControlsExpected':1,'supervisorFiles':[{'path':good.name,'sha256':digest(good.read_bytes())}]}
   with patch.object(q,'FIXED_ROOT',str(root)),contextlib.redirect_stderr(io.StringIO()):q.source_controls(policy)
 def test_declared_control_raw_drift_rejected(self):
  with tempfile.TemporaryDirectory() as owned:
   root=Path(owned);output=root/'outputs';output.mkdir();control=output/'test_declared.py';control.write_bytes(b'raise RuntimeError("must not execute")')
   policy={'sourceControlsExpected':1,'supervisorFiles':[{'path':control.name,'sha256':'0'*64}]}
   with patch.object(q,'FIXED_ROOT',str(root)),self.assertRaises(ValueError):q.source_controls(policy)

class DispatchControls(unittest.TestCase):
 def test_frozen_controller_receives_only_bound_build_arguments(self):
  calls=[]
  class Frozen:
   def main(self):calls.append(list(sys.argv))
  policy={k:'sealed' for k in ('candidateManifestSha256','supervisorManifestSha256','dependencyManifestSha256')};previous=sys.argv
  q.execute_build(Frozen(),policy,Path('test-only-grant'))
  self.assertIs(sys.argv,previous);self.assertEqual(len(calls),1)
  self.assertIn('--sole-lane',calls[0]);self.assertIn('--supervisor-manifest-sha256',calls[0])
  self.assertFalse(any(v in ('test','publish','deploy') for v in calls[0]))
 def test_controller_failure_restores_argument_custody(self):
  class Frozen:
   def main(self):raise KeyboardInterrupt('test-only')
  policy={k:'sealed' for k in ('candidateManifestSha256','supervisorManifestSha256')};previous=sys.argv
  with self.assertRaises(KeyboardInterrupt):q.execute_build(Frozen(),policy,Path('test-only-grant'))
  self.assertIs(sys.argv,previous)
 def test_blob_raw_hash_and_size_rejected(self):
  for row in [{'gitBlob':'a'*40,'bytes':3,'sha256':digest(b'good')},{'gitBlob':'a'*40,'bytes':4,'sha256':digest(b'bad')}]:
   with patch.object(q,'fetch_git_blob',return_value=b'bad'),self.assertRaises(ValueError):q.verified_blob(row)

if __name__=='__main__':unittest.main()
