import base64
import copy
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import materialize_file_atomic_acquisition as intake
import verify_file_atomic_acquisition_results as gate


class SourceControls(unittest.TestCase):
    def setUp(self):
        # Synthetic transport controls, never C# source or actual runtime qualification evidence.
        self.names = list(intake.SOURCES)
        self.before = {name: ('before-' + str(i) + '\n').encode() for i, name in enumerate(self.names)}
        self.after = {name: ('after-' + str(i) + '\n').encode() for i, name in enumerate(self.names)}
        authority = {name: (len(self.before[name]), hashlib.sha256(self.before[name]).hexdigest(),
                           len(self.after[name]), hashlib.sha256(self.after[name]).hexdigest()) for name in self.names}
        owned = patch.object(intake, 'SOURCES', authority)
        owned.start()
        self.addCleanup(owned.stop)

    def capsule(self):
        return {'base': intake.BASE, 'files': [{'path': name, 'bytes': len(data),
                'sha256': hashlib.sha256(data).hexdigest(), 'contentBase64': base64.b64encode(data).decode()}
                for name, data in self.after.items()]}

    @contextmanager
    def checkout(self):
        with tempfile.TemporaryDirectory(prefix='file-atomic-control-') as owned:
            root = Path(owned)
            for name, data in self.before.items():
                target = root / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(data)
            def git(*args):
                return subprocess.run(['git', '-C', owned, *args], capture_output=True, check=True, timeout=20).stdout.decode().strip()
            git('init', '-q')
            git('add', '--', '.')
            git('-c', 'user.name=File Control', '-c', 'user.email=file-control@example.invalid', 'commit', '-q', '-m', 'Owned synthetic base')
            with patch.object(intake, 'BASE', git('rev-parse', 'HEAD')):
                yield root, git
        self.assertFalse(root.exists())

    def test_decode_exact_ordered_wire(self):
        self.assertEqual(intake.decode(json.dumps(self.capsule()).encode()), self.after)

    def test_capsule_authority_negatives(self):
        for kind in ['base', 'root-key', 'extra-row', 'missing-row', 'order', 'path', 'bytes', 'bool-bytes', 'hash', 'content', 'row-key']:
            with self.subTest(kind=kind):
                capsule = self.capsule()
                if kind == 'base': capsule['base'] = 'a' * 40
                elif kind == 'root-key': capsule['phase'] = 'green'
                elif kind == 'extra-row': capsule['files'].append(copy.deepcopy(capsule['files'][0]))
                elif kind == 'missing-row': capsule['files'].pop()
                elif kind == 'order': capsule['files'].reverse()
                elif kind == 'path': capsule['files'][0]['path'] = '../foreign.cs'
                elif kind == 'bytes': capsule['files'][0]['bytes'] += 1
                elif kind == 'bool-bytes': capsule['files'][0]['bytes'] = True
                elif kind == 'hash': capsule['files'][0]['sha256'] = 'a' * 64
                elif kind == 'content': capsule['files'][0]['contentBase64'] = 'AAAA'
                else: capsule['files'][0]['extra'] = None
                with self.assertRaises(ValueError): intake.decode(json.dumps(capsule).encode())

    def test_duplicate_nonfinite_oversize_and_nonobject(self):
        for raw in [b'{"base":1,"base":2}', b'{"base":NaN}', b' ' * 65537, b'[]', b'null']:
            with self.subTest(raw=raw[:40]):
                with self.assertRaises(ValueError): intake.decode(raw)

    def test_noncanonical_base64(self):
        capsule = self.capsule()
        capsule['files'][0]['contentBase64'] += '\n'
        with self.assertRaises(ValueError): intake.decode(json.dumps(capsule).encode())

    def test_real_red_only_installs_tests(self):
        with self.checkout() as (root, _):
            selected = intake.materialize(root, self.after, 'red')
            self.assertEqual(selected, [self.names[1]])
            self.assertEqual((root / self.names[0]).read_bytes(), self.before[self.names[0]])
            self.assertEqual((root / self.names[1]).read_bytes(), self.after[self.names[1]])
            self.assertEqual(list(root.rglob('.codex-file-atomic-*')), [])

    def test_real_green_installs_both(self):
        with self.checkout() as (root, _):
            self.assertEqual(intake.materialize(root, self.after, 'green'), self.names)
            self.assertEqual({name: (root/name).read_bytes() for name in self.names}, self.after)
            self.assertEqual(list(root.rglob('.codex-file-atomic-*')), [])

    def test_wrong_head_before_write(self):
        with self.checkout() as (root, _), patch.object(intake, 'BASE', 'a' * 40):
            with self.assertRaises(ValueError): intake.materialize(root, self.after, 'green')
            self.assertEqual({name: (root/name).read_bytes() for name in self.names}, self.before)

    def test_dirty_worktree_index_and_untracked_before_write(self):
        for kind in ['tracked', 'index', 'untracked']:
            with self.subTest(kind=kind), self.checkout() as (root, git):
                target = root / (self.names[0] if kind != 'untracked' else 'foreign.txt')
                target.write_bytes(b'preserve-foreign')
                if kind == 'index': git('add', '--', self.names[0])
                with self.assertRaises(ValueError): intake.materialize(root, self.after, 'green')
                self.assertEqual(target.read_bytes(), b'preserve-foreign')
                self.assertEqual((root/self.names[1]).read_bytes(), self.before[self.names[1]])

    def test_clean_but_wrong_preimage_rejected(self):
        with self.checkout() as (root, _):
            altered = dict(intake.SOURCES)
            row = altered[self.names[1]]
            altered[self.names[1]] = (row[0], 'a' * 64, row[2], row[3])
            with patch.object(intake, 'SOURCES', altered):
                with self.assertRaises(ValueError): intake.materialize(root, self.after, 'green')
            self.assertEqual({name: (root/name).read_bytes() for name in self.names}, self.before)

    def test_wrong_postimage_direct_phase_and_extra_source_rejected(self):
        with self.checkout() as (root, _):
            for data, phase in [(dict(self.after, foreign=b'x'), 'green'), (self.after, 'native'),
                                ({**self.after, self.names[0]: b'foreign'}, 'green')]:
                with self.assertRaises(ValueError): intake.materialize(root, data, phase)
            self.assertEqual({name: (root/name).read_bytes() for name in self.names}, self.before)

    def test_real_source_link_rejected(self):
        with self.checkout() as (root, git):
            target = root/self.names[0]
            foreign = root/'.git'/'owned-link-target'
            foreign.write_bytes(b'preserve')
            target.unlink()
            try: target.symlink_to(foreign)
            except OSError as error: self.skipTest('Host cannot create symlink: ' + type(error).__name__)
            git('add', '--', self.names[0])
            git('-c', 'user.name=File Control', '-c', 'user.email=file-control@example.invalid', 'commit', '-q', '-m', 'Owned link fixture')
            with patch.object(intake, 'BASE', git('rev-parse', 'HEAD')):
                with self.assertRaises(ValueError): intake.materialize(root, self.after, 'green')
            self.assertEqual(foreign.read_bytes(), b'preserve')

    def test_second_replace_failure_rolls_back_first_owned_postimage(self):
        with self.checkout() as (root, git):
            actual_replace = os.replace
            failed = False
            def replace(source, target):
                nonlocal failed
                if Path(target) == root/self.names[1] and not failed:
                    failed = True
                    raise OSError('owned injected second write failure')
                actual_replace(source, target)
            with patch.object(intake.os, 'replace', side_effect=replace):
                with self.assertRaises(OSError): intake.materialize(root, self.after, 'green')
            self.assertEqual({name: (root/name).read_bytes() for name in self.names}, self.before)
            self.assertEqual(git('status', '--porcelain'), '')
            self.assertEqual(list(root.rglob('.codex-file-atomic-*')), [])

    def test_preimage_changed_after_preflight_is_preserved(self):
        with self.checkout() as (root, _):
            actual = intake.temporary
            def temporary(target, source):
                result = actual(target, source)
                target.write_bytes(b'foreign-race')
                return result
            with patch.object(intake, 'temporary', side_effect=temporary):
                with self.assertRaises(ValueError): intake.materialize(root, self.after, 'green')
            self.assertEqual((root/self.names[0]).read_bytes(), b'foreign-race')
            self.assertEqual((root/self.names[1]).read_bytes(), self.before[self.names[1]])
            self.assertEqual(list(root.rglob('.codex-file-atomic-*')), [])

    def test_rollback_preserves_foreign_same_inode_postimage_edit(self):
        with self.checkout() as (root, _):
            actual_replace = os.replace
            primary = OSError('owned injected later write failure')
            def replace(source, target):
                if Path(target) == root/self.names[1]:
                    (root/self.names[0]).write_bytes(b'foreign-same-inode-after-apply')
                    raise primary
                actual_replace(source, target)
            with patch.object(intake.os, 'replace', side_effect=replace):
                with self.assertRaisesRegex(ValueError, 'rollback ownership uncertain') as failure:
                    intake.materialize(root, self.after, 'green')
            self.assertIs(failure.exception.__cause__, primary)
            self.assertEqual((root/self.names[0]).read_bytes(), b'foreign-same-inode-after-apply')
            self.assertEqual((root/self.names[1]).read_bytes(), self.before[self.names[1]])
            self.assertEqual(list(root.rglob('.codex-file-atomic-*')), [])


def ledger_fixture(index, method):
    # Source-shaped synthetic controls only; never represented as observed cleanup evidence.
    run = f'{index + 1:032x}'
    cid = f'{index + 1:064x}'
    volume = f'{index + 10000:064x}'
    owner = 'file-atomic-acquisition'
    client_count = 5 if method == 'ConcurrentFreshConnections_FirstCommandAcquisitionHasOneFencedOwner' else (
        2 if method == 'FreshConnection_CompletedCheckpointReplaysExactResponseAndRejectsDifferentContent' else 1)
    return {'owner': owner, 'run': run, 'containerId': cid,
            'resource': {'Id': cid, 'Created': '2026-10-09T00:00:00Z', 'StartedAt': '2026-10-09T00:00:01Z', 'Pid': 100 + index,
                         'Labels': {'codex.owner': owner, 'codex.run': run}, 'Memory': 128 * 1024 * 1024, 'NanoCpus': 1_000_000_000,
                         'Mounts': [{'Type': 'volume', 'Name': volume, 'Destination': '/data'}],
                         'Ports': {'6379/tcp': [{'HostIp': '127.0.0.1', 'HostPort': '32001'}]}},
            'volumeNames': [volume], 'volumeInventoryComplete': True, 'containerRemoved': True, 'volumesRemoved': True,
            'clients': [{'identity': f'{index * 10 + c + 1:032x}', 'endpoint': '127.0.0.1:32001', 'createdUtc': '2026-10-09T00:00:01Z',
                         'closed': True, 'disposed': True, 'cleanupFailure': None} for c in range(client_count)],
            'cleanupFailures': [], 'processLedger': [{'pid': 200 + index * 3 + c, 'birth': '2026-10-09T00:00:01Z',
                         'executable': '/usr/bin/docker', 'exitVerified': True, 'stopFailure': None} for c in range(3)]}


def trx_fixture(kind):
    q = lambda name: '{' + gate.NS['t'] + '}' + name
    root = ET.Element(q('TestRun'))
    results = ET.SubElement(root, q('Results'))
    definitions = ET.SubElement(root, q('TestDefinitions'))
    entries = ET.SubElement(root, q('TestEntries'))
    cases = [(gate.RED, None)] if kind == 'red' else [(method, operation if number == 4 else None)
            for method, number in gate.METHODS.items() for operation in (['renew', 'release', 'unknown', 'complete'] if number == 4 else [None])]
    count = {'red': 1, 'focused': 16, 'full': 1562}[kind]
    cases.extend((f'Other{index}', None) for index in range(count - len(cases)))
    for index, (method, operation) in enumerate(cases):
        target = method in gate.METHODS
        cls = gate.CLASS if target else 'Legacy.Maliev.FileService.Tests.OtherTests'
        name = cls + '.' + method + (f'(operation: "{operation}")' if operation else '')
        result = ET.SubElement(results, q('UnitTestResult'), testId=f't{index}', executionId=f'e{index}',
                               outcome='Failed' if kind == 'red' else 'Passed', testName=name)
        if target:
            output = ET.SubElement(result, q('Output'))
            ET.SubElement(output, q('StdOut')).text = 'FILE_ATOMIC_RESOURCE=' + json.dumps(ledger_fixture(index, method))
            if kind == 'red':
                error = ET.SubElement(output, q('ErrorInfo'))
                ET.SubElement(error, q('Message')).text = 'System.InvalidOperationException : Upload checkpoint changed during acquisition.'
                ET.SubElement(error, q('StackTrace')).text = 'RedisUploadIdempotencyStore.AcquireAsync in RedisUploadIdempotencyStore.cs:line 28\n' + gate.RED
        definition = ET.SubElement(definitions, q('UnitTest'), id=f't{index}', name=name, storage='/fixture/' + gate.DLL.lower())
        ET.SubElement(definition, q('Execution'), id=f'e{index}')
        ET.SubElement(definition, q('TestMethod'), className=cls, name=method, codeBase='/fixture/' + gate.DLL)
        ET.SubElement(entries, q('TestEntry'), testId=f't{index}', executionId=f'e{index}')
    summary = ET.SubElement(root, q('ResultSummary'), outcome='Failed' if kind == 'red' else 'Completed')
    ET.SubElement(summary, q('Counters'), **{key: str(count) if key in {'total', 'executed'} else (
        '1' if kind == 'red' and key == 'failed' else str(count) if kind != 'red' and key == 'passed' else '0') for key in gate.COUNTERS})
    return root


class ResultControls(unittest.TestCase):
    def test_unmocked_exact_red_and_focused_and_full(self):
        for kind in ['red', 'focused', 'full']:
            with self.subTest(kind=kind):
                self.assertEqual(gate.inspect_trx(ET.tostring(trx_fixture(kind)), kind)['kind'], kind)

    def test_all_sixteen_counter_corruptions(self):
        for kind in ['red', 'focused', 'full']:
            for name in gate.COUNTERS:
                with self.subTest(kind=kind, counter=name):
                    root = trx_fixture(kind)
                    root.find('./t:ResultSummary/t:Counters', gate.NS).set(name, '999')
                    with self.assertRaises(ValueError): gate.inspect_trx(ET.tostring(root), kind)

    def test_compiled_identity_negatives(self):
        for kind in ['red', 'focused', 'full']:
            for field in ['className', 'name', 'codeBase', 'storage', 'definitionName', 'execution', 'resultName']:
                with self.subTest(kind=kind, field=field):
                    root = trx_fixture(kind)
                    definition = root.find('./t:TestDefinitions/t:UnitTest', gate.NS)
                    if field == 'storage': definition.set('storage', '/foreign.dll')
                    elif field == 'definitionName': definition.set('name', 'Foreign')
                    elif field == 'execution': definition.find('./t:Execution', gate.NS).set('id', 'foreign')
                    elif field == 'resultName': root.find('./t:Results/t:UnitTestResult', gate.NS).set('testName', 'Foreign')
                    else: definition.find('./t:TestMethod', gate.NS).set(field, 'Foreign')
                    with self.assertRaises(ValueError): gate.inspect_trx(ET.tostring(root), kind)

    def test_duplicate_missing_and_mismatched_joins(self):
        for kind in ['red', 'focused', 'full']:
            for field in ['result', 'definition', 'entry', 'execution', 'testId']:
                with self.subTest(kind=kind, field=field):
                    root = trx_fixture(kind)
                    if field in {'result', 'definition', 'entry'}:
                        group = root.find('./t:' + {'result': 'Results', 'definition': 'TestDefinitions', 'entry': 'TestEntries'}[field], gate.NS)
                        group.append(copy.deepcopy(group[0]))
                    else: root.find('./t:TestEntries/t:TestEntry', gate.NS).set('executionId' if field == 'execution' else 'testId', 'foreign')
                    with self.assertRaises(ValueError): gate.inspect_trx(ET.tostring(root), kind)

    def test_red_failure_cannot_be_build_or_unrelated_or_pass(self):
        for field in ['message', 'stack', 'outcome', 'summary']:
            root = trx_fixture('red')
            if field == 'message': root.find('./t:Results/t:UnitTestResult/t:Output/t:ErrorInfo/t:Message', gate.NS).text = 'Moq.MockException : unrelated'
            elif field == 'stack': root.find('./t:Results/t:UnitTestResult/t:Output/t:ErrorInfo/t:StackTrace', gate.NS).text = 'Build failed'
            elif field == 'outcome': root.find('./t:Results/t:UnitTestResult', gate.NS).set('outcome', 'Passed')
            else: root.find('./t:ResultSummary', gate.NS).set('outcome', 'Completed')
            with self.assertRaises(ValueError): gate.inspect_trx(ET.tostring(root), 'red')

    def test_fencing_case_cannot_be_repeated_to_replace_other_cases(self):
        root = trx_fixture('focused')
        rows = root.findall('./t:Results/t:UnitTestResult', gate.NS)
        definitions = root.findall('./t:TestDefinitions/t:UnitTest', gate.NS)
        selected = [index for index, row in enumerate(rows) if 'SupersededWorker' in row.get('testName', '')]
        duplicate = rows[selected[0]].get('testName')
        rows[selected[1]].set('testName', duplicate)
        definitions[selected[1]].set('name', duplicate)
        with self.assertRaises(ValueError): gate.inspect_trx(ET.tostring(root), 'focused')

    def test_resource_cleanup_authority_negatives(self):
        for field in ['owner', 'run', 'containerId', 'removed', 'inventory', 'volumes', 'memory', 'cpu', 'port', 'mount', 'client', 'process', 'schema', 'cleanup']:
            with self.subTest(field=field):
                root = trx_fixture('red')
                node = root.find('./t:Results/t:UnitTestResult/t:Output/t:StdOut', gate.NS)
                item = json.loads(node.text.split('=', 1)[1])
                if field in {'owner', 'run', 'containerId'}: item[field] = 'foreign'
                elif field == 'removed': item['containerRemoved'] = False
                elif field == 'inventory': item['volumeInventoryComplete'] = False
                elif field == 'volumes': item['volumeNames'] = []
                elif field == 'memory': item['resource']['Memory'] = 0
                elif field == 'cpu': item['resource']['NanoCpus'] = 0
                elif field == 'port': item['resource']['Ports']['6379/tcp'][0]['HostIp'] = '0.0.0.0'
                elif field == 'mount': item['resource']['Mounts'][0]['Type'] = 'bind'
                elif field == 'client': item['clients'][0]['disposed'] = False
                elif field == 'process': item['processLedger'][0]['exitVerified'] = False
                elif field == 'schema': item['extra'] = True
                else: item['cleanupFailures'] = ['unverified']
                node.text = 'FILE_ATOMIC_RESOURCE=' + json.dumps(item)
                with self.assertRaises(ValueError): gate.inspect_trx(ET.tostring(root), 'red')

    def test_empty_volume_inventory_is_valid_only_when_complete(self):
        root = trx_fixture('red')
        node = root.find('./t:Results/t:UnitTestResult/t:Output/t:StdOut', gate.NS)
        item = json.loads(node.text.split('=', 1)[1])
        item['resource']['Mounts'] = []
        item['volumeNames'] = []
        item['processLedger'].pop()
        node.text = 'FILE_ATOMIC_RESOURCE=' + json.dumps(item)
        gate.inspect_trx(ET.tostring(root), 'red')
        item['volumeInventoryComplete'] = False
        node.text = 'FILE_ATOMIC_RESOURCE=' + json.dumps(item)
        with self.assertRaises(ValueError): gate.inspect_trx(ET.tostring(root), 'red')

    def test_resource_ledger_duplicate_and_reused_identity_rejected(self):
        root = trx_fixture('focused')
        rows = root.findall('./t:Results/t:UnitTestResult', gate.NS)
        first = rows[0].find('./t:Output/t:StdOut', gate.NS)
        second = rows[1].find('./t:Output/t:StdOut', gate.NS)
        second.text = first.text
        with self.assertRaises(ValueError): gate.inspect_trx(ET.tostring(root), 'focused')
        root = trx_fixture('red')
        node = root.find('./t:Results/t:UnitTestResult/t:Output/t:StdOut', gate.NS)
        node.text += '\n' + node.text
        with self.assertRaises(ValueError): gate.inspect_trx(ET.tostring(root), 'red')


if __name__ == '__main__':
    unittest.main()
