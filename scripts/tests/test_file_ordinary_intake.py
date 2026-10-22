import base64
import copy
from contextlib import contextmanager
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import subprocess
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import materialize_file_ordinary as intake
import verify_file_ordinary_results as gate


def diagnostic_fixture():
    # Source-shaped synthetic controls only; never represented as observed runtime evidence.
    run = '1' * 32
    return {'run': run, 'resource': {'Id': '2' * 64, 'Created': '2026-10-09T00:00:00Z',
        'StartedAt': '2026-10-09T00:00:01Z', 'Pid': 100,
        'Labels': {'codex.run': run, 'codex.owner': 'file-issue103-discriminator'},
        'Mounts': [{'Type': 'volume', 'Name': 'owned-volume', 'Destination': '/data'}],
        'Memory': 128 * 1024 * 1024, 'NanoCpus': 1_000_000_000,
        'Ports': {'6379/tcp': [{'HostIp': '127.0.0.1', 'HostPort': '32001'}]}},
        'containerRemoved': True, 'volumeNames': ['owned-volume'], 'volumeInventoryComplete': True,
        'volumesRemoved': True, 'cleanupFailures': [],
        'processLedger': [{'purpose': 'inspect exact diagnostic resource', 'pid': pid,
            'birth': '2026-10-09T00:00:01Z', 'executable': '/usr/bin/docker',
            'ownership': 'retained Process instance from successful Start; child not relinquished',
            'metadataFailure': None, 'stopFailure': None, 'exitVerified': True} for pid in (200, 201)],
        'trials': [{'pair': pair, 'pingReadiness': arm, 'stage': 'acquire', 'acquired': 0,
            'failure': None, 'hints': None, 'elapsedMilliseconds': 1,
            'clientCreated': True, 'clientCleanupApplicable': True, 'clientClosed': True,
            'clientDisposed': True, 'clientCleanupFailure': None, 'trace': [], 'traceTruncated': False}
            for pair in range(4) for arm in ((False, True) if pair % 2 == 0 else (True, False))],
        'driver': '2.0.0.0', 'runtimeCauseProven': False, 'productionChanged': False,
        'observerEffect': 'Information logger and exclusive test collection can alter timing.'}


class SourceControls(unittest.TestCase):
    def setUp(self):
        # Synthetic control payload; not the unpublished C# source or runtime evidence.
        self.source = b'owned-source-control\n'
        self.length = patch.object(intake, 'SOURCE_BYTES', len(self.source))
        self.hash = patch.object(intake, 'SOURCE_SHA256', hashlib.sha256(self.source).hexdigest())
        self.length.start(); self.hash.start()
        self.addCleanup(self.length.stop); self.addCleanup(self.hash.stop)

    def capsule(self):
        return {'base': intake.BASE, 'files': [{'path': intake.SOURCE, 'bytes': intake.SOURCE_BYTES,
                'sha256': intake.SOURCE_SHA256, 'contentBase64': base64.b64encode(self.source).decode()}]}

    @contextmanager
    def checkout(self, ignore_source=False):
        with tempfile.TemporaryDirectory(prefix='file-intake-git-control-') as owned:
            root = Path(owned)
            (root / intake.SOURCE).parent.mkdir(parents=True)
            (root / intake.SOURCE).parent.joinpath('.keep').write_text('owned control\n')
            if ignore_source: (root / '.gitignore').write_text(intake.SOURCE + '\n')
            def git(*args):
                return subprocess.run(['git', '-C', owned, *args], check=True, capture_output=True, timeout=20).stdout.decode().strip()
            git('init', '-q'); git('add', '--', '.')
            git('-c', 'user.name=File Control', '-c', 'user.email=file-control@example.invalid', 'commit', '-q', '-m', 'Owned synthetic base')
            with patch.object(intake, 'BASE', git('rev-parse', 'HEAD')):
                yield root
        self.assertFalse(root.exists())

    def test_real_clean_checkout_materializes_successfully(self):
        with self.checkout() as root:
            target = intake.materialize(root, self.source)
            self.assertEqual(target.read_bytes(), self.source)

    def test_real_wrong_head_rejected_before_write(self):
        with self.checkout() as root, patch.object(intake, 'BASE', 'a' * 40):
            with self.assertRaises(ValueError): intake.materialize(root, self.source)
            self.assertFalse((root / intake.SOURCE).exists())

    def test_real_dirty_checkout_rejected_before_write(self):
        with self.checkout() as root:
            (root / 'foreign.txt').write_text('preserve\n')
            with self.assertRaises(ValueError): intake.materialize(root, self.source)
            self.assertFalse((root / intake.SOURCE).exists())
            self.assertEqual((root / 'foreign.txt').read_text(), 'preserve\n')

    def test_real_link_rejected_before_write(self):
        with self.checkout(ignore_source=True) as root:
            other = root / '.git' / 'owned-link-target'; other.write_bytes(b'preserve')
            target = root / intake.SOURCE
            try: target.symlink_to(other)
            except OSError as error: self.skipTest('Host cannot create symlink: ' + type(error).__name__)
            with self.assertRaises(ValueError): intake.materialize(root, self.source)
            self.assertEqual(other.read_bytes(), b'preserve')

    def test_exact_capsule_wire_shape(self):
        self.assertEqual(len(intake.decode(json.dumps(self.capsule()).encode())), intake.SOURCE_BYTES)

    def test_wrong_base_extra_key_extra_row(self):
        for mutation in ('base', 'key', 'row'):
            with self.subTest(mutation=mutation):
                capsule = self.capsule()
                if mutation == 'base': capsule['base'] = 'a' * 40
                elif mutation == 'key': capsule['extra'] = True
                else: capsule['files'].append(capsule['files'][0].copy())
                with self.assertRaises(ValueError): intake.decode(json.dumps(capsule).encode())

    def test_altered_bytes_path_length_hash(self):
        for mutation in ('contentBase64', 'path', 'bytes', 'sha256'):
            with self.subTest(mutation=mutation):
                capsule = self.capsule()
                capsule['files'][0][mutation] = 0 if mutation == 'bytes' else 'AAAA'
                with self.assertRaises(ValueError): intake.decode(json.dumps(capsule).encode())

    def test_duplicate_json_and_oversize_rejected(self):
        for raw in (b'{"base":"a","base":"b"}', b' ' * 32769):
            with self.assertRaises(ValueError): intake.decode(raw)

    def test_exclusive_write_preserves_foreign_preimage(self):
        with self.checkout(ignore_source=True) as root:
            target = root / intake.SOURCE
            target.write_bytes(b'foreign')
            with self.assertRaises(FileExistsError): intake.materialize(root, self.source)
            self.assertEqual(target.read_bytes(), b'foreign')

    def test_readback_failure_rolls_back_only_owned_new_file(self):
        with self.checkout() as root:
            target = root / intake.SOURCE
            with patch.object(intake, 'digest', side_effect=[intake.SOURCE_SHA256, 'bad-readback']):
                with self.assertRaises(ValueError): intake.materialize(root, self.source)
            self.assertFalse(target.exists())


class ResultControls(unittest.TestCase):
    def trx(self):
        q = lambda name: '{' + gate.NS['t'] + '}' + name
        root = ET.Element(q('TestRun'))
        result = ET.SubElement(ET.SubElement(root, q('Results')), q('UnitTestResult'),
                               testId='test', executionId='execution', outcome='Passed', testName=gate.RESULT_NAME)
        ET.SubElement(ET.SubElement(result, q('Output')), q('StdOut')).text = 'REDIS_BOOTSTRAP_DIAGNOSTIC ' + json.dumps(diagnostic_fixture())
        definition = ET.SubElement(ET.SubElement(root, q('TestDefinitions')), q('UnitTest'), id='test', name=gate.RESULT_NAME, storage='/fixture/' + gate.DLL.lower())
        ET.SubElement(definition, q('Execution'), id='execution')
        ET.SubElement(definition, q('TestMethod'), className=gate.CLASS, name=gate.METHOD, codeBase='/fixture/' + gate.DLL)
        ET.SubElement(ET.SubElement(root, q('TestEntries')), q('TestEntry'), testId='test', executionId='execution')
        summary = ET.SubElement(root, q('ResultSummary'), outcome='Completed')
        ET.SubElement(summary, q('Counters'), **{key: '1' if key in {'total', 'executed', 'passed'} else '0' for key in gate.COUNTERS})
        return root

    def test_exact_associations(self):
        self.assertEqual(gate.inspect_trx(ET.tostring(self.trx()), 1)['actualPassed'], 1)

    def test_wrong_compiled_identity_and_result_name(self):
        for field in ('className', 'name', 'codeBase', 'resultName', 'definitionName', 'storage'):
            with self.subTest(field=field):
                root = self.trx()
                if field == 'resultName': root.find('./t:Results/t:UnitTestResult', gate.NS).set('testName', 'Foreign.' + gate.METHOD)
                elif field in ('definitionName', 'storage'):
                    root.find('./t:TestDefinitions/t:UnitTest', gate.NS).set('name' if field == 'definitionName' else field, 'foreign.dll')
                else: root.find('./t:TestDefinitions/t:UnitTest/t:TestMethod', gate.NS).set(field, 'Foreign.Unrelated.dll')
                with self.assertRaises(ValueError): gate.inspect_trx(ET.tostring(root), 1)

    def test_missing_compiled_identity(self):
        for field in ('className', 'name', 'codeBase', 'node'):
            with self.subTest(field=field):
                root = self.trx(); definition = root.find('./t:TestDefinitions/t:UnitTest', gate.NS)
                method = definition.find('./t:TestMethod', gate.NS)
                if field == 'node': definition.remove(method)
                else: del method.attrib[field]
                with self.assertRaises(ValueError): gate.inspect_trx(ET.tostring(root), 1)

    def test_root_foreign_compiled_identity_regression(self):
        root = self.trx()
        root.find('./t:TestDefinitions/t:UnitTest/t:TestMethod', gate.NS).attrib.update(
            className='Foreign.Unrelated.Tests', name='WrongMethod', codeBase='foreign.dll')
        with self.assertRaises(ValueError): gate.inspect_trx(ET.tostring(root), 1)

    def test_wrong_execution_failed_result_and_counter(self):
        for mutation in ('execution', 'outcome', 'counter'):
            with self.subTest(mutation=mutation):
                root = self.trx()
                if mutation == 'execution': root.find('./t:TestEntries/t:TestEntry', gate.NS).set('executionId', 'other')
                elif mutation == 'outcome': root.find('./t:Results/t:UnitTestResult', gate.NS).set('outcome', 'Failed')
                else: root.find('./t:ResultSummary/t:Counters', gate.NS).set('failed', '1')
                with self.assertRaises(ValueError): gate.inspect_trx(ET.tostring(root), 1)

    def test_missing_and_duplicate_results_rejected(self):
        for duplicate in (False, True):
            root = self.trx(); results = root.find('./t:Results', gate.NS)
            if duplicate: results.append(ET.fromstring(ET.tostring(results[0])))
            else: results.remove(results[0])
            with self.assertRaises(ValueError): gate.inspect_trx(ET.tostring(root), 1)

    def test_false_cleanup_receipt_is_rejected(self):
        receipt = {'runtimeCauseProven': False, 'productionChanged': False, 'cleanupFailures': [],
                   'containerRemoved': False, 'volumeInventoryComplete': True, 'volumesRemoved': True}
        with self.assertRaises(ValueError): gate.inspect_diagnostic(receipt)


class DiagnosticControls(unittest.TestCase):
    def reject(self, mutation):
        receipt = diagnostic_fixture()
        mutation(receipt)
        with self.assertRaises(ValueError): gate.inspect_diagnostic(receipt)

    def test_complete_source_shaped_receipt(self):
        gate.inspect_diagnostic(diagnostic_fixture())

    def test_measured_failure_remains_failure(self):
        receipt = diagnostic_fixture()
        receipt['trials'][0].update(acquired=None, failure='RedisTimeoutException', hints={'command=': 'UNWATCH'})
        gate.inspect_diagnostic(receipt)
        self.assertIsNone(receipt['trials'][0]['acquired'])
        self.assertEqual(receipt['trials'][0]['failure'], 'RedisTimeoutException')

    def test_owner(self):
        self.reject(lambda r: r['resource']['Labels'].update({'codex.owner': 'foreign'}))

    def test_run(self):
        self.reject(lambda r: r.update(run='foreign'))

    def test_memory_and_cpu_caps(self):
        for field in ('Memory', 'NanoCpus'):
            with self.subTest(field=field): self.reject(lambda r: r['resource'].update({field: 0}))

    def test_loopback(self):
        self.reject(lambda r: r['resource']['Ports']['6379/tcp'][0].update(HostIp='0.0.0.0'))

    def test_volume_inventory(self):
        for mutation in (lambda r: r.update(volumeNames=[]), lambda r: r['volumeNames'].append('foreign'),
                         lambda r: r.update(volumeInventoryComplete=False), lambda r: r.update(volumesRemoved=False)):
            with self.subTest(mutation=mutation): self.reject(mutation)

    def test_process_exit(self):
        for mutation in (lambda r: r['processLedger'][0].update(exitVerified=False),
                         lambda r: r['processLedger'][0].update(stopFailure='TimeoutException'),
                         lambda r: r.update(processLedger=[])):
            with self.subTest(mutation=mutation): self.reject(mutation)

    def test_eight_distinct_trials(self):
        for mutation in (lambda r: r['trials'].pop(), lambda r: r['trials'].__setitem__(7, copy.deepcopy(r['trials'][0])),
                         lambda r: r['trials'][0].update(pair=True), lambda r: r['trials'][0].update(pingReadiness=0)):
            with self.subTest(mutation=mutation): self.reject(mutation)

    def test_client_cleanup(self):
        for field in ('clientCreated', 'clientCleanupApplicable', 'clientClosed', 'clientDisposed'):
            with self.subTest(field=field): self.reject(lambda r: r['trials'][0].update({field: False}))
        self.reject(lambda r: r['trials'][0].update(clientCleanupFailure='TimeoutException'))

    def test_unsupported_outcomes(self):
        for mutation in (lambda r: r['trials'][0].update(acquired=1), lambda r: r['trials'][0].update(acquired=False),
                         lambda r: r['trials'][0].update(failure='ForeignException', acquired=None),
                         lambda r: r['trials'][0].update(failure='RedisTimeoutException', acquired=0),
                         lambda r: r['trials'][0].update(stage='foreign', failure='RedisTimeoutException', acquired=None),
                         lambda r: r.update(runtimeCauseProven=True), lambda r: r.update(productionChanged=True)):
            with self.subTest(mutation=mutation): self.reject(mutation)


if __name__ == '__main__':
    unittest.main()
