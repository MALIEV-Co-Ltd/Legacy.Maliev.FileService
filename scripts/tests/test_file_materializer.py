import json
from pathlib import Path
import sys
import subprocess
from unittest.mock import patch
import tempfile
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import materialize_file_candidate as m
import sealed_source_capsule as s


def fixture():
    files={};rows=[]
    for i in range(21):
        path=f'part-{i}.txt';raw=f'raw-{i}\r\n'.encode();files['candidate/raw/'+path]=raw;rows.append({'path':path,'preparedSha256':s.digest(raw)})
    candidate={'base':m.BASE,'worktree':m.FIXED_ROOT+'/work/file-literal-upload-mutation-v8','files':rows}
    raw=(json.dumps(candidate)+'\n').encode();files['candidate/manifest.json']=raw
    supervisor_rows=[]
    for i in range(5):
        name=f'helper-{i}.py';raw=b'placeholder\r\n';files['supervisor/'+name]=raw;supervisor_rows.append({'path':m.FIXED_ROOT+'/outputs/'+name,'sealedCopy':name,'sha256':s.digest(raw)})
    supervisor={'candidateManifestSha256':s.digest(files['candidate/manifest.json']),'files':supervisor_rows}
    files['supervisor/manifest.json']=(json.dumps(supervisor)+'\n').encode()
    deps=[]
    for i in range(130):
        raw=b'dependency\r\n';key=f'Repo/f{i}.txt';files['dependencies/'+key]=raw;deps.append({'repository':'Repo','path':f'f{i}.txt','sha256':s.digest(raw)})
    files['dependency-manifest.json']=(json.dumps({'files':deps})+'\n').encode();files['transport-index.json']=b'{}\n'
    policy={'candidateManifestSha256':s.digest(files['candidate/manifest.json']),'supervisorManifestSha256':s.digest(files['supervisor/manifest.json']),'dependencyManifestSha256':s.digest(files['dependency-manifest.json']),'transportIndexSha256':s.digest(files['transport-index.json'])}
    return files,policy


class FileIntakeTests(unittest.TestCase):
    def test_nested_bindings(self):
        files,policy=fixture();candidate,supervisor=m.validate_bindings(files,policy)
        self.assertEqual(len(candidate['files']),21);self.assertEqual(len(supervisor['files']),5)
    def test_candidate_base_not_hidden_by_matching_path(self):
        files,policy=fixture();obj=s.parse_json(files['candidate/manifest.json']);obj['base']='0'*40;files['candidate/manifest.json']=json.dumps(obj).encode();policy['candidateManifestSha256']=s.digest(files['candidate/manifest.json'])
        with self.assertRaises(ValueError):m.validate_bindings(files,policy)
    def test_modified_candidate_raw(self):
        files,policy=fixture();files['candidate/raw/part-0.txt']=b'wrong'
        with self.assertRaises(ValueError):m.validate_bindings(files,policy)
    def test_modified_dependency(self):
        files,policy=fixture();files['dependencies/Repo/f0.txt']=b'wrong'
        with self.assertRaises(ValueError):m.validate_bindings(files,policy)
    def test_modified_helper(self):
        files,policy=fixture();files['supervisor/helper-0.py']=b'wrong'
        with self.assertRaises(ValueError):m.validate_bindings(files,policy)
    def test_missing_raw_file(self):
        files,policy=fixture();del files['candidate/raw/part-0.txt']
        with self.assertRaises(KeyError):m.validate_bindings(files,policy)
    def test_fresh_materialization_preserves_bytes(self):
        files,policy=fixture()
        with tempfile.TemporaryDirectory() as parent:
            root=Path(parent)/'fresh';candidate=m.materialize(root,{'baseline.txt':b'baseline\r\n'},{'.git/HEAD':(m.BASE+'\n').encode()},files,policy)
            self.assertEqual((candidate/'part-0.txt').read_bytes(),b'raw-0\r\n')
            self.assertEqual((candidate/'baseline.txt').read_bytes(),b'baseline\r\n')
            self.assertEqual((root/'work/file-prefix-native-pinned-dependencies-20261008/Repo/f0.txt').read_bytes(),b'dependency\r\n')
            with self.assertRaises(ValueError):m.materialize(root,{}, {},files,policy)
    def test_checkout_rejects_extra_ignored_source(self):
        with tempfile.TemporaryDirectory() as parent:
            root=Path(parent);(root/'.git').mkdir();(root/'.git/objects').mkdir();(root/'.git/refs').mkdir();(root/'.git/HEAD').write_text(m.BASE+'\n');(root/'file.txt').write_bytes(b'baseline');(root/'ignored-extra.txt').write_bytes(b'extra')
            policy={'baseFiles':[{'path':'file.txt','checkoutBytes':8,'checkoutSha256':s.digest(b'baseline')}]}
            with self.assertRaises(ValueError):m.read_base(root,policy)
    def test_checkout_exact_raw_projection(self):
        with tempfile.TemporaryDirectory() as parent:
            root=Path(parent);(root/'.git').mkdir();(root/'.git/objects').mkdir();(root/'.git/refs').mkdir();(root/'.git/HEAD').write_text(m.BASE+'\n');(root/'file.ps1').write_bytes(b'baseline\r\n')
            policy={'baseFiles':[{'path':'file.ps1','checkoutBytes':10,'checkoutSha256':s.digest(b'baseline\r\n')}]}
            source,metadata=m.read_base(root,policy);self.assertEqual(source['file.ps1'],b'baseline\r\n');self.assertIn('.git/HEAD',metadata)
    def test_checkout_wrong_head(self):
        with tempfile.TemporaryDirectory() as parent:
            root=Path(parent);(root/'.git').mkdir();(root/'.git/objects').mkdir();(root/'.git/refs').mkdir();(root/'.git/HEAD').write_text('0'*40+'\n')
            with self.assertRaises(ValueError):m.read_base(root,{'baseFiles':[]})
    def test_checkout_credentials_not_copied(self):
        with tempfile.TemporaryDirectory() as parent:
            root=Path(parent);(root/'.git').mkdir();(root/'.git/objects').mkdir();(root/'.git/refs').mkdir();(root/'.git/HEAD').write_text(m.BASE+'\n');(root/'.git/config').write_text('extraheader=synthetic')
            with self.assertRaises(ValueError):m.read_base(root,{'baseFiles':[]})
    def test_raw_policy_seal_rejects_semantically_identical_edit(self):
        original = Path(__file__).resolve().parents[1]/'file-candidate-policy.json'
        self.assertEqual(m.load_policy(original)['owner'], m.OWNER)
        with tempfile.TemporaryDirectory() as parent:
            changed = Path(parent)/'policy.json'
            changed.write_bytes(original.read_bytes()+b' ')
            with self.assertRaises(ValueError): m.load_policy(changed)
    def test_profile_rejects_foreign_owner(self):
        original = Path(__file__).resolve().parents[1]/'file-candidate-policy.json'
        policy = m.load_policy(original); policy['owner'] = 'foreign'
        with self.assertRaises(ValueError): m.validate_profile(policy)
    def test_committed_profile_has_no_sdk_authority(self):
        policy=s.parse_json((Path(__file__).resolve().parents[1]/'file-candidate-policy.json').read_bytes());m.validate_profile(policy)
        self.assertFalse(policy['nativeTestsAuthorized']);self.assertEqual(len(policy['entries']),178)

class GitDirectoryProjectionTests(unittest.TestCase):
    def test_actual_empty_refs_projection_preserves_plain_git_identity(self):
        with tempfile.TemporaryDirectory() as owned:
            parent=Path(owned);original=parent/'original';original.mkdir()
            def git(repo,*args):return subprocess.run(['git','-C',str(repo),*args],capture_output=True,timeout=5,check=True)
            git(original,'init');git(original,'-c','user.name=Fixture','-c','user.email=fixture@example.invalid','commit','--allow-empty','-m','fixture')
            branch=git(original,'symbolic-ref','HEAD').stdout.decode().strip();head=git(original,'rev-parse','HEAD').stdout.decode().strip()
            git(original,'checkout','--detach',head);git(original,'update-ref','-d',branch)
            self.assertFalse(any(path.is_file() for path in (original/'.git/refs').rglob('*')))
            with patch.object(m,'BASE',head):source,metadata=m.read_base(original,{'baseFiles':[]})
            broken=parent/'file-only'
            for name,raw in metadata.items():
                target=broken/name;target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(raw)
            self.assertFalse((broken/'.git/refs').exists())
            failed=subprocess.run(['git','-C',str(broken),'rev-parse','HEAD'],capture_output=True,timeout=5)
            self.assertEqual(failed.returncode,128);self.assertIn(b'not a git repository',failed.stderr)
            files,policy=fixture();projection=m.materialize(parent/'fresh',source,metadata,files,policy)
            self.assertEqual(git(projection,'rev-parse','HEAD').stdout.decode().strip(),head)
            self.assertTrue((projection/'.git/refs').is_dir())
            for name,raw in metadata.items():self.assertEqual((projection/name).read_bytes(),raw)
    def test_original_missing_required_directory_rejected(self):
        for missing in ('objects','refs'):
            with self.subTest(missing=missing),tempfile.TemporaryDirectory() as owned:
                root=Path(owned);metadata=root/'.git';metadata.mkdir();(metadata/'HEAD').write_bytes(m.BASE.encode())
                (metadata/('refs' if missing=='objects' else 'objects')).mkdir()
                with self.assertRaisesRegex(ValueError,'Required original Git directory missing'):m.read_base(root,{'baseFiles':[]})

if __name__=='__main__':unittest.main()
