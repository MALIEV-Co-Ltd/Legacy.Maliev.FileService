import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import os
import tempfile
import unittest
from unittest.mock import patch

HERE=Path(__file__).resolve().parents[2]
BASE=os.environ.get('FILE_CURRENT_TEST_BASE','native-base')
sys.path.insert(0,str(HERE/'scripts'))
import materialize_file_current_transport as transport

class TransportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.capsule=(HERE/'qualification-inputs/file-current/current-capsule.zip').read_bytes()
        cls.index=(HERE/'scripts/file-current-transport-index.json').read_bytes()
        cls.baseline=(HERE/'qualification-inputs/file-current/baseline-artifact.zip').read_bytes()
        cls.files=transport.decode(cls.capsule,cls.index,cls.baseline)
    def test_actual_reconstruction_association_and_cleanup(self):
        with tempfile.TemporaryDirectory(prefix='owned-current-transport-') as owned:
            root=Path(owned)/'transport';evidence=Path(owned)/'evidence'
            transport.run(self.files,self.baseline,BASE,root,evidence,'source-association')
            self.assertFalse(root.exists())
            association=json.loads((evidence/'source-association.json').read_bytes())
            self.assertFalse(association['SDKStarted'])
            cleanup=json.loads((evidence/'transport-cleanup.json').read_bytes())
            self.assertTrue(cleanup['ownedRootAbsent'])
            for color in ('red','green'):
                receipt=json.loads((evidence/(color+'-materialization-receipt.json')).read_bytes())
                self.assertEqual((receipt['sourceFiles'],receipt['fixturePostimages'],receipt['producerConsumerPostimages'],receipt['producerProjectionFiles']),(319,11,2,10))
    def test_qualification_refuses_before_root_creation(self):
        with tempfile.TemporaryDirectory() as owned:
            root=Path(owned)/'transport'
            with self.assertRaisesRegex(ValueError,'source-only'):
                transport.run(self.files,self.baseline,'unread-base',root,Path(owned)/'evidence','qualification','original-bundle','original-sha')
            self.assertFalse(root.exists())
    def test_evidence_path_failure_removes_new_owned_root(self):
        with tempfile.TemporaryDirectory(prefix='owned-evidence-failure-') as owned:
            root=Path(owned)/'transport';evidence=Path(owned)/'evidence'
            evidence.write_bytes(b'owned evidence-path failure fixture')
            # Exercise actual mkdir failure, not a synthetic evidence exception.
            with patch('materialize_file_native.current_git_read',return_value=({},{})):
                with self.assertRaises(FileExistsError):
                    transport.run(self.files,self.baseline,'unused-base',root,evidence,'source-association')
            self.assertFalse(root.exists())
            self.assertEqual(evidence.read_bytes(),b'owned evidence-path failure fixture')
    def test_first_root_refusal_still_cleans_valid_second_root_and_preserves_primary(self):
        import materialize_file_native as intake
        primary=RuntimeError('owned primary association failure')
        with tempfile.TemporaryDirectory(prefix='owned-independent-cleanup-') as owned:
            root=Path(owned)/'transport';evidence=Path(owned)/'evidence'
            red=root/'file-current-delete-move-route-red'
            green=root/'file-current-delete-move-route-green'
            selected=red/'candidate/.dockerignore'
            original=None
            def fail_association(argv):
                nonlocal original
                original=selected.read_bytes();selected.write_bytes(original+b'owned mutation')
                raise primary
            try:
                with patch('qualify_file_native.current_route_main',side_effect=fail_association):
                    with self.assertRaises(RuntimeError) as caught:
                        transport.run(self.files,self.baseline,BASE,root,evidence,'source-association')
                self.assertIs(caught.exception,primary)
                self.assertTrue(red.exists())
                self.assertFalse(green.exists())
                self.assertIn('source-cleanup-RED',' '.join(primary.__notes__))
                cleanup=json.loads((evidence/'transport-cleanup.json').read_bytes())
                self.assertEqual(cleanup['cleanupFailures'],['source-cleanup-RED','source-root-retained'])
                self.assertFalse(cleanup['ownedRootAbsent'])
            finally:
                if original is not None:
                    selected.write_bytes(original)
                    receipt=(evidence/'red-materialization-receipt.json').read_bytes()
                    intake.cleanup_current_materialization(red,self.files['input/current-source-policy.json'],hashlib.sha256(receipt).hexdigest())
                self.assertFalse(red.exists());self.assertFalse(green.exists())
                # Dispose only exact test-owned transport bytes, after recovery.
                expected=dict(self.files)
                expected['supervisor/supervisor-manifest.json']=self.files['input/supervisor-manifest.json']
                expected['input/baseline-artifact.zip']=self.baseline
                for name,raw in expected.items():
                    path=root/name
                    self.assertEqual(path.read_bytes(),raw);path.unlink()
                for directory in sorted(root.rglob('*'),key=lambda p:len(p.parts),reverse=True):directory.rmdir()
                root.rmdir()
    def test_source_cannot_consume_grants(self):
        with tempfile.TemporaryDirectory() as owned:
            with self.assertRaisesRegex(ValueError,'cannot consume grants'):
                transport.run(self.files,self.baseline,'unread-base',Path(owned)/'root',Path(owned)/'evidence','source-association','grant',None)
    def test_changed_capsule_refused(self):
        with self.assertRaisesRegex(ValueError,'capsule'):
            transport.decode(self.capsule[:-1]+b'X',self.index,self.baseline)
    def test_changed_baseline_refused(self):
        with self.assertRaisesRegex(ValueError,'baseline'):
            transport.decode(self.capsule,self.index,self.baseline[:-1]+b'X')
    def test_changed_index_refused(self):
        with self.assertRaisesRegex(ValueError,'index'):
            transport.decode(self.capsule,self.index+b' ',self.baseline)
    def test_capsule_closed_pins_and_original_quotas(self):
        self.assertEqual(len(self.files),170)
        self.assertLess(len(self.capsule),1024*1024)
        self.assertLess(len(self.baseline),1024*1024)
        self.assertEqual(sum(map(len,self.files.values())),1497320)
        for name in ('materialize_file_native.py','materialize_file_candidate.py','sealed_source_capsule.py','qualify_file_native.py'):
            self.assertEqual(self.files['scripts/'+name],(HERE/'scripts'/name).read_bytes())
    def test_original_workflow_custody_unchanged(self):
        raw=(HERE/'.github/workflows/file-native-qualification.yml').read_text()
        for expected in ('runs-on: ubuntu-24.04','timeout-minutes: 65','group: file-native-qualification','cancel-in-progress: false','permissions:\n  contents: read','needs: transport-controls','ce183871469381eca13b359d997ad68042c4aa46','default: source-association'):
            self.assertIn(expected,raw)
        self.assertNotIn('07f0b5b721e3df758a9ec742c6f167491f899e44',raw)

if __name__=='__main__':
    if '--focused' in sys.argv:
        suite=unittest.TestSuite(TransportTests(name) for name in ('test_evidence_path_failure_removes_new_owned_root','test_first_root_refusal_still_cleans_valid_second_root_and_preserves_primary'))
        result=unittest.TextTestRunner(verbosity=2).run(suite);raise SystemExit(not result.wasSuccessful())
    unittest.main(verbosity=2)
