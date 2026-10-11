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
        counters = ET.SubElement(summary, n + "Counters", total="1", executed="1", passed="1", failed="0", timeout="0")
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
        ET.SubElement(summary, n + "Counters", total="2", executed="2", passed="2", failed="0")
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
            self.assertEqual(rows, verify_full("controlled", prior_inventory()))
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
                    verify_full("mutated", prior_inventory())

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
