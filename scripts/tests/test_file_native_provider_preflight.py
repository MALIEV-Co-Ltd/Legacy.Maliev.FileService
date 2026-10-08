"""Pure provider-observer controls: no SDK, Docker or native grant."""
import ast
import copy
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import Mock, patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import qualify_file_native as qualifier

class ProviderPreflightControls(unittest.TestCase):
    def setUp(self):
        self.provider={'sdkVersion':'10.0.401','host':{'sha256':'a'*64},
            'files':[{'path':name,'sha256':str(i)*64,'identity':[1,i]} for i,name in enumerate(
                ['vstest.console.dll','vstest.console.deps.json','vstest.console.runtimeconfig.json'],1)],
            'directories':[{'path':'/sdk/10.0.401','identity':[1,8]}]}
        self.policy={'candidateManifestSha256':'b'*64,'supervisorManifestSha256':'c'*64,'dependencyManifestSha256':'d'*64}

    def exercise(self, side_effect=None):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);exe=root/'dotnet';exe.write_bytes(b'fixture')
            owner=SimpleNamespace(bind_vstest_provider=Mock(side_effect=side_effect,return_value=self.provider),main=Mock())
            with patch.object(qualifier.subprocess,'Popen') as popen,patch.object(qualifier.subprocess,'run') as run:
                result=qualifier.observe_vstest_provider(owner,str(exe),root,self.policy)
                popen.assert_not_called();run.assert_not_called();owner.main.assert_not_called()
            owner.bind_vstest_provider.assert_any_call(exe.resolve(strict=True))
            self.assertEqual(2,owner.bind_vstest_provider.call_count)
            receipt=json.loads((root/'provider-preflight.json').read_bytes());self.assertEqual(result,receipt)
            self.assertFalse(receipt['SDKStarted']);self.assertFalse(receipt['nativeTestsExecuted'])
            return receipt

    def test_positive_three_provider_files_and_all_policy_seals_are_retained(self):
        receipt=self.exercise()
        self.assertEqual(self.provider,receipt['provider'])
        for key,value in self.policy.items():self.assertEqual(value,receipt[key])
        self.assertEqual(1,receipt['schemaVersion'])

    def test_no_sdk_or_native_process_is_started(self):
        receipt=self.exercise();self.assertIs(False,receipt['SDKStarted']);self.assertIs(False,receipt['nativeTestsExecuted'])

    def test_unavailable_sdk_refuses_before_owner_or_receipt(self):
        with tempfile.TemporaryDirectory() as d:
            owner=SimpleNamespace(bind_vstest_provider=Mock(),main=Mock())
            with self.assertRaises(RuntimeError):qualifier.observe_vstest_provider(owner,None,Path(d),self.policy)
            owner.bind_vstest_provider.assert_not_called();owner.main.assert_not_called()
            self.assertFalse((Path(d)/'provider-preflight.json').exists())

    def refuse(self, failure):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);exe=root/'dotnet';exe.write_bytes(b'fixture')
            owner=SimpleNamespace(bind_vstest_provider=Mock(side_effect=failure),main=Mock())
            with patch.object(qualifier.subprocess,'Popen') as popen,patch.object(qualifier.subprocess,'run') as run:
                with self.assertRaises((RuntimeError,FileNotFoundError,json.JSONDecodeError)):
                    qualifier.observe_vstest_provider(owner,str(exe),root,self.policy)
                popen.assert_not_called();run.assert_not_called();owner.main.assert_not_called()
            self.assertFalse((root/'provider-preflight.json').exists())

    def test_missing_dll_propagates_and_never_publishes_success(self):
        self.refuse(FileNotFoundError('fixed provider missing'))

    def test_malformed_deps_propagates_and_never_publishes_success(self):
        self.refuse(json.JSONDecodeError('invalid','{',1))

    def test_changed_second_binding_refuses_success(self):
        changed=copy.deepcopy(self.provider);changed['files'][0]['sha256']='f'*64
        self.refuse([self.provider,changed])

    def test_actual_qualification_rejects_unsealed_provider_before_policy_or_grant_fetch(self):
        arguments=['qualifier','--policy','unused','--evidence','unused','--mode','qualification',
            '--grant-bundle-blob','original-placeholder','--grant-bundle-sha256','a'*64]
        with patch.object(qualifier.sys,'platform','linux'),patch.object(qualifier.os,'geteuid',return_value=0,create=True),\
             patch.object(qualifier.sys,'argv',arguments),patch.object(qualifier,'load_policy') as policy,\
             patch.object(qualifier,'fetch_git_blob') as grants,patch.object(qualifier.shutil,'which') as sdk,\
             patch.object(qualifier.subprocess,'Popen') as popen:
            with self.assertRaisesRegex(ValueError,'provider hashes are sealed'):qualifier.main()
            policy.assert_not_called();grants.assert_not_called();sdk.assert_not_called();popen.assert_not_called()

    def test_actual_main_observes_provider_before_preflight_success_and_return(self):
        tree=ast.parse(Path(qualifier.__file__).read_bytes())
        main=next(n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name=='main')
        texts=[ast.unparse(n) for n in main.body]
        observe=next(i for i,s in enumerate(texts) if s.startswith('observe_vstest_provider('))
        success=next(i for i,s in enumerate(texts) if "'preflight.json'" in s)
        stop=next(i for i,s in enumerate(texts) if "args.mode == 'preflight-only'" in s and 'return' in s)
        grants=next(i for i,s in enumerate(texts) if s.startswith('bundle = fetch_git_blob'))
        self.assertLess(observe,success);self.assertLess(success,stop);self.assertLess(stop,grants)
        function=next(n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name=='observe_vstest_provider')
        self.assertFalse(any(isinstance(n,ast.Call) and isinstance(n.func,ast.Attribute) and n.func.attr in
            {'Popen','run','execve','system'} for n in ast.walk(function)))

if __name__=='__main__':unittest.main()
