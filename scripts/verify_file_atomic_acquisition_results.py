"""Strict ordinary RED/GREEN compiled-test joins and exact owned-resource evidence."""
import argparse
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET
from sealed_source_capsule import parse_json

NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
CLASS = 'Legacy.Maliev.FileService.Tests.Data.RedisUploadIdempotencyStoreTests'
DLL = 'Legacy.Maliev.FileService.Tests.dll'
RED = 'FirstAcquisition_DoesNotDependOnConditionalTransactionCompletion'
METHODS = {
    'ExpiredWorkerLease_TransitionsDurableCheckpointToUnknownWithoutReacquiring': 1,
    'CompletedCheckpoint_ReplaysExactSignedResponseAndRejectsDifferentPayload': 1,
    'ConcurrentAcquisition_OneOwnerAndEveryOtherCallerInProgress': 1,
    'SupersededWorker_CannotChangeNewReservationOrItsLease': 4,
    'ExpiredLease_StaleWorkerCannotRenewReleaseOrCompletePendingCheckpoint': 1,
    'UnknownResponse_FinalizesExactResponseWithoutReexecutionOrNewLease': 1,
    RED: 1,
    'FirstAcquisition_PersistsExactEnvelopeAndExistingRetentionAndLease': 1,
    'ConcurrentFreshConnections_FirstCommandAcquisitionHasOneFencedOwner': 1,
    'OrphanLease_FirstAcquisitionDoesNotOverwriteOrCreateCheckpoint': 1,
    'CancelledFirstAcquisition_DoesNotSubmitOrCreateCheckpointOrLease': 1,
    'CancellationAfterSubmission_PreservesCommittedCheckpointForReconciliation': 1,
    'FreshConnection_CompletedCheckpointReplaysExactResponseAndRejectsDifferentContent': 1,
}
COUNTERS = {'total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted', 'inconclusive',
            'passedButRunAborted', 'notRunnable', 'notExecuted', 'disconnected', 'warning', 'completed', 'inProgress', 'pending'}


def require(value, message):
    if not value:
        raise ValueError(message)


def resource_evidence(raw, method):
    lines = [line.split('FILE_ATOMIC_RESOURCE=', 1)[1] for line in raw.splitlines() if 'FILE_ATOMIC_RESOURCE=' in line]
    require(len(lines) == 1, 'one actual resource ledger required')
    item = parse_json(lines[0])
    require(set(item) == {'owner', 'run', 'containerId', 'resource', 'volumeNames', 'volumeInventoryComplete',
                          'containerRemoved', 'volumesRemoved', 'clients', 'cleanupFailures', 'processLedger'}, 'resource schema differs')
    require(item['owner'] == 'file-atomic-acquisition' and re.fullmatch('[0-9a-f]{32}', item['run']), 'resource owner/run differs')
    require(re.fullmatch('[0-9a-f]{64}', item['containerId']), 'exact container id required')
    require(not item['cleanupFailures'] and all(item[k] is True for k in ['volumeInventoryComplete', 'containerRemoved', 'volumesRemoved']), 'physical resource cleanup unverified')
    owned = item['resource']
    require(owned['Id'] == item['containerId'] and owned['Labels']['codex.owner'] == item['owner']
            and owned['Labels']['codex.run'] == item['run'], 'container ownership differs')
    require(owned['Memory'] == 128 * 1024 * 1024 and owned['NanoCpus'] == 1_000_000_000, 'container bounds differ')
    require(type(owned['Pid']) is int and owned['Pid'] > 0 and owned['Created'] and owned['StartedAt'], 'actual container start missing')
    ports = owned['Ports']['6379/tcp']
    require(ports and all(port['HostIp'] == '127.0.0.1' and 0 < int(port['HostPort']) < 65536 for port in ports), 'owned port is not loopback')
    mounts = owned['Mounts']
    require(all(mount['Type'] == 'volume' and re.fullmatch('[0-9a-f]{64}', mount['Name']) for mount in mounts), 'unexpected persistent or bind mount')
    names = [mount['Name'] for mount in mounts]
    require(len(names) == len(set(names)) and names == item['volumeNames'], 'complete volume inventory differs')
    processes = item['processLedger']
    require(len(processes) == 2 + len(names), 'actual inventory/removal inspection count differs')
    require(all(process['exitVerified'] is True and process['stopFailure'] is None
                and type(process['pid']) is int and process['pid'] > 0 and process['birth'] and process['executable']
                for process in processes), 'owned inspection child exit/identity unverified')
    expected_clients = 5 if method == 'ConcurrentFreshConnections_FirstCommandAcquisitionHasOneFencedOwner' else (
        2 if method == 'FreshConnection_CompletedCheckpointReplaysExactResponseAndRejectsDifferentContent' else 1)
    clients = item['clients']
    require(len(clients) == expected_clients and len({c['identity'] for c in clients}) == expected_clients, 'actual client inventory differs')
    require(all(c['closed'] is True and c['disposed'] is True and c['cleanupFailure'] is None and c['createdUtc']
                and c['endpoint'].rsplit(':', 1)[-1] == ports[0]['HostPort'] for c in clients), 'client cleanup unverified')
    return item


def inspect_trx(raw, kind):
    require(kind in {'red', 'focused', 'full'} and len(raw) <= 16 * 1024 * 1024, 'bounded fixed result kind required')
    count = {'red': 1, 'focused': 16, 'full': 1562}[kind]
    root = ET.fromstring(raw)
    results = root.findall('./t:Results/t:UnitTestResult', NS)
    definitions = root.findall('./t:TestDefinitions/t:UnitTest', NS)
    entries = root.findall('./t:TestEntries/t:TestEntry', NS)
    require(len(results) == len(definitions) == len(entries) == count, 'actual count differs')
    ids = {r.attrib['testId'] for r in results}
    executions = {r.attrib['executionId'] for r in results}
    require(len(ids) == len(executions) == count and len({d.attrib['id'] for d in definitions}) == count, 'duplicate identity')
    joins = {(r.attrib['testId'], r.attrib['executionId']) for r in results}
    require(joins == {(e.attrib['testId'], e.attrib['executionId']) for e in entries}
            == {(d.attrib['id'], d.find('./t:Execution', NS).attrib['id']) for d in definitions}, 'compiled execution associations differ')
    summary = root.find('./t:ResultSummary', NS)
    require(summary is not None and summary.get('outcome') == ('Failed' if kind == 'red' else 'Completed'), 'summary differs')
    counters = summary.find('./t:Counters', NS).attrib
    expected = {key: str(count) if key in {'total', 'executed'} else ('1' if kind == 'red' and key == 'failed'
                else str(count) if kind != 'red' and key == 'passed' else '0') for key in COUNTERS}
    require(counters == expected, 'all sixteen actual counters differ')
    require(all(r.get('outcome') == ('Failed' if kind == 'red' else 'Passed') for r in results), 'actual test outcome differs')
    indexed = {r.attrib['testId']: r for r in results}
    target_methods = {}
    target_names = set()
    ledgers = []
    for definition in definitions:
        compiled = definition.findall('./t:TestMethod', NS)
        require(len(compiled) == 1, 'compiled TestMethod absent/duplicated')
        method = compiled[0]
        require(method.get('codeBase', '').replace('\\', '/').rsplit('/', 1)[-1] == DLL
                and definition.get('storage', '').replace('\\', '/').rsplit('/', 1)[-1] == DLL.lower(), 'compiled DLL/storage differs')
        result = indexed[definition.attrib['id']]
        require(definition.get('name') == result.get('testName'), 'definition/result display identity differs')
        if method.get('className') != CLASS:
            require(not result.get('testName', '').startswith(CLASS + '.'), 'foreign compiled class masquerades as target')
            continue
        name = method.get('name')
        require(name in METHODS and result.get('testName', '').startswith(CLASS + '.' + name), 'compiled target method differs')
        require(result.get('testName') not in target_names, 'duplicate target case display identity')
        target_names.add(result.get('testName'))
        if name == 'SupersededWorker_CannotChangeNewReservationOrItsLease':
            require(result.get('testName') in {CLASS + '.' + name + '(operation: "' + op + '")' for op in ['renew', 'release', 'unknown', 'complete']}, 'fencing case differs')
        else:
            require(result.get('testName') == CLASS + '.' + name, 'target result name differs')
        target_methods[name] = target_methods.get(name, 0) + 1
        if kind == 'red':
            message = result.findtext('./t:Output/t:ErrorInfo/t:Message', default='', namespaces=NS).strip()
            require(message == 'System.InvalidOperationException : Upload checkpoint changed during acquisition.', 'unrelated failure cannot qualify as RED')
            stack = result.findtext('./t:Output/t:ErrorInfo/t:StackTrace', default='', namespaces=NS)
            require('RedisUploadIdempotencyStore.AcquireAsync' in stack and 'RedisUploadIdempotencyStore.cs:line 28' in stack
                    and RED in stack, 'RED failing boundary differs')
        ledgers.append(resource_evidence(result.findtext('./t:Output/t:StdOut', default='', namespaces=NS), name))
    require(target_methods == ({RED: 1} if kind == 'red' else METHODS), 'required compiled target cases missing/duplicated')
    require(len({entry['containerId'] for entry in ledgers}) == len(ledgers)
            and len({entry['run'] for entry in ledgers}) == len(ledgers), 'reused resource identity')
    return {'kind': kind, 'actualCounters': counters, 'resourceLedgers': ledgers, 'nativeAcceptance': False, 'runtimeLockCycleProven': False}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--phase', required=True, choices=['red', 'green'])
    parser.add_argument('--focused', required=True)
    parser.add_argument('--full')
    parser.add_argument('--receipt', required=True)
    args = parser.parse_args()
    require((args.phase == 'green') == (args.full is not None), 'fixed phase/full association required')
    result = {'focused': inspect_trx(Path(args.focused).read_bytes(), 'red' if args.phase == 'red' else 'focused')}
    if args.full:
        result['full'] = inspect_trx(Path(args.full).read_bytes(), 'full')
    with Path(args.receipt).open('x', encoding='utf-8') as stream:
        json.dump(result, stream, indent=2)


if __name__ == '__main__':
    main()
