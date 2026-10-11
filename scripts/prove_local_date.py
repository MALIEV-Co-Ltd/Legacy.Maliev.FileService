"""Ordinary hosted causal proof; no local SDK execution or real provider calls."""
import base64
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

CLASS = "Legacy.Maliev.FileService.Tests.Application.ObjectNamePolicyTests"
METHOD = "BuildFinalObjectName_DefaultPrefix_UsesMachineLocalDateWithoutChangingCustomPrefix"
CASES = [("2026-07-15T18:00:00Z", 7, "2026-7-16"), ("2026-07-31T18:00:00Z", 7, "2026-8-1"),
         ("2026-12-31T18:00:00Z", 7, "2027-1-1"), ("2026-07-15T02:00:00Z", -8, "2026-7-14"),
         ("2026-08-01T02:00:00Z", -8, "2026-7-31"), ("2027-01-01T02:00:00Z", -8, "2026-12-31"),
         ("2026-07-15T18:00:00Z", 0, "2026-7-15"), ("2026-08-01T02:00:00Z", 0, "2026-8-1"),
         ("2027-01-01T02:00:00Z", 0, "2027-1-1")]
OLD = "9bf31c5ab0f72ef1fcbd6afbde4d51da59ec6826"
CONTROLLER = "Legacy.Maliev.FileService.Application/Services/ObjectNamePolicy.cs"
TEST = "Legacy.Maliev.FileService.Tests/Application/ObjectNamePolicyTests.cs"
BASELINE_BLOB = "ef56c94c81e3f70285df9ec102ee070935990f18"
BASELINE_SHA256 = "581a73156f78913cff6acd86f375ddf9f777d6a0458959fd17e182e629a655db"
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
NONPASSING_COUNTERS = {"error", "timeout", "aborted", "inconclusive", "passedButRunAborted",
                      "notRunnable", "notExecuted", "disconnected", "warning", "completed", "inProgress", "pending"}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def trx_guid(value):
    require(isinstance(value, str) and re.fullmatch(
        r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", value),
        "Invalid TRX GUID identity")
    return value.lower()


def read_trx(path, strict_clean_run=True):
    tree = ET.parse(path)
    definitions = tree.findall("./t:TestDefinitions/t:UnitTest", NS)
    results = tree.findall("./t:Results/t:UnitTestResult", NS)
    entries = tree.findall("./t:TestEntries/t:TestEntry", NS)
    require(len(definitions) == len(results) == len(entries), "Definition/result/entry cardinality mismatch")
    identities, execution_ids = {}, set()
    for definition in definitions:
        method = definition.find("t:TestMethod", NS)
        execution = definition.find("t:Execution", NS)
        identity = trx_guid(definition.get("id"))
        require(identity and identity not in identities and method is not None and execution is not None, "Invalid definition identity")
        require(len(definition.findall("t:TestMethod", NS)) == 1 and len(definition.findall("t:Execution", NS)) == 1,
                "Duplicated definition method/execution")
        execution_id = trx_guid(execution.get("id"))
        require(execution_id and execution_id not in execution_ids, "Duplicate execution identity")
        execution_ids.add(execution_id)
        identities[identity] = (method.get("className", "").split(",", 1)[0].strip(), method.get("name"), definition.get("name"), execution_id)
    entry_ids, entry_executions = set(), set()
    for entry in entries:
        identity = trx_guid(entry.get("testId"))
        execution_id = trx_guid(entry.get("executionId"))
        require(identity in identities and identity not in entry_ids and execution_id not in entry_executions,
                "Invalid/duplicate entry join")
        require(execution_id == identities[identity][3], "Entry execution identity mismatch")
        entry_ids.add(identity)
        entry_executions.add(execution_id)
    require(entry_ids == set(identities) and entry_executions == execution_ids, "Entry/definition bijection mismatch")
    rows, seen = [], set()
    for result in results:
        identity = trx_guid(result.get("testId"))
        require(identity in identities and identity not in seen, "Invalid/duplicate result join")
        seen.add(identity)
        cls, method, name, execution_id = identities[identity]
        require(trx_guid(result.get("executionId")) == execution_id and result.get("testName") == name, "Result identity mismatch")
        rows.append({"class": cls, "method": method, "name": name, "outcome": result.get("outcome"),
                     "message": result.findtext("t:Output/t:ErrorInfo/t:Message", default="", namespaces=NS).strip(),
                     "stack": result.findtext("t:Output/t:ErrorInfo/t:StackTrace", default="", namespaces=NS),
                     "errorInfo": result.find("t:Output/t:ErrorInfo", NS) is not None})
    require(seen == entry_ids, "Result/entry bijection mismatch")
    summaries = tree.findall("./t:ResultSummary", NS)
    require(len(summaries) == 1, "Expected exactly one result summary")
    summary = summaries[0]
    counter_nodes = summary.findall("t:Counters", NS)
    require(len(counter_nodes) == 1, "Expected exactly one counters element")
    counters = counter_nodes[0]
    require(not summary.findall(".//t:ErrorInfo", NS), "Run summary contains ErrorInfo")
    require(({"total", "executed", "passed", "failed"} | NONPASSING_COUNTERS).issubset(counters.attrib),
            "Missing mandatory counters")
    expected = {"total": len(rows), "executed": len(rows), "passed": sum(r["outcome"] == "Passed" for r in rows),
                "failed": sum(r["outcome"] == "Failed" for r in rows)}
    for identity, value in counters.attrib.items():
        require(int(value) == expected.get(identity, 0), "Counter mismatch/skip/timeout")
    require(summary.get("outcome") == ("Failed" if expected["failed"] else "Completed"), "Summary outcome mismatch")
    if strict_clean_run:
        require(expected["failed"] == 0 and summary.get("outcome") == "Completed",
                "Focus run must be clean Completed")
        for info in tree.findall(".//t:RunInfo", NS):
            require(info.get("outcome") in {"Passed", "Completed", "Information"}
                    and not info.findall(".//t:ErrorInfo", NS), "Adverse focus RunInfo")
    return rows


def verify_focus(path, baseline):
    rows = read_trx(path, strict_clean_run=not baseline)
    return verify_focus_rows(rows, baseline)


def case_name(instant, offset, date):
    return CLASS + "." + METHOD + f'(utcInstant: "{instant}", offsetHours: {offset}, expectedDate: "{date}")'


def new_identities():
    return Counter((CLASS, METHOD, case_name(*case)) for case in CASES)


def prior_inventory():
    return json.loads(Path("docs/local-date-prior1567-inventory.json").read_text())


def verify_focus_rows(rows, baseline, inventory=None):
    if inventory is None:
        inventory = prior_inventory()
    prior = Counter({(r["className"], r["method"], r["testName"]): r["executions"] for r in inventory["rows"] if r["className"] == CLASS})
    require(sum(prior.values()) == 5, "Accepted focus must contain five prior executions")
    require(Counter((r["class"], r["method"], r["name"]) for r in rows) == prior + new_identities(), "Focus roster differs from exact fourteen")
    cases = {case_name(*case): case for case in CASES}
    for row in rows:
        case = cases.get(row["name"])
        failed = baseline and case is not None and case[1] != 0
        require(row["outcome"] == ("Failed" if failed else "Passed"), "Unexpected focus outcome")
        if failed:
            instant, offset, date = case
            utc = datetime.fromisoformat(instant.replace("Z", "+00:00"))
            actual_date = f"{utc.year}-{utc.month}-{utc.day}"
            suffix = "/01234567-89ab-cdef-0123-456789abcdef/part.stl"
            message = f"Legacy upload date path mismatch; expected path=uploads/{date}{suffix}; actual path=uploads/{actual_date}{suffix}."
            require(row["message"] == message and CLASS + "." + METHOD in row["stack"], "Failure is not exact local-date path mismatch")
        else:
            require(not row["errorInfo"], "Passing control contains error")
    return rows


UPLOAD_COORDINATE_INVENTORY = "docs/upload-coordinate25-inventory.json"
UPLOAD_COORDINATE_INVENTORY_SHA256 = "7f5a674e1c538bc5cd989e4001f1674aab4717382175175c7d09e7e82ac87972"
UPLOAD_COORDINATE_TEST = "Legacy.Maliev.FileService.Tests/Api/FileUploadRouteHttpAdmissionTests.cs"
UPLOAD_COORDINATE_TEST_SHA256 = "8eee7c6b4b839e9dfb90451a9302834e6552670707e84d161dc6eca116522662"


def reviewed_upload_additions(inventory_path=None, source_path=None):
    raw = Path(inventory_path or UPLOAD_COORDINATE_INVENTORY).read_bytes()
    require(digest(raw) == UPLOAD_COORDINATE_INVENTORY_SHA256, "Reviewed upload inventory changed")
    additions = json.loads(raw)
    require(digest(Path(source_path or UPLOAD_COORDINATE_TEST).read_bytes()) == UPLOAD_COORDINATE_TEST_SHA256,
            "Reviewed upload test source changed")
    require(additions["sourcePath"] == UPLOAD_COORDINATE_TEST
            and additions["sourceSha256"] == UPLOAD_COORDINATE_TEST_SHA256
            and additions["baselineFullCount"] == 1576 and additions["additionalCases"] == 25
            and additions["expectedFullCount"] == 1601, "Unexpected reviewed upload scope")
    identities = Counter()
    methods = Counter()
    for row in additions["rows"]:
        require(row["class"] == "Legacy.Maliev.FileService.Tests.Api.FileUploadRouteHttpAdmissionTests"
                and row["executions"] == 1, "Unexpected reviewed upload identity")
        identity = (row["class"], row["method"], row["testName"])
        require(identity not in identities, "Duplicate reviewed upload identity")
        identities[identity] = 1
        methods[row["method"]] += 1
    require(methods == Counter({"RepeatedUploadCoordinate_PreservesActualAuthorityBeforeStorageOrReplay": 12,
                               "RepeatedUploadCoordinate_PreservesAuthenticationAndWriteGatePrecedence": 8,
                               "SingleUploadCoordinates_PreserveOptionalPathMultipartBytesAndCreatedWireShape": 3,
                               "RepeatedUploadCoordinate_PreservesExistingFileValidationResponse": 2}),
            "Reviewed upload case groups changed")
    return identities


RECONCILE_GENERATION_INVENTORY = "docs/reconcile-generation6-inventory.json"
RECONCILE_GENERATION_INVENTORY_SHA256 = "fca5af6bcc2a7e65a25679e1ef16fb4ce19dd5e8388b554aa81fbf9b3b5b02e2"
RECONCILE_GENERATION_TEST = "Legacy.Maliev.FileService.Tests/Application/FileApplicationServiceTests.cs"
RECONCILE_GENERATION_TEST_SHA256 = "f95d5660453e415061b12fc3923caeaa761bae1bef4842387487b1de373f7725"


def reviewed_reconcile_additions(inventory_path=None, source_path=None):
    raw = Path(inventory_path or RECONCILE_GENERATION_INVENTORY).read_bytes()
    require(digest(raw) == RECONCILE_GENERATION_INVENTORY_SHA256, "Reviewed reconciliation inventory changed")
    additions = json.loads(raw)
    require(digest(Path(source_path or RECONCILE_GENERATION_TEST).read_bytes()) == RECONCILE_GENERATION_TEST_SHA256,
            "Reviewed reconciliation test source changed")
    require(additions["sourcePath"] == RECONCILE_GENERATION_TEST
            and additions["sourceSha256"] == RECONCILE_GENERATION_TEST_SHA256
            and additions["baselineFullCount"] == 1601 and additions["additionalCases"] == 6
            and additions["expectedFullCount"] == 1607, "Unexpected reviewed reconciliation scope")
    identities = Counter()
    methods = Counter()
    for row in additions["rows"]:
        require(row["class"] == "Legacy.Maliev.FileService.Tests.Application.FileApplicationServiceTests"
                and row["executions"] == 1, "Unexpected reviewed reconciliation identity")
        identity = (row["class"], row["method"], row["testName"])
        require(identity not in identities, "Duplicate reviewed reconciliation identity")
        identities[identity] = 1
        methods[row["method"]] += 1
    require(methods == Counter({"ReconcileUploadAsync_ReplacementAfterEvidenceRead_SignsOnlyAcknowledgedGeneration": 2,
                               "ReconcileUploadAsync_StableEvidence_UsesGenerationSigner": 1,
                               "ReconcileUploadAsync_NonpositiveJournalGeneration_RefusesBeforeReadingOrSigning": 2,
                               "ReconcileUploadAsync_GenerationSignerFailure_PreservesCauseAndRecoveryObjects": 1}),
            "Reviewed reconciliation case groups changed")
    return identities


STARTUP_OBSERVATION_INVENTORY = "docs/startup-observation7-inventory.json"
STARTUP_OBSERVATION_INVENTORY_SHA256 = "f10a21a02d7b1d0b2c06362b2de2f8b9d3688a1bd4ab3f25e4ba2596eb4ca8a6"
STARTUP_OBSERVATION_TEST = "Legacy.Maliev.FileService.Tests/Startup/FileStartupBoundaryTests.cs"
STARTUP_OBSERVATION_TEST_SHA256 = "a471b810b4e67bc922dded9c6ba010a0f64334d145dea6021b4c3e41ebc7b03d"
STARTUP_OBSERVATION_METHODS = (
    "ExecutableObservation_PresentModuleRecordsObservedIdentity",
    "ExecutableObservation_AbsentModuleRequiresVerifiedExit",
    "ExecutableObservation_AbsentLiveModuleFailsWithoutLaunchFallback",
    "ExecutableObservation_ExitQueryFailurePreservesCause",
    "ExecutableObservation_ModuleQueryFailurePreservesCause",
    "ExecutableObservation_ExitBetweenObservationsPreventsTerminationDecision",
    "ExecutableObservation_PrimaryPrecedesCleanupObservationFailure",
)


def reviewed_startup_additions(inventory_path=None, source_path=None):
    raw = Path(inventory_path or STARTUP_OBSERVATION_INVENTORY).read_bytes()
    require(digest(raw) == STARTUP_OBSERVATION_INVENTORY_SHA256, "Reviewed startup inventory changed")
    additions = json.loads(raw)
    require(digest(Path(source_path or STARTUP_OBSERVATION_TEST).read_bytes()) == STARTUP_OBSERVATION_TEST_SHA256,
            "Reviewed startup test source changed")
    require(additions["sourcePath"] == STARTUP_OBSERVATION_TEST
            and additions["sourceSha256"] == STARTUP_OBSERVATION_TEST_SHA256
            and additions["baselineFullCount"] == 1607 and additions["additionalCases"] == 7
            and additions["expectedFullCount"] == 1614, "Unexpected reviewed startup scope")
    cls = "Legacy.Maliev.FileService.Tests.Startup.FileStartupBoundaryTests"
    expected = Counter((cls, method, cls + "." + method) for method in STARTUP_OBSERVATION_METHODS)
    actual = Counter()
    for row in additions["rows"]:
        identity = (row["class"], row["method"], row["testName"])
        require(row["executions"] == 1 and identity not in actual, "Duplicate/invalid startup identity")
        actual[identity] = 1
    require(actual == expected, "Reviewed startup identities changed")
    return actual


DELETE_LITERAL_INVENTORY = "docs/delete-literal23-inventory.json"
DELETE_LITERAL_INVENTORY_SHA256 = "11252f406b1c0ed1377f842ec69b3e1827b5463d6e20855e446ac2214fd20c3a"


def reviewed_delete_additions(inventory_path=None, source_root=None):
    raw = Path(inventory_path or DELETE_LITERAL_INVENTORY).read_bytes()
    require(digest(raw) == DELETE_LITERAL_INVENTORY_SHA256, "Reviewed literal delete inventory changed")
    additions = json.loads(raw)
    require(additions["baselineFullCount"] == 1614 and additions["additionalCases"] == 23
            and additions["expectedFullCount"] == 1637, "Unexpected literal delete scope")
    expected_sources = {
        "Legacy.Maliev.FileService.Tests/Application/FileDeleteLiteralIdentityTests.cs",
        "Legacy.Maliev.FileService.Tests/Integration/LegacyLiteralDeleteHttpBoundaryTests.cs",
    }
    require(len(additions["sources"]) == 2 and {row["path"] for row in additions["sources"]} == expected_sources,
            "Unexpected literal delete source scope")
    for source in additions["sources"]:
        require(digest((Path(source_root or ".") / source["path"]).read_bytes()) == source["sha256"],
                "Reviewed literal delete test source changed")
    identities = Counter()
    for row in additions["rows"]:
        require(row["class"] in {
            "Legacy.Maliev.FileService.Tests.Application.FileDeleteLiteralIdentityTests",
            "Legacy.Maliev.FileService.Tests.Integration.LegacyLiteralDeleteHttpBoundaryTests",
        } and row["executions"] == 1 and row["testName"] == row["class"] + "." + row["method"],
                "Unexpected literal delete identity")
        identity = (row["class"], row["method"], row["testName"])
        require(identity not in identities, "Duplicate literal delete identity")
        identities[identity] = 1
    require(sum(identities.values()) == 23
            and Counter(identity[0] for identity in identities) == Counter({
                "Legacy.Maliev.FileService.Tests.Application.FileDeleteLiteralIdentityTests": 20,
                "Legacy.Maliev.FileService.Tests.Integration.LegacyLiteralDeleteHttpBoundaryTests": 3,
            }), "Unexpected literal delete case groups")
    return identities

SIGNED_DOWNLOAD_INVENTORY = "docs/signed-download-async3-inventory.json"
SIGNED_DOWNLOAD_INVENTORY_SHA256 = "152d869771086d16a527d2cb9bb94518e86603ec37ab5e1299914362a4d95a68"
SIGNED_DOWNLOAD_CLASS = "Legacy.Maliev.FileService.Tests.Data.LegacySignedDownloadAsyncBoundaryTests"
SIGNED_DOWNLOAD_METHODS = {
    "AsyncSigning_WaitsForSignatureAndPreservesLiteralThaiIdentity",
    "AsyncSigning_CancellationReachesPendingSignatureAndSettlesCaller",
    "AsyncGenerationSigning_BindsGenerationAndSevenDayExpiryWithoutChangingObjectIdentity",
}


def reviewed_signed_download_additions(inventory_path=None, source_root=None):
    raw = Path(inventory_path or SIGNED_DOWNLOAD_INVENTORY).read_bytes()
    require(digest(raw) == SIGNED_DOWNLOAD_INVENTORY_SHA256, "Reviewed signed download inventory changed")
    additions = json.loads(raw)
    require(additions["baselineFullCount"] == 1637 and additions["additionalCases"] == 3
            and additions["expectedFullCount"] == 1640, "Unexpected signed download scope")
    source = "Legacy.Maliev.FileService.Tests/Data/LegacySignedDownloadAsyncBoundaryTests.cs"
    require(len(additions["sources"]) == 1 and additions["sources"][0]["path"] == source,
            "Unexpected signed download source scope")
    require(digest((Path(source_root or ".") / source).read_bytes()) == additions["sources"][0]["sha256"],
            "Reviewed signed download source changed")
    actual = Counter()
    for row in additions["rows"]:
        identity = (row["class"], row["method"], row["testName"])
        require(row["executions"] == 1 and identity not in actual, "Duplicate signed download identity")
        actual[identity] = 1
    expected = Counter((SIGNED_DOWNLOAD_CLASS, method, SIGNED_DOWNLOAD_CLASS + "." + method)
                       for method in SIGNED_DOWNLOAD_METHODS)
    require(actual == expected, "Reviewed signed download identities changed")
    return actual


def verify_full(path, inventory, include_literal_delete=True, include_signed_download=True):
    rows = read_trx(path, strict_clean_run=True)
    additions = reviewed_upload_additions()
    reconcile_additions = reviewed_reconcile_additions()
    startup_additions = reviewed_startup_additions()
    literal_additions = reviewed_delete_additions() if include_literal_delete else Counter()
    signed_additions = reviewed_signed_download_additions() if include_literal_delete and include_signed_download else Counter()
    expected_count = 1640 if signed_additions else 1637 if include_literal_delete else 1614
    require(len(rows) == expected_count and all(r["outcome"] == "Passed" and not r["errorInfo"] for r in rows), "Full suite must pass exact reviewed roster")
    prior = Counter({(r["className"], r["method"], r["testName"]): r["executions"] for r in inventory["rows"]})
    expected = prior + new_identities() + additions + reconcile_additions + startup_additions + literal_additions + signed_additions
    require(sum(prior.values()) == 1567 and sum(expected.values()) == expected_count
            and Counter((r["class"], r["method"], r["name"]) for r in rows) == expected,
            "Full suite changed accepted1637 or reviewed signed download identities")
    verify_focus_rows([r for r in rows if r["class"] == CLASS], False, inventory)
    return rows


def build_ok(log):
    text = Path(log).read_text()
    require("Build succeeded." in text and "0 Warning(s)" in text and "0 Error(s)" in text
            and not re.search(r"\b(?:warning|error) [A-Z]+\d+", text, re.I), "Build not zero-warning/error")


def wait_group_absent(group_id, probe=None, now=time.monotonic, pause=time.sleep, observations=None):
    if probe is None:
        probe = os.killpg
    started = now()
    deadline = started + 15
    while True:
        observed = {"elapsedSeconds": now() - started, "signal": 0, "groupId": group_id}
        if observations is not None:
            observations.append(observed)
        try:
            probe(group_id, 0)
        except ProcessLookupError:
            observed["state"] = "absent"
            return True
        except BaseException as exception:
            observed["state"] = "error"
            observed["errorType"] = type(exception).__name__
            raise
        observed["state"] = "present"
        if now() >= deadline:
            return False
        pause(0.1)


def record_group_exit(record, receipt, commands, probe=None, now=time.monotonic, pause=time.sleep):
    record["groupObservationStartedUtc"] = datetime.now(timezone.utc).isoformat()
    record["groupObservations"] = []
    try:
        record["ownedTimeoutGroupAbsent"] = wait_group_absent(
            record["pid"], probe, now, pause, record["groupObservations"])
        require(record["ownedTimeoutGroupAbsent"], "Owned command group did not exit; do not start another worker")
    finally:
        record["groupObservationEndedUtc"] = datetime.now(timezone.utc).isoformat()
        record["endedUtc"] = datetime.now(timezone.utc).isoformat()
        receipt.write_text(json.dumps(commands, indent=2) + "\n")


def verify_baseline_identity(commit, blob_identity, data):
    require(commit == OLD, "Fetched baseline commit differs from immutable pin")
    require(blob_identity == BASELINE_BLOB and digest(data) == BASELINE_SHA256,
            "Accepted baseline policy blob/hash mismatch")
    return data


def fetch_baseline(output):
    timeout = shutil.which("timeout")
    git = shutil.which("git")
    require(timeout and git, "Missing bounded provenance tools")
    command = [timeout, "--signal=TERM", "--kill-after=5s", "30s", git, "fetch", "--no-tags",
               "--no-recurse-submodules", "--depth=1",
               "https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.FileService.git", OLD]
    record = {"name": "immutable-baseline-fetch", "command": command,
              "startedUtc": datetime.now(timezone.utc).isoformat()}
    with (output / "baseline-fetch.log").open("wb") as log:
        with subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT) as process:
            record["pid"] = process.pid
            stat = Path(f"/proc/{process.pid}/stat").read_text()
            record["kernelStartTicks"] = stat[stat.rfind(")") + 2:].split()[19]
            record["executable"] = os.readlink(f"/proc/{process.pid}/exe")
            record["returnCode"] = process.wait()
            record["exitVerified"] = process.poll() is not None
            record_group_exit(record, output / "baseline-fetch-custody.json", [record])
    require(record["returnCode"] == 0, "Immutable baseline fetch failed")
    commit = subprocess.check_output([git, "rev-parse", "FETCH_HEAD"], timeout=20).decode().strip()
    blob_identity = subprocess.check_output([git, "rev-parse", OLD + ":" + CONTROLLER], timeout=20).decode().strip()
    data = subprocess.check_output([git, "show", OLD + ":" + CONTROLLER], timeout=20)
    original = verify_baseline_identity(commit, blob_identity, data)
    (output / "baseline-provenance.json").write_text(json.dumps(
        {"commit": commit, "blob": blob_identity, "sha256": digest(original)}, indent=2) + "\n")
    return original


DELETE_BASELINE_COMMIT = "1e0844ec72974ad1905b45461fef33e22f403640"
DELETE_SERVICE = "Legacy.Maliev.FileService.Application/Services/FileApplicationService.cs"
DELETE_BASELINE_BLOB = "9da9db6f483a9099daa44383f1c25a9eec16b73e"
DELETE_BASELINE_SHA256 = "78460169e33179b87cd97c6eac03aab44148e920f640f500d8a11fca51344cc7"
DELETE_UNIT_FAILURES = {
    "Delete_LeadingSpace_PreservesLiteralIdentity",
    "Delete_TrailingSpace_PreservesLiteralIdentity",
    "Delete_Backslash_PreservesLiteralIdentity",
    "Delete_LeadingSlash_PreservesLiteralIdentity",
    "Delete_RepeatedSlash_PreservesLiteralIdentity",
    "Delete_TrailingSlash_PreservesLiteralIdentity",
    "Delete_ControlPrefix_RejectsBeforeBoundaryCalls",
    "Delete_ControlSuffix_RejectsBeforeBoundaryCalls",
    "Delete_MissingMetadata_PreservesMetadata",
    "Delete_ProviderFailure_PreservesMetadata",
}
DELETE_HTTP_FAILURES = {
    "LiteralOnly_DeletesExactObjectAndMetadata",
    "LiteralAndDecoy_DeletesLiteralAndRetainsDecoy",
    "MissingLiteralWithDecoy_LeavesProviderAndMetadataUntouched",
}


def verify_delete_baseline_identity(commit, blob, data):
    require(commit == DELETE_BASELINE_COMMIT and blob == DELETE_BASELINE_BLOB
            and digest(data) == DELETE_BASELINE_SHA256, "Delete baseline identity differs from immutable pin")
    return data


def delete_http_failure_message(method, coordinate_id="0123456789abcdef0123456789abcdef"):
    # Source-derived xunit.assert 2.9.3 formatting; this is not runtime evidence.
    if method != "LiteralAndDecoy_DeletesLiteralAndRetainsDecoy":
        expected, actual = (("NoContent", "BadRequest") if method ==
            "LiteralOnly_DeletesExactObjectAndMetadata" else ("BadRequest", "NoContent"))
        return f"Assert.Equal() Failure: Values differ\nExpected: {expected}\nActual:   {actual}"
    normalized = "orders/" + coordinate_id + "/ชิ้นงาน-cafe\u0301.step"
    literal = "  " + normalized + "  "
    preview = lambda value: '"' + value[:50] + '"···'
    pointer = " " * 24
    return ("Assert.Equal() Failure: Collections differ\n" + pointer + "↓ (pos 0)\n"
            + "Expected: string[]     [" + preview(literal) + "]\n"
            + "Actual:   List<string> [" + preview(normalized) + "]\n"
            + pointer + "↑ (pos 0)")


def verify_delete_focus_rows(rows, baseline):
    expected = reviewed_delete_additions()
    require(len(rows) == 23 and Counter((r["class"], r["method"], r["name"]) for r in rows) == expected,
            "Delete focus changed frozen23 identities")
    failures = 0
    for row in rows:
        unit = row["class"].endswith(".FileDeleteLiteralIdentityTests")
        failed = baseline and row["method"] in (DELETE_UNIT_FAILURES if unit else DELETE_HTTP_FAILURES)
        require(row["outcome"] == ("Failed" if failed else "Passed"), "Unexpected delete causal outcome")
        if failed:
            failures += 1
            require(row["errorInfo"], "Delete baseline failure lacks ErrorInfo")
            if unit:
                helper = ("AssertRejectedAsync" if "Control" in row["method"] else
                          "AssertFailureAsync" if row["method"] in {
                              "Delete_MissingMetadata_PreservesMetadata",
                              "Delete_ProviderFailure_PreservesMetadata"} else "AssertLiteralAsync")
                require(row["class"] + "." + helper in row["stack"],
                        "Delete baseline failure lacks reviewed helper stack")
                require("Moq.MockException" in row["message"] and "ExistsAsync" in row["message"],
                        "Delete baseline did not fail at exact metadata coordinate boundary")
            else:
                stage = 52 if row["method"] == "LiteralAndDecoy_DeletesLiteralAndRetainsDecoy" else 51
                require(row["class"] + ".AssertDeleteAsync" in row["stack"] and
                        f"LegacyLiteralDeleteHttpBoundaryTests.cs:line {stage}" in row["stack"],
                        "Delete HTTP baseline failed at wrong assertion stage")
                message = row["message"].replace("\r\n", "\n")
                coordinate = re.search(r'Expected: string\[\]     \["  orders/([0-9a-f]{32})/', message)
                require(message == delete_http_failure_message(row["method"],
                        coordinate.group(1) if coordinate else "0123456789abcdef0123456789abcdef"),
                        "Delete HTTP baseline failure is not the expected coordinate/status assertion")
        else:
            require(not row["errorInfo"], "Delete passing control contains ErrorInfo")
    require(failures == (13 if baseline else 0), "Delete baseline failure count differs")
    return rows


def fetch_delete_baseline(output):
    timeout, git = shutil.which("timeout"), shutil.which("git")
    require(timeout and git, "Missing bounded delete provenance tools")
    command = [timeout, "--signal=TERM", "--kill-after=5s", "30s", git, "fetch", "--no-tags",
               "--no-recurse-submodules", "--depth=1",
               "https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.FileService.git", DELETE_BASELINE_COMMIT]
    record = {"name": "immutable-delete-baseline-fetch", "command": command,
              "startedUtc": datetime.now(timezone.utc).isoformat()}
    with (output / "baseline-fetch.log").open("wb") as log:
        with subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT) as process:
            record["pid"] = process.pid
            stat = Path(f"/proc/{process.pid}/stat").read_text()
            record["kernelStartTicks"] = stat[stat.rfind(")") + 2:].split()[19]
            record["executable"] = os.readlink(f"/proc/{process.pid}/exe")
            record["returnCode"] = process.wait()
            record["exitVerified"] = process.poll() is not None
            record_group_exit(record, output / "baseline-fetch-custody.json", [record])
    require(record["returnCode"] == 0, "Immutable delete baseline fetch failed")
    commit = subprocess.check_output([git, "rev-parse", "FETCH_HEAD"], timeout=20).decode().strip()
    blob = subprocess.check_output([git, "rev-parse", DELETE_BASELINE_COMMIT + ":" + DELETE_SERVICE], timeout=20).decode().strip()
    data = subprocess.check_output([git, "show", DELETE_BASELINE_COMMIT + ":" + DELETE_SERVICE], timeout=20)
    original = verify_delete_baseline_identity(commit, blob, data)
    (output / "baseline-provenance.json").write_text(json.dumps(
        {"commit": commit, "blob": blob, "sha256": digest(original)}, indent=2) + "\n")
    return original


def delete_correction_disabled(corrected):
    helper = b'''    private string RequireExistingObjectIdentity(string objectName)
    {
        var safetyName = names.RequireObjectName(objectName);
        RequireGenericObject(safetyName);
        if (objectName.Any(char.IsControl))
        {
            throw new FileUploadValidationException("Object name is invalid");
        }

        return objectName;
    }

'''
    call = b"objectName = RequireExistingObjectIdentity(objectName);"
    require(corrected.count(helper) == 1 and corrected.count(call) == 1,
            "Delete correction block/call count differs")
    disabled = corrected.replace(helper, b"").replace(call,
        b"objectName = names.RequireObjectName(objectName);\n        RequireGenericObject(objectName);")
    require(digest(disabled) == DELETE_BASELINE_SHA256, "Delete correction-disabled source differs from immutable baseline")
    return disabled


def verify_delete_focus(path, baseline):
    return verify_delete_focus_rows(read_trx(path, strict_clean_run=not baseline), baseline)


def prove_delete_causal(repo, output, run, build, tests, head, inventory):
    output.mkdir(exist_ok=False)
    service = repo / DELETE_SERVICE
    corrected = service.read_bytes()
    additions = json.loads(Path(DELETE_LITERAL_INVENTORY).read_text())
    frozen = {row["path"]: (repo / row["path"]).read_bytes() for row in additions["sources"]}
    reviewed_delete_additions()
    require(subprocess.check_output(["git", "show", head + ":" + DELETE_SERVICE], timeout=20) == corrected,
            "Delete candidate source differs from proposed head")
    original = fetch_delete_baseline(output)
    require(delete_correction_disabled(corrected) == original, "Delete disabled source is not exact accepted baseline")
    focus = tests + ["--filter", "FullyQualifiedName~FileDeleteLiteralIdentityTests|FullyQualifiedName~LegacyLiteralDeleteHttpBoundaryTests"]
    error = None
    try:
        require(run("delete-candidate-focus", focus + ["--logger", "trx;LogFileName=candidate.trx", "--results-directory", str(output)], 120, output) == 0,
                "Delete candidate focus failed")
        verify_delete_focus(output / "candidate.trx", False)
        service.write_bytes(original)
        require(run("delete-baseline-build", build, 180, output) == 0, "Delete baseline build failed")
        build_ok(output / "delete-baseline-build.log")
        require(service.read_bytes() == original and all((repo / path).read_bytes() == data for path, data in frozen.items()),
                "Delete baseline source/tests changed")
        require(run("delete-baseline-focus", focus + ["--logger", "trx;LogFileName=baseline.trx", "--results-directory", str(output)], 120, output) == 1,
                "Delete baseline exit is not exactly one")
        verify_delete_focus(output / "baseline.trx", True)
    except BaseException as exception:
        error = exception
    finally:
        service.write_bytes(corrected)
        require(service.read_bytes() == corrected and all((repo / path).read_bytes() == data for path, data in frozen.items()),
                "Delete corrected source/test restore failed")
    if error:
        raise error
    require(run("delete-restored-build", build, 180, output) == 0, "Delete restored build failed")
    build_ok(output / "delete-restored-build.log")
    require(run("delete-restored-full", tests + ["--logger", "trx;LogFileName=restored-full.trx", "--results-directory", str(output)], 600, output) == 0,
            "Delete restored full suite failed")
    restored = verify_full(output / "restored-full.trx", inventory)
    require(service.read_bytes() == corrected and all((repo / path).read_bytes() == data for path, data in frozen.items()),
            "Delete final source custody changed")
    receipt = {"head": head, "baselineCommit": DELETE_BASELINE_COMMIT, "baselineBlob": DELETE_BASELINE_BLOB,
               "baselineServiceSha256": digest(original), "candidateServiceSha256": digest(corrected),
               "immutableTests": {path: digest(data) for path, data in frozen.items()},
               "baselineTenControlsPassedThirteenExpectedFailures": True, "candidate23Passed": True,
               "restoredFullPassed": len(restored), "sourceRestored": True,
               "rawFiles": {p.name: digest(p.read_bytes()) for p in output.iterdir() if p.suffix in {".trx", ".log"}}}
    (output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")

def main():
    require(sys.platform == "linux" and os.environ.get("GITHUB_ACTIONS") == "true", "Hosted ordinary runner only; no local SDK custody")
    root = Path(sys.argv[1]).resolve()
    repo = Path.cwd()
    require(root == repo / "runner-results", "Unexpected evidence root")
    output = root / "local-date-causal"
    output.mkdir(exist_ok=False)
    controller, test = repo / CONTROLLER, repo / TEST
    corrected, test_bytes = controller.read_bytes(), test.read_bytes()
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], timeout=20).decode().strip()
    require(subprocess.check_output(["git", "show", head + ":" + CONTROLLER], timeout=20) == corrected
            and subprocess.check_output(["git", "show", head + ":" + TEST], timeout=20) == test_bytes, "Dirty source inputs")
    original = fetch_baseline(output)
    require(corrected.count(b"timeProvider.GetLocalNow()") == 1 and
            corrected.replace(b"timeProvider.GetLocalNow()", b"timeProvider.GetUtcNow()") == original,
            "Correction-disabled policy is not exact accepted source")
    inventory = json.loads((repo / "docs/local-date-prior1567-inventory.json").read_text())
    original_full = [p for p in root.rglob("*.trx") if output not in p.parents]
    require(len(original_full) == 1, "Expected one original full-suite TRX")
    verify_full(original_full[0], inventory)
    commands = []
    dotnet, timeout = shutil.which("dotnet"), shutil.which("timeout")
    require(dotnet and timeout, "Missing ordinary runner tools")

    def run(name, arguments, seconds, evidence_output=None):
        directory = output if evidence_output is None else evidence_output
        available = next(line.split()[1] for line in Path("/proc/meminfo").read_text().splitlines() if line.startswith("MemAvailable:"))
        require(int(available) >= 4194304, "Fixed 4GiB memory admission blocked")
        command = [timeout, "--signal=TERM", "--kill-after=15s", str(seconds) + "s", dotnet, *arguments]
        record = {"name": name, "command": command, "startedUtc": datetime.now(timezone.utc).isoformat()}
        commands.append(record)
        with (directory / (name + ".log")).open("wb") as log:
            # GNU timeout owns the bounded process group; no detached SDK workers or imported job roles.
            with subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT) as process:
                record["pid"] = process.pid
                stat = Path(f"/proc/{process.pid}/stat").read_text()
                record["kernelStartTicks"] = stat[stat.rfind(")") + 2:].split()[19]
                record["executable"] = os.readlink(f"/proc/{process.pid}/exe")
                record["returnCode"] = process.wait()
                record["exitVerified"] = process.poll() is not None
                record_group_exit(record, directory / "command-custody.json", commands)
        return record["returnCode"]

    build = ["build", "Legacy.Maliev.FileService.slnx", "--configuration", "Release", "--no-restore", "--disable-build-servers",
             "-p:UseLocalMalievDependencies=true", "-p:GITHUB_ACTIONS=false", "-p:UseSharedCompilation=false", "-nodeReuse:false", "-warnaserror"]
    tests = ["test", "Legacy.Maliev.FileService.Tests/Legacy.Maliev.FileService.Tests.csproj", "--configuration", "Release",
             "--no-build", "--no-restore", "--disable-build-servers", "-p:UseLocalMalievDependencies=true", "-p:GITHUB_ACTIONS=false", "-p:VSTestCollect="]
    error = None
    try:
        require(run("candidate-focus", tests + ["--filter", "FullyQualifiedName~ObjectNamePolicyTests", "--logger", "trx;LogFileName=candidate.trx", "--results-directory", str(output)], 120) == 0, "Candidate focus failed")
        verify_focus(output / "candidate.trx", False)
        controller.write_bytes(original)
        require(run("baseline-build", build, 180) == 0, "Baseline build failed")
        build_ok(output / "baseline-build.log")
        require(test.read_bytes() == test_bytes and controller.read_bytes() == original, "Baseline source changed")
        require(run("baseline-focus", tests + ["--filter", "FullyQualifiedName~ObjectNamePolicyTests", "--logger", "trx;LogFileName=baseline.trx", "--results-directory", str(output)], 120) == 1, "Baseline exit is not exactly one")
        verify_focus(output / "baseline.trx", True)
    except BaseException as exception:
        error = exception
    finally:
        controller.write_bytes(corrected)
        require(controller.read_bytes() == corrected and test.read_bytes() == test_bytes, "Corrected source restore failed")
        (output / "command-custody.json").write_text(json.dumps(commands, indent=2) + "\n")
    # A failed baseline still restores source; it cannot be treated as a green causal proof.
    if error:
        raise error
    require(run("restored-build", build, 180) == 0, "Restored build failed")
    build_ok(output / "restored-build.log")
    require(run("restored-full", tests + ["--logger", "trx;LogFileName=restored-full.trx", "--results-directory", str(output)], 600) == 0, "Restored full suite failed")
    restored = verify_full(output / "restored-full.trx", inventory)
    require(controller.read_bytes() == corrected and test.read_bytes() == test_bytes, "Final source identity changed")
    receipt = {"head": head, "baselineControllerSource": OLD, "correctedControllerSha256": digest(corrected),
               "baselineControllerSha256": digest(original), "immutableTestSha256": digest(test_bytes),
               "baselineSixLocalFailedEightControlsPassed": True, "candidateFourteenPassed": True, "restoredFullPassed": len(restored),
               "prior1567IdentitiesRetained": True, "originalFullTrxSha256": digest(original_full[0].read_bytes()),
               "sourceRestored": True, "commands": commands,
               "rawFiles": {p.name: digest(p.read_bytes()) for p in output.iterdir() if p.suffix in {".trx", ".log"}}}
    (output / "command-custody.json").write_text(json.dumps(commands, indent=2) + "\n")
    (output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
    commands = []
    prove_delete_causal(repo, root / "delete-literal-causal", run, build, tests, head, inventory)


if __name__ == "__main__":
    main()
