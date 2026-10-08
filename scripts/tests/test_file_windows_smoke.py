from pathlib import Path
import sys
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import smoke_file_windows_job as m

class SmokeProofControls(unittest.TestCase):
    def record(self):return {'actualJobLimits':dict(m.EXPECTED_LIMITS)}
    def test_success_requires_actual_limits_and_quiescence(self):m.evaluate_case('success',None,self.record(),True,True)
    def test_timeout_witness(self):m.evaluate_case('timeout',TimeoutError(),self.record(),True,True)
    def test_partial_start_causal_marker(self):
        record=self.record();record['partialStartInjected']=True;m.evaluate_case('partial-start',RuntimeError(),record,True,True)
    def test_unsettled_handles_rejected(self):
        with self.assertRaises(RuntimeError):m.evaluate_case('success',None,self.record(),True,False)
    def test_unstopped_process_rejected(self):
        with self.assertRaises(RuntimeError):m.evaluate_case('success',None,self.record(),False,True)
    def test_wrong_timeout_failure_rejected(self):
        with self.assertRaises(RuntimeError):m.evaluate_case('timeout',RuntimeError(),self.record(),True,True)
    def test_unwitnessed_partial_start_rejected(self):
        with self.assertRaises(RuntimeError):m.evaluate_case('partial-start',RuntimeError(),self.record(),True,True)
    def test_limits_missing_rejected(self):
        with self.assertRaises(RuntimeError):m.evaluate_case('success',None,{},True,True)
    def test_changed_limits_rejected(self):
        record=self.record();record['actualJobLimits']['activeProcesses']=9
        with self.assertRaises(RuntimeError):m.evaluate_case('success',None,record,True,True)
    def test_unknown_case_rejected(self):
        with self.assertRaises(RuntimeError):m.evaluate_case('unreviewed',None,self.record(),True,True)
    def test_original_failure_preserved(self):
        error=OSError('synthetic')
        with self.assertRaises(OSError):m.evaluate_case('success',error,self.record(),True,True)

if __name__=='__main__':unittest.main()
