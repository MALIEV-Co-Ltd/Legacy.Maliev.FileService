import unittest
from pathlib import Path
import tempfile
from prove_local_date import *

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
        definition = ET.SubElement(definitions, n + "UnitTest", id="test1", name="case1")
        ET.SubElement(definition, n + "TestMethod", className=CLASS, name=METHOD)
        ET.SubElement(definition, n + "Execution", id="execution1")
        results = ET.SubElement(root, n + "Results")
        result = ET.SubElement(results, n + "UnitTestResult", testId="test1", testName="case1", executionId="execution1", outcome="Passed")
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
                verify_full("synthetic", prior_inventory())

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
        return rows

    def test_exact_accepted1576_plus_reviewed25_passes(self):
        import unittest.mock
        rows = self.full_rows()
        self.assertEqual(1601, len(rows))
        with unittest.mock.patch("prove_local_date.read_trx", return_value=rows):
            self.assertEqual(rows, verify_full("controlled", prior_inventory()))

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
                        verify_full("controlled", prior_inventory())

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
