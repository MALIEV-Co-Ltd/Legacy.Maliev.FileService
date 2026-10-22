"""Actual ordinary TRX joins and diagnostic resource evidence; no native acceptance."""
import argparse
import json
from pathlib import Path
import xml.etree.ElementTree as ET
from sealed_source_capsule import parse_json

NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
METHOD = 'FreshClients_ImmediateConditionalAcquisitionVersusPingReadiness_RecordsActualOutcomes'
CLASS = 'Legacy.Maliev.FileService.Tests.Data.RedisBootstrapDiscriminatorTests'
DLL = 'Legacy.Maliev.FileService.Tests.dll'
RESULT_NAME = CLASS + '.' + METHOD
COUNTERS = {'total', 'executed', 'passed', 'failed', 'error', 'timeout', 'aborted', 'inconclusive',
            'passedButRunAborted', 'notRunnable', 'notExecuted', 'disconnected', 'warning', 'completed', 'inProgress', 'pending'}


def inspect_trx(raw, count):
    if len(raw) > 16 * 1024 * 1024:
        raise ValueError('TRX exceeds bound')
    root = ET.fromstring(raw)
    results = root.findall('./t:Results/t:UnitTestResult', NS)
    definitions = root.findall('./t:TestDefinitions/t:UnitTest', NS)
    entries = root.findall('./t:TestEntries/t:TestEntry', NS)
    if len(results) != count or len(definitions) != count or len(entries) != count:
        raise ValueError('actual count differs')
    if any(r.get('outcome') != 'Passed' for r in results):
        raise ValueError('actual test failed')
    ids = {r.attrib['testId'] for r in results}
    executions = {r.attrib['executionId'] for r in results}
    if len(ids) != count or len(executions) != count or {d.attrib['id'] for d in definitions} != ids:
        raise ValueError('duplicate or unmatched identity')
    result_joins = {(r.attrib['testId'], r.attrib['executionId']) for r in results}
    entry_joins = {(e.attrib['testId'], e.attrib['executionId']) for e in entries}
    definition_joins = {(d.attrib['id'], d.find('./t:Execution', NS).attrib['id']) for d in definitions}
    if result_joins != entry_joins or result_joins != definition_joins:
        raise ValueError('execution associations differ')
    summary = root.find('./t:ResultSummary', NS)
    if summary is None or summary.get('outcome') != 'Completed':
        raise ValueError('summary incomplete')
    counters = summary.find('./t:Counters', NS).attrib
    if set(counters) != COUNTERS or any(v != (str(count) if k in {'total', 'executed', 'passed'} else '0') for k, v in counters.items()):
        raise ValueError('actual counters differ')
    compiled = []
    for definition in definitions:
        methods = definition.findall('./t:TestMethod', NS)
        if len(methods) != 1:
            raise ValueError('compiled TestMethod absent or duplicated')
        method = methods[0]
        if method.get('className') == CLASS and method.get('name') == METHOD:
            codebase = method.get('codeBase', '').replace('\\', '/').rsplit('/', 1)[-1]
            storage = definition.get('storage', '').replace('\\', '/').rsplit('/', 1)[-1]
            if codebase != DLL or storage != DLL.lower() or definition.get('name') != RESULT_NAME:
                raise ValueError('compiled diagnostic DLL or definition name differs')
            compiled.append(definition)
    focused = [r for r in results if r.get('testName') == RESULT_NAME]
    if len(focused) != 1 or len(compiled) != 1 or focused[0].get('testId') != compiled[0].get('id'):
        raise ValueError('diagnostic identity absent or duplicated')
    text = focused[0].findtext('./t:Output/t:StdOut', default='', namespaces=NS)
    marker = 'REDIS_BOOTSTRAP_DIAGNOSTIC '
    lines = [line.split(marker, 1)[1] for line in text.splitlines() if marker in line]
    if len(lines) != 1:
        raise ValueError('diagnostic output absent or duplicated')
    diagnostic = parse_json(lines[0])
    inspect_diagnostic(diagnostic)
    return {'actualPassed': count, 'actualCounters': counters, 'diagnostic': diagnostic,
            'runtimeCauseProven': False, 'nativeAcceptance': False}


def inspect_diagnostic(diagnostic):
    if diagnostic['runtimeCauseProven'] is not False or diagnostic['productionChanged'] is not False:
        raise ValueError('unsupported acceptance claim')
    if diagnostic['cleanupFailures'] or not all(diagnostic[k] is True for k in ['containerRemoved', 'volumeInventoryComplete', 'volumesRemoved']):
        raise ValueError('resource absence unverified')
    resource = diagnostic['resource']
    if resource['Labels']['codex.run'] != diagnostic['run'] or resource['Labels']['codex.owner'] != 'file-issue103-discriminator':
        raise ValueError('resource ownership differs')
    if resource['Memory'] != 128 * 1024 * 1024 or resource['NanoCpus'] != 1_000_000_000:
        raise ValueError('resource bounds differ')
    mounts = {m['Name'] for m in resource['Mounts'] if m['Type'] == 'volume'}
    if mounts != set(diagnostic['volumeNames']) or len(mounts) != len(diagnostic['volumeNames']):
        raise ValueError('volume inventory differs')
    ports = resource['Ports']['6379/tcp']
    if not ports or any(p['HostIp'] != '127.0.0.1' for p in ports):
        raise ValueError('non-loopback port')
    processes = diagnostic['processLedger']
    if len(processes) < 2 or any(p['exitVerified'] is not True or p['stopFailure'] is not None for p in processes):
        raise ValueError('owned process exit unverified')
    trials = diagnostic['trials']
    if any(type(t['pair']) is not int or type(t['pingReadiness']) is not bool for t in trials):
        raise ValueError('trial identity types differ')
    if len(trials) != 8 or {(t['pair'], t['pingReadiness']) for t in trials} != {(pair, arm) for pair in range(4) for arm in (False, True)}:
        raise ValueError('eight actual counterbalanced trials required')
    for trial in trials:
        if not all(trial[k] is True for k in ['clientCreated', 'clientCleanupApplicable', 'clientClosed', 'clientDisposed']) or trial['clientCleanupFailure'] is not None:
            raise ValueError('client cleanup unverified')
        if len(trial['trace']) > 128:
            raise ValueError('trace exceeds bound')
        if trial['failure'] is None:
            if type(trial['acquired']) is not int or trial['acquired'] != 0 or trial['stage'] != 'acquire':
                raise ValueError('successful acquisition differs')
        elif trial['stage'] not in {'connect', 'ping', 'acquire'} or trial['acquired'] is not None or trial['failure'] not in {'RedisException', 'RedisTimeoutException', 'RedisConnectionException', 'TimeoutException', 'OperationCanceledException', 'TaskCanceledException'}:
            raise ValueError('invalid measured failure')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--focused', required=True)
    parser.add_argument('--full', required=True)
    parser.add_argument('--receipt', required=True)
    args = parser.parse_args()
    receipt = {'focused': inspect_trx(Path(args.focused).read_bytes(), 1),
               'full': inspect_trx(Path(args.full).read_bytes(), 1074)}
    with Path(args.receipt).open('x', encoding='utf-8') as stream:
        json.dump(receipt, stream, indent=2)


if __name__ == '__main__':
    main()
