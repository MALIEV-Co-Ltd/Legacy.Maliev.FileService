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

    def test_closed_trust_witness_retained_without_masking_primary(self):
        primary=RuntimeError('private primary');witness={'reasonBits':2,'componentIndex':1,'uid':0,'mode':0o40777}
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);exe=root/'dotnet';exe.write_bytes(b'fixture')
            owner=SimpleNamespace(bind_vstest_provider=Mock(side_effect=primary),project_vstest_trust_failure=Mock(return_value=witness))
            with self.assertRaises(RuntimeError) as caught:qualifier.observe_vstest_provider(owner,str(exe),root,self.policy)
            self.assertIs(primary,caught.exception);receipt=json.loads((root/'provider-trust-failure.json').read_bytes())
            self.assertEqual(witness,receipt['witness']);self.assertFalse(receipt['SDKStarted']);self.assertFalse(receipt['nativeTestsExecuted'])
            for key,value in self.policy.items():self.assertEqual(value,receipt[key])
            self.assertNotIn('private primary',json.dumps(receipt));self.assertFalse((root/'provider-preflight.json').exists())

    def test_diagnostic_projection_publish_and_byte_cap_faults_preserve_primary(self):
        for variant in ['projection','publish','quota']:
            primary=RuntimeError('primary')
            with tempfile.TemporaryDirectory() as d:
                root=Path(d);exe=root/'dotnet';exe.write_bytes(b'fixture')
                project=Mock(side_effect=ValueError()) if variant=='projection' else Mock(return_value={'x':'x'*5000} if variant=='quota' else {'reasonBits':2})
                owner=SimpleNamespace(bind_vstest_provider=Mock(side_effect=primary),project_vstest_trust_failure=project)
                with patch.object(qualifier,'write_new',side_effect=KeyboardInterrupt()) as writer:
                    with self.assertRaises(RuntimeError) as caught:qualifier.observe_vstest_provider(owner,str(exe),root,self.policy)
                    self.assertIs(primary,caught.exception)
                    if variant!='publish':writer.assert_not_called()
                self.assertFalse((root/'provider-preflight.json').exists())

    def test_projected_observer_uses_trusted_binder_twice_without_host_fallback(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);exe=root/'dotnet';exe.write_bytes(b'fixture');projection={'fixture':'context'}
            owner=SimpleNamespace(bind_qualification_projected_provider=Mock(return_value=self.provider),bind_vstest_provider=Mock())
            receipt=qualifier.observe_vstest_provider(owner,str(exe),root,self.policy,projection)
            self.assertFalse(receipt['SDKStarted']);owner.bind_vstest_provider.assert_not_called()
            self.assertEqual(2,owner.bind_qualification_projected_provider.call_count)
            owner.bind_qualification_projected_provider.assert_called_with(exe.resolve(),projection)

    def test_projected_refusal_preserves_failure_and_no_success(self):
        primary=RuntimeError('untrusted fullSDK')
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);exe=root/'dotnet';exe.write_bytes(b'fixture')
            owner=SimpleNamespace(bind_qualification_projected_provider=Mock(side_effect=primary),bind_vstest_provider=Mock())
            with self.assertRaises(RuntimeError) as caught:qualifier.observe_vstest_provider(owner,str(exe),root,self.policy,{})
            self.assertIs(primary,caught.exception);owner.bind_vstest_provider.assert_not_called()
            self.assertFalse((root/'provider-preflight.json').exists())

    def test_projection_policy_requires_exact_official_seals_and_limits(self):
        actual=json.loads(Path(qualifier.__file__).with_name('file-native-policy.json').read_bytes())['trustedSDKProjection']
        owner=SimpleNamespace(VSTEST_SDK_VERSION=actual['sdkVersion'],SDK_ARCHIVE_URL=actual['archiveURL'],
            SDK_ARCHIVE_SHA512=actual['archiveSha512'],SDK_PROJECTION_ANCHOR=Path(actual['anchor']),
            QUALIFICATION_SDK_ARCHIVE_BYTES=actual['exactArchiveBytes'],QUALIFICATION_SDK_CONTENT_ROWS=actual['contentRows'],QUALIFICATION_SDK_CONTENT_SHA256=actual['contentSha256'],SDK_ARCHIVE_MAX=actual['archiveMaxBytes'],SDK_EXPANDED_MAX=actual['expandedMaxBytes'],
            SDK_FILE_MAX=actual['fileMaxBytes'],SDK_ENTRIES_MAX=actual['entriesMax'])
        qualifier.validate_sdk_projection_policy(owner,{'trustedSDKProjection':actual})
        for field,value in [('archiveSha512','0'*128),('lifetimeSeconds',301),('SDKStarted',True),('anchor','/tmp')]:
            forged=copy.deepcopy(actual);forged[field]=value
            with self.assertRaises(ValueError):qualifier.validate_sdk_projection_policy(owner,{'trustedSDKProjection':forged})

    def test_actual_main_observes_provider_before_preflight_success_and_return(self):
        tree=ast.parse(Path(qualifier.__file__).read_bytes())
        main=next(n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name=='main')
        texts=[ast.unparse(n) for n in main.body]
        observe=next(i for i,s in enumerate(texts) if s.startswith('with owner.trusted_sdk_projection(') and 'observe_vstest_provider(' in s)
        success=next(i for i,s in enumerate(texts) if "'preflight.json'" in s)
        stop=next(i for i,s in enumerate(texts) if "args.mode == 'preflight-only'" in s and 'return' in s)
        grants=next(i for i,s in enumerate(texts) if s.startswith('bundle = fetch_git_blob'))
        self.assertLess(observe,success);self.assertLess(success,stop);self.assertLess(stop,grants)
        function=next(n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name=='observe_vstest_provider')
        self.assertFalse(any(isinstance(n,ast.Call) and isinstance(n.func,ast.Attribute) and n.func.attr in
            {'Popen','run','execve','system'} for n in ast.walk(function)))

class IndependentArchiveControls(unittest.TestCase):
    def test_second_physical_archive_read_exact_bytes_hash_identity_and_cleanup(self):
        import hashlib
        for variant in ['ok','size','hash','identity','deadline']:
            with tempfile.TemporaryDirectory() as d:
                root=Path(d);archive=root/'official-sdk.tar.gz';raw=b'bounded synthetic archive bytes';archive.write_bytes(raw)
                evidence=root/'evidence';evidence.mkdir();before=archive.stat()
                owner=SimpleNamespace(QUALIFICATION_SDK_ARCHIVE_BYTES=len(raw)+(1 if variant=='size' else 0),SDK_ARCHIVE_SHA512='0'*128 if variant=='hash' else hashlib.sha512(raw).hexdigest(),projection_check_deadline=Mock(side_effect=RuntimeError('expired') if variant=='deadline' else None),projection_trust=Mock(return_value=before))
                if variant=='identity':owner.projection_trust.side_effect=[before,SimpleNamespace(st_dev=before.st_dev,st_ino=before.st_ino+1,st_ctime_ns=before.st_ctime_ns,st_size=before.st_size,st_mode=before.st_mode,st_uid=before.st_uid)]
                projection={'root':str(root),'deadline':123}
                with patch.object(qualifier.subprocess,'run') as run,patch.object(qualifier.subprocess,'Popen') as popen:
                    if variant=='ok':
                        receipt=qualifier.independently_read_official_archive(owner,projection,evidence)
                        self.assertEqual((len(raw),hashlib.sha512(raw).hexdigest()),(receipt['archiveBytes'],receipt['archiveSha512']))
                        self.assertTrue(receipt['independentPhysicalRead']);self.assertTrue(receipt['handlesReleased']);self.assertFalse(receipt['SDKStarted'])
                    else:
                        with self.assertRaises((ValueError,RuntimeError)):qualifier.independently_read_official_archive(owner,projection,evidence)
                        self.assertFalse((evidence/'independent-sdk-archive.json').exists())
                    run.assert_not_called();popen.assert_not_called()
                archive.unlink();self.assertFalse(archive.exists())


if __name__=='__main__':unittest.main()
