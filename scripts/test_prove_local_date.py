import unittest
from pathlib import Path
import tempfile
from prove_local_date import *
def verify_accepted_full(path, inventory):
    # Preserve the historical 1614-roster controls alongside the stricter candidate gate.
    return verify_full(path, inventory, include_literal_delete=False)

class ProofTests(unittest.TestCase):
    def rows(self, baseline):
        rows=[dict(**{"class":r["className"]},method=r["method"],name=r["testName"],outcome="Passed",message="",stack="",errorInfo=False) for r in prior_inventory()["rows"] if r["className"]==CLASS for _ in range(r["executions"])]
        for instant,offset,date in CASES:
            failed=baseline and offset!=0
            utc="-".join(str(int(x)) for x in instant[:10].split("-"))
            suffix="/01234567-89ab-cdef-0123-456789abcdef/part.stl"
            rows.append(dict(**{"class":CLASS},method=METHOD,name=case_name(instant,offset,date),outcome="Failed" if failed else "Passed",message=f"Legacy upload date path mismatch; expected path=uploads/{date}{suffix}; actual path=uploads/{utc}{suffix}." if failed else "",stack=CLASS+"."+METHOD,errorInfo=failed))
        return rows
    def test_exact_cases(self):
        verify_focus_rows(self.rows(False),False)
        verify_focus_rows(self.rows(True),True)
    def test_bad_failures_and_rosters(self):
        for field,value in [("message","Timeout"),("stack","Other"),("outcome","Skipped"),("name","Other"),("class","Other")]:
            rows=self.rows(True);rows[5][field]=value
            with self.assertRaises(ValueError):verify_focus_rows(rows,True)
        with self.assertRaises(ValueError):verify_focus_rows(self.rows(True)[:-1],True)
    def test_custody(self):
        for mode in ["delayed","permanent","permission"]:
            clock=[0.0]
            def pause(s):clock[0]+=s
            def probe(group,signal):
                self.assertEqual((42,0),(group,signal))
                if mode=="permission":raise PermissionError("synthetic")
                if mode=="delayed" and clock[0]>=0.2:raise ProcessLookupError
            r={"pid":42,"kernelStartTicks":"123","executable":"/usr/bin/timeout"}
            with tempfile.TemporaryDirectory() as d:
                p=Path(d)/"receipt.json"
                if mode=="delayed":record_group_exit(r,p,[r],probe,lambda:clock[0],pause)
                else:
                    with self.assertRaises(PermissionError if mode=="permission" else ValueError):record_group_exit(r,p,[r],probe,lambda:clock[0],pause)
                self.assertTrue(p.exists());self.assertIn("groupObservationEndedUtc",p.read_text());self.assertLess(clock[0],15.2)

    def test_trx_identity_and_skip_guards(self):
        import xml.etree.ElementTree as ET
        n = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
        root = ET.Element(n + "TestRun")
        definitions = ET.SubElement(root, n + "TestDefinitions")
        test_id = "00000000-0000-4000-8000-000000000001"
        execution_id = "00000000-0000-4000-8000-000000000002"
        definition = ET.SubElement(definitions, n + "UnitTest", id=test_id, name="case1")
        ET.SubElement(definition, n + "TestMethod", className=CLASS, name=METHOD)
        ET.SubElement(definition, n + "Execution", id=execution_id)
        entries = ET.SubElement(root, n + "TestEntries")
        ET.SubElement(entries, n + "TestEntry", testId=test_id, executionId=execution_id)
        results = ET.SubElement(root, n + "Results")
        result = ET.SubElement(results, n + "UnitTestResult", testId=test_id, testName="case1", executionId=execution_id, outcome="Passed")
        summary = ET.SubElement(root, n + "ResultSummary", outcome="Completed")
        counters = ET.SubElement(summary, n + "Counters", total="1", executed="1", passed="1", failed="0",
                                 **{key: "0" for key in NONPASSING_COUNTERS})
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "result.trx"
            ET.ElementTree(root).write(p)
            self.assertEqual(1, len(read_trx(p)))
            for node, attribute, value in [(result, "executionId", "other"), (result, "outcome", "Skipped"), (counters, "timeout", "1"), (summary, "outcome", "Failed")]:
                before = node.get(attribute)
                node.set(attribute, value)
                ET.ElementTree(root).write(p)
                with self.assertRaises(ValueError):
                    read_trx(p)
                node.set(attribute, before)

    def test_partial_focus_cannot_replace_full_suite(self):
        import unittest.mock
        with unittest.mock.patch("prove_local_date.read_trx", return_value=self.rows(False)):
            with self.assertRaises(ValueError):
                verify_accepted_full("synthetic", prior_inventory())

    def test_immutable_baseline_provenance_rejects_wrong_commit_blob_or_bytes(self):
        data = Path(CONTROLLER).read_bytes().replace(b"timeProvider.GetLocalNow()", b"timeProvider.GetUtcNow()")
        self.assertEqual(data, verify_baseline_identity(OLD, BASELINE_BLOB, data))
        for commit, blob, content in [("0" * 40, BASELINE_BLOB, data), (OLD, "0" * 40, data), (OLD, BASELINE_BLOB, data + b"\n")]:
            with self.assertRaises(ValueError):
                verify_baseline_identity(commit, blob, content)


class ReviewedUploadRosterTests(unittest.TestCase):
    def full_rows(self):
        rows = [dict(**{"class": r["className"]}, method=r["method"], name=r["testName"],
                     outcome="Passed", message="", stack="", errorInfo=False)
                for r in prior_inventory()["rows"] for _ in range(r["executions"])]
        rows += [dict(**{"class": CLASS}, method=METHOD, name=case_name(*case),
                      outcome="Passed", message="", stack="", errorInfo=False) for case in CASES]
        rows += [dict(**{"class": cls}, method=method, name=name, outcome="Passed",
                      message="", stack="", errorInfo=False)
                 for cls, method, name in reviewed_upload_additions().elements()]
        rows += [dict(**{"class": cls}, method=method, name=name, outcome="Passed",
                      message="", stack="", errorInfo=False)
                 for cls, method, name in reviewed_reconcile_additions().elements()]
        rows += [dict(**{"class": cls}, method=method, name=name, outcome="Passed",
                      message="", stack="", errorInfo=False)
                 for cls, method, name in reviewed_startup_additions().elements()]
        return rows

    def test_exact_accepted1601_plus_reviewed6_and7_passes(self):
        import unittest.mock
        rows = self.full_rows()
        self.assertEqual(1614, len(rows))
        with unittest.mock.patch("prove_local_date.read_trx", return_value=rows):
            self.assertEqual(rows, verify_accepted_full("controlled", prior_inventory()))

    def test_roster_changes_and_nonpassing_results_fail_closed(self):
        import unittest.mock
        for mutation in ("dropped-baseline", "unreviewed-extra", "missing-new", "failed", "skipped",
                         "duplicate-new", "same-count-baseline-replacement", "passing-with-error"):
            with self.subTest(mutation=mutation):
                rows = self.full_rows()
                if mutation == "dropped-baseline":
                    rows.pop(0)
                elif mutation == "unreviewed-extra":
                    rows.append(dict(rows[0], name="unreviewed"))
                elif mutation == "missing-new":
                    rows.pop()
                elif mutation == "failed":
                    rows[-1]["outcome"] = "Failed"
                elif mutation == "skipped":
                    rows[-1]["outcome"] = "Skipped"
                elif mutation == "duplicate-new":
                    rows[-1] = dict(rows[-2])
                elif mutation == "same-count-baseline-replacement":
                    rows[0] = dict(rows[0], name="unreviewed")
                else:
                    rows[-1]["errorInfo"] = True
                with unittest.mock.patch("prove_local_date.read_trx", return_value=rows):
                    with self.assertRaises(ValueError):
                        verify_accepted_full("controlled", prior_inventory())

    def test_reviewed_inventory_and_source_byte_bindings_fail_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            inventory = Path(directory) / "inventory.json"
            source = Path(directory) / "source.cs"
            original_inventory = Path(UPLOAD_COORDINATE_INVENTORY).read_bytes()
            original_source = Path(UPLOAD_COORDINATE_TEST).read_bytes()
            inventory.write_bytes(original_inventory)
            source.write_bytes(original_source)
            self.assertEqual(25, sum(reviewed_upload_additions(inventory, source).values()))
            for changed in ("inventory", "source"):
                with self.subTest(changed=changed):
                    inventory.write_bytes(original_inventory + (b" " if changed == "inventory" else b""))
                    source.write_bytes(original_source + (b" " if changed == "source" else b""))
                    with self.assertRaises(ValueError):
                        reviewed_upload_additions(inventory, source)

class ReviewedReconcileRosterTests(unittest.TestCase):
    def full_rows(self):
        return ReviewedUploadRosterTests.full_rows(self)

    def test_old_complete1601_cannot_substitute_for_additive1614(self):
        import unittest.mock
        rows = self.full_rows()[:-13]
        self.assertEqual(1601, len(rows))
        with unittest.mock.patch("prove_local_date.read_trx", return_value=rows):
            with self.assertRaises(ValueError):
                verify_accepted_full("old-complete", prior_inventory())

    def test_prior1601_identities_are_preserved_exactly(self):
        rows = self.full_rows()
        old = Counter((row["class"], row["method"], row["name"]) for row in rows[:-13])
        prior = Counter({(row["className"], row["method"], row["testName"]): row["executions"]
                         for row in prior_inventory()["rows"]})
        self.assertEqual(prior + new_identities() + reviewed_upload_additions(), old)
        self.assertEqual(1601, sum(old.values()))
        self.assertEqual(reviewed_reconcile_additions(),
                         Counter((row["class"], row["method"], row["name"]) for row in rows[-13:-7]))

    def test_reconcile_inventory_and_source_bindings_fail_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            inventory = Path(directory) / "inventory.json"
            source = Path(directory) / "source.cs"
            original_inventory = Path(RECONCILE_GENERATION_INVENTORY).read_bytes()
            original_source = Path(RECONCILE_GENERATION_TEST).read_bytes()
            inventory.write_bytes(original_inventory)
            source.write_bytes(original_source)
            self.assertEqual(6, sum(reviewed_reconcile_additions(inventory, source).values()))
            for changed in ("inventory", "source"):
                with self.subTest(changed=changed):
                    inventory.write_bytes(original_inventory + (b" " if changed == "inventory" else b""))
                    source.write_bytes(original_source + (b" " if changed == "source" else b""))
                    with self.assertRaises(ValueError):
                        reviewed_reconcile_additions(inventory, source)

    def test_real_redis_first_acquire_failure_is_never_hidden(self):
        import unittest.mock
        rows = self.full_rows()
        name = ('Legacy.Maliev.FileService.Tests.Data.RedisUploadIdempotencyStoreTests.'
                'SupersededWorker_CannotChangeNewReservationOrItsLease(operation: "release")')
        failed = [row for row in rows if row["name"] == name]
        self.assertEqual(1, len(failed))
        failed[0].update(outcome="Failed", errorInfo=True, message="command=UNWATCH; timeout is 5000ms",
                         stack="RedisUploadIdempotencyStore.AcquireAsync")
        with unittest.mock.patch("prove_local_date.read_trx", return_value=rows):
            with self.assertRaises(ValueError):
                verify_accepted_full("real-failure-shaped-fixture", prior_inventory())

    def test_exact_count_with_unreviewed_new_identity_is_rejected(self):
        import unittest.mock
        rows = self.full_rows()
        rows[-8] = dict(rows[-8], name="unreviewed-reconciliation-case")
        self.assertEqual(1614, len(rows))
        with unittest.mock.patch("prove_local_date.read_trx", return_value=rows):
            with self.assertRaises(ValueError):
                verify_accepted_full("same-count-replaced-new-case", prior_inventory())


class ReviewedStartupRosterTests(unittest.TestCase):
    def test_old_complete1607_is_rejected(self):
        import unittest.mock
        rows = ReviewedUploadRosterTests.full_rows(self)[:-7]
        self.assertEqual(1607, len(rows))
        with unittest.mock.patch("prove_local_date.read_trx", return_value=rows):
            with self.assertRaises(ValueError):
                verify_accepted_full("old-complete1607", prior_inventory())

    def test_prior1607_counter_is_preserved(self):
        rows = ReviewedUploadRosterTests.full_rows(self)
        prior = Counter({(r["className"], r["method"], r["testName"]): r["executions"]
                         for r in prior_inventory()["rows"]})
        self.assertEqual(prior + new_identities() + reviewed_upload_additions() + reviewed_reconcile_additions(),
                         Counter((r["class"], r["method"], r["name"]) for r in rows[:-7]))
        self.assertEqual(reviewed_startup_additions(),
                         Counter((r["class"], r["method"], r["name"]) for r in rows[-7:]))

    def test_startup_source_and_inventory_tampering_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            inventory = Path(directory) / "inventory.json"
            source = Path(directory) / "source.cs"
            original_inventory = Path(STARTUP_OBSERVATION_INVENTORY).read_bytes()
            original_source = Path(STARTUP_OBSERVATION_TEST).read_bytes()
            inventory.write_bytes(original_inventory)
            source.write_bytes(original_source)
            self.assertEqual(7, sum(reviewed_startup_additions(inventory, source).values()))
            for changed in ("inventory", "source"):
                with self.subTest(changed=changed):
                    inventory.write_bytes(original_inventory + (b" " if changed == "inventory" else b""))
                    source.write_bytes(original_source + (b" " if changed == "source" else b""))
                    with self.assertRaises(ValueError):
                        reviewed_startup_additions(inventory, source)

    def test_changed_startup_identity_at_same_count_is_rejected(self):
        import unittest.mock
        rows = ReviewedUploadRosterTests.full_rows(self)
        rows[-1] = dict(rows[-1], name="unreviewed-startup-case")
        with unittest.mock.patch("prove_local_date.read_trx", return_value=rows):
            with self.assertRaises(ValueError):
                verify_accepted_full("same-count-changed-startup", prior_inventory())

    def test_nonpassing_startup_controls_are_rejected(self):
        import unittest.mock
        for mutation in ("failed", "skipped", "passing-with-error"):
            with self.subTest(mutation=mutation):
                rows = ReviewedUploadRosterTests.full_rows(self)
                rows[-1].update(outcome={"failed": "Failed", "skipped": "NotExecuted"}.get(mutation, "Passed"),
                                errorInfo=mutation != "skipped")
                with unittest.mock.patch("prove_local_date.read_trx", return_value=rows):
                    with self.assertRaises(ValueError):
                        verify_accepted_full("nonpassing-startup", prior_inventory())


class TrxEntryJoinTests(unittest.TestCase):
    def test_duplicate_summary_counters_and_run_error_info_reject(self):
        import copy
        import xml.etree.ElementTree as ET
        n = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
        for mutation in ("summary", "counters", "error-info"):
            def mutate(root, definitions, results, entries):
                summary = root.find(n + "ResultSummary")
                if mutation == "summary":
                    root.append(copy.deepcopy(summary))
                elif mutation == "counters":
                    summary.append(copy.deepcopy(summary.find(n + "Counters")))
                else:
                    ET.SubElement(ET.SubElement(summary, n + "Output"), n + "ErrorInfo")
            with self.subTest(mutation=mutation):
                self.verify_rejected(mutate)

    def test_missing_mandatory_and_nonzero_optional_counters_reject(self):
        n = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
        for key in ("total", "executed", "passed", "failed", "timeout", "error", "warning",
                    "aborted", "inconclusive", "passedButRunAborted", "notRunnable",
                    "notExecuted", "disconnected", "completed", "inProgress", "pending"):
            def mutate(root, definitions, results, entries):
                counters = root.find(n + "ResultSummary/" + n + "Counters")
                if key in {"total", "executed", "passed", "failed"}:
                    counters.attrib.pop(key)
                else:
                    counters.set(key, "1")
            with self.subTest(counter=key):
                self.verify_rejected(mutate)

    def test_clean_focus_rejects_adverse_run_infos(self):
        import xml.etree.ElementTree as ET
        n = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "focus.trx"
            for outcome in ("Information", "Error", "Warning", "Aborted", "unknown", ""):
                root, _, _, _ = self.fixture()
                summary = root.find(n + "ResultSummary")
                ET.SubElement(ET.SubElement(summary, n + "RunInfos"), n + "RunInfo", outcome=outcome)
                ET.ElementTree(root).write(path)
                if outcome == "Information":
                    self.assertEqual(2, len(read_trx(path, strict_clean_run=True)))
                else:
                    with self.assertRaises(ValueError):
                        read_trx(path, strict_clean_run=True)

    def test_every_missing_nonpassing_counter_rejects(self):
        n = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
        for key in NONPASSING_COUNTERS:
            def mutate(root, definitions, results, entries):
                root.find(n + "ResultSummary/" + n + "Counters").attrib.pop(key)
            with self.subTest(counter=key):
                self.verify_rejected(mutate)

    def test_clean_focus_rejects_failed_or_incomplete_summary(self):
        import xml.etree.ElementTree as ET
        n = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "focus.trx"
            for outcome in ("Failed", "Aborted", "InProgress", ""):
                root, _, _, _ = self.fixture()
                root.find(n + "ResultSummary").set("outcome", outcome)
                ET.ElementTree(root).write(path)
                with self.assertRaises(ValueError):
                    read_trx(path, strict_clean_run=True)

    def fixture(self):
        import xml.etree.ElementTree as ET
        n = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
        root = ET.Element(n + "TestRun")
        definitions = ET.SubElement(root, n + "TestDefinitions")
        results = ET.SubElement(root, n + "Results")
        entries = ET.SubElement(root, n + "TestEntries")
        for index in (1, 2):
            identity = f"00000000-0000-4000-8000-{index:012d}"
            execution = f"00000000-0000-4000-8000-{index + 10:012d}"
            definition = ET.SubElement(definitions, n + "UnitTest", id=identity, name=f"case{index}")
            ET.SubElement(definition, n + "TestMethod", className=CLASS, name=METHOD)
            ET.SubElement(definition, n + "Execution", id=execution)
            ET.SubElement(results, n + "UnitTestResult", testId=identity, executionId=execution,
                          testName=f"case{index}", outcome="Passed")
            ET.SubElement(entries, n + "TestEntry", testId=identity, executionId=execution)
        summary = ET.SubElement(root, n + "ResultSummary", outcome="Completed")
        ET.SubElement(summary, n + "Counters", total="2", executed="2", passed="2", failed="0",
                      **{key: "0" for key in NONPASSING_COUNTERS})
        return root, definitions, results, entries

    def verify_rejected(self, mutate):
        import xml.etree.ElementTree as ET
        root, definitions, results, entries = self.fixture()
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "join.trx"
            ET.ElementTree(root).write(path)
            self.assertEqual(2, len(read_trx(path)))
            mutate(root, definitions, results, entries)
            ET.ElementTree(root).write(path)
            with self.assertRaises(ValueError):
                read_trx(path)

    def test_absent_entries_section_is_rejected(self):
        self.verify_rejected(lambda root, definitions, results, entries: root.remove(entries))

    def test_missing_entry_is_rejected(self):
        self.verify_rejected(lambda root, definitions, results, entries: entries.remove(entries[1]))

    def test_duplicate_entry_at_same_count_is_rejected(self):
        def mutate(root, definitions, results, entries):
            entries[1].attrib.update(entries[0].attrib)
        self.verify_rejected(mutate)

    def test_swapped_entry_executions_are_rejected(self):
        def mutate(root, definitions, results, entries):
            first, second = entries[0].get("executionId"), entries[1].get("executionId")
            entries[0].set("executionId", second)
            entries[1].set("executionId", first)
        self.verify_rejected(mutate)

    def test_unknown_entry_test_id_is_rejected(self):
        self.verify_rejected(lambda root, definitions, results, entries:
                             entries[1].set("testId", "00000000-0000-4000-8000-000000000099"))

    def test_malformed_guids_in_every_join_surface_are_rejected(self):
        for surface, attribute in (("definition", "id"), ("execution", "id"), ("result", "testId"),
                                   ("result", "executionId"), ("entry", "testId"), ("entry", "executionId")):
            for value in ("", "not-a-guid", "{00000000-0000-4000-8000-000000000001}"):
                with self.subTest(surface=surface, attribute=attribute, value=value):
                    def mutate(root, definitions, results, entries):
                        node = {"definition": definitions[0], "execution": definitions[0][1],
                                "result": results[0], "entry": entries[0]}[surface]
                        node.set(attribute, value)
                    self.verify_rejected(mutate)

class ReviewedDeleteLiteralRosterTests(unittest.TestCase):
    def candidate_rows(self):
        return ReviewedUploadRosterTests.full_rows(self) + [
            dict(**{"class": cls}, method=method, name=name, outcome="Passed",
                 message="", stack="", errorInfo=False)
            for cls, method, name in reviewed_delete_additions().elements()
        ]

    def test_exact_accepted1614_plus_literal23_passes(self):
        import unittest.mock
        raw = Path(DELETE_LITERAL_INVENTORY).read_bytes()
        self.assertEqual(digest(raw), DELETE_LITERAL_INVENTORY_SHA256)
        self.assertEqual(23, sum(reviewed_delete_additions().values()))
        rows = self.candidate_rows()
        self.assertEqual(1637, len(rows))
        with unittest.mock.patch("prove_local_date.read_trx", return_value=rows):
            self.assertEqual(rows, verify_full("controlled", prior_inventory(), include_signed_download=False))
        self.assertEqual(Counter((row["class"], row["method"], row["name"]) for row in rows[:1614]),
                         Counter((row["class"], row["method"], row["name"]) for row in ReviewedUploadRosterTests.full_rows(self)))

    def test_old1614_and_replaced_duplicate_failed_skipped_literal_cases_fail(self):
        import unittest.mock
        for mutation in ("old1614", "replacement", "duplicate", "failed", "skipped", "passing-error"):
            rows = self.candidate_rows()
            if mutation == "old1614":
                rows = rows[:1614]
            elif mutation == "replacement":
                rows[-1] = dict(rows[-1], name="unreviewed")
            elif mutation == "duplicate":
                rows[-1] = dict(rows[-2])
            elif mutation == "failed":
                rows[-1]["outcome"] = "Failed"
            elif mutation == "skipped":
                rows[-1]["outcome"] = "NotExecuted"
            else:
                rows[-1]["errorInfo"] = True
            with unittest.mock.patch("prove_local_date.read_trx", return_value=rows):
                with self.assertRaises(ValueError):
                    verify_full("mutated", prior_inventory(), include_signed_download=False)

    def test_literal_inventory_and_both_source_bindings_fail_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            inventory = root / "inventory.json"
            raw = Path(DELETE_LITERAL_INVENTORY).read_bytes()
            sources = json.loads(raw)["sources"]
            originals = {row["path"]: Path(row["path"]).read_bytes() for row in sources}
            for mutation in (None, "inventory", *(row["path"] for row in sources)):
                inventory.write_bytes(raw + (b" " if mutation == "inventory" else b""))
                for path, content in originals.items():
                    target = root / path
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(content + (b" " if mutation == path else b""))
                if mutation is None:
                    self.assertEqual(23, sum(reviewed_delete_additions(inventory, root).values()))
                else:
                    expected = ("Reviewed literal delete inventory changed" if mutation == "inventory"
                                else "Reviewed literal delete test source changed")
                    with self.assertRaisesRegex(ValueError, expected):
                        reviewed_delete_additions(inventory, root)

class ReviewedSignedDownloadRosterTests(unittest.TestCase):
    def rows(self):
        return ReviewedDeleteLiteralRosterTests.candidate_rows(self) + [
            dict(**{"class": cls}, method=method, name=name, outcome="Passed",
                 message="", stack="", errorInfo=False)
            for cls, method, name in reviewed_signed_download_additions().elements()
        ]

    def test_exact_accepted1637_plus_signed3_passes(self):
        import unittest.mock
        rows = self.rows()
        self.assertEqual(1640, len(rows))
        with unittest.mock.patch("prove_local_date.read_trx", return_value=rows):
            self.assertEqual(rows, verify_full("controlled", prior_inventory()))
        self.assertEqual(ReviewedDeleteLiteralRosterTests.candidate_rows(self), rows[:1637])

    def test_old_roster_replacement_duplicate_failed_skipped_and_passing_error_reject(self):
        import unittest.mock
        for mutation in ("old1637", "replacement", "duplicate", "failed", "skipped", "passing-error"):
            with self.subTest(mutation=mutation):
                rows = self.rows()
                if mutation == "old1637":
                    rows = rows[:1637]
                elif mutation == "replacement":
                    rows[-1]["name"] = "unreviewed"
                elif mutation == "duplicate":
                    rows[-1] = dict(rows[-2])
                elif mutation == "passing-error":
                    rows[-1]["errorInfo"] = True
                else:
                    rows[-1]["outcome"] = "Failed" if mutation == "failed" else "NotExecuted"
                with unittest.mock.patch("prove_local_date.read_trx", return_value=rows):
                    with self.assertRaises(ValueError):
                        verify_full("mutated", prior_inventory())

    def test_inventory_and_reviewed_source_bytes_are_bound(self):
        raw = Path(SIGNED_DOWNLOAD_INVENTORY).read_bytes()
        source = json.loads(raw)["sources"][0]["path"]
        content = Path(source).read_bytes()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            inventory = root / "inventory.json"
            target = root / source
            target.parent.mkdir(parents=True)
            for mutation in (None, "inventory", "source"):
                inventory.write_bytes(raw + (b" " if mutation == "inventory" else b""))
                target.write_bytes(content + (b" " if mutation == "source" else b""))
                if mutation is None:
                    self.assertEqual(3, sum(reviewed_signed_download_additions(inventory, root).values()))
                else:
                    with self.assertRaises(ValueError):
                        reviewed_signed_download_additions(inventory, root)


class CleanAcceptancePathTests(unittest.TestCase):
    def fixture(self, rows):
        import xml.etree.ElementTree as ET
        n = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
        root = ET.Element(n + "TestRun")
        definitions = ET.SubElement(root, n + "TestDefinitions")
        results = ET.SubElement(root, n + "Results")
        entries = ET.SubElement(root, n + "TestEntries")
        for index, row in enumerate(rows, 1):
            identity = f"00000000-0000-4000-8000-{index:012d}"
            execution = f"00000000-0000-4000-9000-{index:012d}"
            definition = ET.SubElement(definitions, n + "UnitTest", id=identity, name=row["name"])
            ET.SubElement(definition, n + "TestMethod", className=row["class"], name=row["method"])
            ET.SubElement(definition, n + "Execution", id=execution)
            result = ET.SubElement(results, n + "UnitTestResult", testId=identity, executionId=execution,
                                   testName=row["name"], outcome=row["outcome"])
            ET.SubElement(entries, n + "TestEntry", testId=identity, executionId=execution)
            if row["errorInfo"]:
                error = ET.SubElement(ET.SubElement(result, n + "Output"), n + "ErrorInfo")
                ET.SubElement(error, n + "Message").text = row["message"]
                ET.SubElement(error, n + "StackTrace").text = row["stack"]
        failed = sum(row["outcome"] == "Failed" for row in rows)
        summary = ET.SubElement(root, n + "ResultSummary", outcome="Failed" if failed else "Completed")
        ET.SubElement(summary, n + "Counters", total=str(len(rows)), executed=str(len(rows)),
                      passed=str(len(rows) - failed), failed=str(failed), **{key: "0" for key in NONPASSING_COUNTERS})
        return root

    def paths(self):
        full = ReviewedSignedDownloadRosterTests.rows(self)
        signing = [row for row in full if row["class"] in {
            SIGNED_DOWNLOAD_CLASS, "Legacy.Maliev.FileService.Tests.Data.GoogleCloudObjectStorageSignedUrlTests"}]
        startup = [row for row in full if row["class"].endswith(".FileStartupBoundaryTests")]
        self.assertEqual(11, len(signing))
        self.assertEqual(15, len(startup))
        return [
            ("startup-default15", self.fixture(startup), read_trx),
            ("signing11", self.fixture(signing), verify_signing_focus),
            ("date-candidate14", self.fixture(ProofTests.rows(self, False)), lambda path: verify_focus(path, False)),
            ("delete-candidate23", self.fixture(DeleteCausalProofTests.rows(self, False)), lambda path: verify_delete_focus(path, False)),
            *((name, self.fixture(full), lambda path: verify_full(path, prior_inventory())) for name in
              ("original-full1640", "date-restored1640", "delete-restored1640")),
        ]

    def test_every_candidate_full_and_required_focus_rejects_adverse_run_evidence(self):
        import copy
        import xml.etree.ElementTree as ET
        n = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "acceptance.trx"
            for name, root, reader in self.paths():
                ET.ElementTree(root).write(path)
                reader(path)  # Positive control must reach and pass the real roster gate.
                for mutation in ("RunInfo", "ErrorInfo", "duplicate-summary", "missing-nonpassing-counter"):
                    changed = copy.deepcopy(root)
                    summary = changed.find(n + "ResultSummary")
                    if mutation == "RunInfo":
                        ET.SubElement(ET.SubElement(summary, n + "RunInfos"), n + "RunInfo", outcome="Error")
                    elif mutation == "ErrorInfo":
                        ET.SubElement(ET.SubElement(summary, n + "Output"), n + "ErrorInfo")
                    elif mutation == "duplicate-summary":
                        changed.append(copy.deepcopy(summary))
                    else:
                        summary.find(n + "Counters").attrib.pop("notExecuted")
                    ET.ElementTree(changed).write(path)
                    with self.subTest(path=name, mutation=mutation):
                        with self.assertRaisesRegex(ValueError, "RunInfo|ErrorInfo|summary|counters"):
                            reader(path)

    def test_only_explicit_baselines_accept_expected_failed_run_infos(self):
        import xml.etree.ElementTree as ET
        n = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
        cases = [(ProofTests.rows(self, True), lambda path: verify_focus(path, True), 6),
                 (DeleteCausalProofTests.rows(self, True), lambda path: verify_delete_focus(path, True), 13)]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "baseline.trx"
            for rows, reader, failures in cases:
                root = self.fixture(rows)
                ET.SubElement(ET.SubElement(root.find(n + "ResultSummary"), n + "RunInfos"),
                              n + "RunInfo", outcome="Error")
                ET.ElementTree(root).write(path)
                reader(path)
                self.assertEqual(failures, sum(row["outcome"] == "Failed"
                                             for row in read_trx(path, strict_clean_run=False)))
                with self.assertRaises(ValueError):
                    read_trx(path)


class SigningFocusCollectorTests(unittest.TestCase):
    def rows(self):
        full = ReviewedSignedDownloadRosterTests.rows(self)
        return [row for row in full if row["class"] in {SIGNED_DOWNLOAD_CLASS, SIGNED_EXISTING_CLASS}]

    def test_exact_eight_plus_three_strict_focus_passes(self):
        import xml.etree.ElementTree as ET
        rows = self.rows()
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "signing11.trx"
            ET.ElementTree(CleanAcceptancePathTests.fixture(self, rows)).write(path)
            self.assertEqual(11, len(verify_signing_focus(path)))

    def test_old_eight_replacement_duplicate_and_nonpassing_reject(self):
        import xml.etree.ElementTree as ET
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "signing11.trx"
            for mutation in ("old8", "replacement", "duplicate", "failed", "skipped", "passing-error"):
                rows = self.rows()
                if mutation == "old8":
                    rows = [row for row in rows if row["class"] == SIGNED_EXISTING_CLASS]
                elif mutation == "replacement":
                    rows[-1]["name"] = "unreviewed"
                elif mutation == "duplicate":
                    rows[-1] = dict(rows[-2])
                elif mutation == "passing-error":
                    rows[-1]["errorInfo"] = True
                else:
                    rows[-1]["outcome"] = "Failed" if mutation == "failed" else "NotExecuted"
                ET.ElementTree(CleanAcceptancePathTests.fixture(self, rows)).write(path)
                with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                    verify_signing_focus(path)

    def test_existing_signing_source_pin_rejects_changes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            target = root / SIGNED_EXISTING_TEST
            target.parent.mkdir(parents=True)
            content = Path(SIGNED_EXISTING_TEST).read_bytes()
            target.write_bytes(content)
            self.assertEqual(8, sum(reviewed_existing_signing_identities(root).values()))
            target.write_bytes(content + b" ")
            with self.assertRaisesRegex(ValueError, "existing signing test source changed"):
                reviewed_existing_signing_identities(root)


class DeleteCausalProofTests(unittest.TestCase):
    def rows(self, baseline):
        rows = []
        for cls, method, name in reviewed_delete_additions().elements():
            unit = cls.endswith(".FileDeleteLiteralIdentityTests")
            failed = baseline and method in (DELETE_UNIT_FAILURES if unit else DELETE_HTTP_FAILURES)
            rows.append(dict(**{"class": cls}, method=method, name=name,
                outcome="Failed" if failed else "Passed", errorInfo=failed,
                message=("Moq.MockException ExistsAsync normalized coordinate" if unit else delete_http_failure_message(method)) if failed else "",
                stack=(cls + "." + ("AssertRejectedAsync" if "Control" in method else
                       "AssertFailureAsync" if method in {"Delete_MissingMetadata_PreservesMetadata", "Delete_ProviderFailure_PreservesMetadata"}
                       else "AssertLiteralAsync") if unit else cls + ".AssertDeleteAsync(Boolean literalExists, Boolean decoyExists) in /source/LegacyLiteralDeleteHttpBoundaryTests.cs:line "
                       + ("52" if method == "LiteralAndDecoy_DeletesLiteralAndRetainsDecoy" else "51")) if failed else ""))
        return rows

    def test_each_http_failure_rejects_auth_infrastructure_values_and_wrong_stage(self):
        for method in DELETE_HTTP_FAILURES:
            for mutation in ("Unauthorized", "ServiceUnavailable", "wrong-stage", "wrong-values", "fact-stack"):
                with self.subTest(method=method, mutation=mutation):
                    rows = self.rows(True)
                    row = next(row for row in rows if row["method"] == method)
                    if mutation == "wrong-stage":
                        row["stack"] = row["stack"].rsplit(" ", 1)[0] + " 55"
                    elif mutation == "fact-stack":
                        row["stack"] = row["class"] + "." + method
                    elif mutation == "wrong-values":
                        row["message"] = row["message"].replace("Expected:", "Actual:", 1)
                    else:
                        row["message"] = "Assert.Equal() Failure: Values differ\nExpected: NoContent\nActual:   " + mutation
                    with self.assertRaises(ValueError):
                        verify_delete_focus_rows(rows, True)
    def test_exact_candidate_and_baseline_coordinate_failures(self):
        for baseline in (False, True):
            rows = self.rows(baseline)
            self.assertEqual(rows, verify_delete_focus_rows(rows, baseline))
            self.assertEqual(13 if baseline else 0, sum(row["outcome"] == "Failed" for row in rows))

    def test_infrastructure_failure_cannot_replace_expected_coordinate_failure(self):
        for kind in ("database", "auth", "admission", "timeout", "unrelated-mock"):
            rows = self.rows(True)
            row = next(row for row in rows if row["outcome"] == "Failed")
            row["message"] = kind
            with self.assertRaises(ValueError):
                verify_delete_focus_rows(rows, True)

    def test_changed_rosters_stacks_outcomes_and_passing_errors_reject(self):
        for mutation in ("missing", "duplicate", "unreviewed", "wrong-stack", "wrong-outcome", "skipped", "passing-error"):
            rows = self.rows(True)
            failed = next(row for row in rows if row["outcome"] == "Failed")
            if mutation == "missing":
                rows.pop()
            elif mutation == "duplicate":
                rows[-1] = dict(rows[-2])
            elif mutation == "unreviewed":
                rows[-1]["name"] = "unreviewed"
            elif mutation == "wrong-stack":
                failed["stack"] = "unrelated"
            elif mutation == "wrong-outcome":
                failed["outcome"] = "Passed"
            elif mutation == "skipped":
                failed["outcome"] = "NotExecuted"
            else:
                next(row for row in rows if row["outcome"] == "Passed")["errorInfo"] = True
            with self.assertRaises(ValueError):
                verify_delete_focus_rows(rows, True)

    def test_actual_candidate_reconstructs_exact_pinned_baseline_and_rejects_wrong_identity(self):
        corrected = Path(DELETE_SERVICE).read_bytes()
        baseline = delete_correction_disabled(corrected)
        self.assertEqual(DELETE_BASELINE_SHA256, digest(baseline))
        self.assertEqual(baseline, verify_delete_baseline_identity(DELETE_BASELINE_COMMIT, DELETE_BASELINE_BLOB, baseline))
        for commit, blob, data in (("0" * 40, DELETE_BASELINE_BLOB, baseline),
                                  (DELETE_BASELINE_COMMIT, "0" * 40, baseline),
                                  (DELETE_BASELINE_COMMIT, DELETE_BASELINE_BLOB, baseline + b"\n")):
            with self.assertRaises(ValueError):
                verify_delete_baseline_identity(commit, blob, data)
        for changed in (corrected + b"\n", corrected.replace(b"RequireExistingObjectIdentity", b"OtherIdentity")):
            with self.assertRaises(ValueError):
                delete_correction_disabled(changed)
