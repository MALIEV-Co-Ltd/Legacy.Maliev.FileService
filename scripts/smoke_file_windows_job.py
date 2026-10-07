"""Actual Windows owned-Python lifecycle smoke. No SDK commands or build permits."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import sys
import time
from datetime import datetime,timedelta,timezone
from materialize_file_candidate import load_policy, FIXED_ROOT,validate_profile,validate_bindings
from sealed_source_capsule import digest,parse_json,reject_links


def load_supervisor(root,policy):
    manifest=Path(root)/'outputs/file-build-supervisor-source-v2/manifest.json'
    raw=manifest.read_bytes()
    if digest(raw)!=policy['supervisorManifestSha256']:raise ValueError('Reviewed Windows supervisor differs')
    obj=parse_json(raw)
    for row in obj['files']:
        path=Path(row['path']);reject_links(path)
        if digest(path.read_bytes())!=row['sha256']:raise ValueError('Reviewed Windows helper differs')
    sys.path.insert(0,str(Path(root)/'outputs'))
    path=Path(root)/'outputs/file_build_supervisor_draft_v2.py'
    spec=importlib.util.spec_from_file_location('file_owned_windows_supervisor',path)
    module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
    module.REPO=Path(root)/'work/file-literal-upload-mutation-v8'
    return module


class PythonDeadline:
    """Finite Python-only lifetime; deliberately not a Root SDK phase grant."""
    def __init__(self):self.expiry=datetime.now(timezone.utc)+timedelta(seconds=60)
    def live(self):
        if datetime.now(timezone.utc)>=self.expiry:raise TimeoutError('Owned Python smoke deadline expired')


def job_limits(backend):
    api=backend.api;limits=api.JobLimits();cpu=api.JobCpu()
    if not api.kernel.QueryInformationJobObject(backend.job,9,api.ctypes.byref(limits),api.ctypes.sizeof(limits),None):raise RuntimeError('Actual job memory/process limits unavailable')
    if not api.kernel.QueryInformationJobObject(backend.job,15,api.ctypes.byref(cpu),api.ctypes.sizeof(cpu),None):raise RuntimeError('Actual job CPU limit unavailable')
    result={'memoryBytes':limits.jobMemory,'activeProcesses':limits.basic.activeProcesses,'limitFlags':limits.basic.flags,'cpuFlags':cpu.flags,'cpuRate':cpu.rate}
    if result!={'memoryBytes':2147483648,'activeProcesses':8,'limitFlags':0x2208,'cpuFlags':5,'cpuRate':5000}:raise RuntimeError('Actual Windows job envelope differs')
    return result


EXPECTED_LIMITS={'memoryBytes':2147483648,'activeProcesses':8,'limitFlags':0x2208,'cpuFlags':5,'cpuRate':5000}
def evaluate_case(case,error,record,verified,released):
    if not verified or not released:raise RuntimeError('Owned Windows smoke exit/handle proof missing')
    if record.get('actualJobLimits')!=EXPECTED_LIMITS:raise RuntimeError('Actual Windows job limits not witnessed')
    if case=='success':
        if error is not None:raise error
    elif case=='timeout':
        if not isinstance(error,TimeoutError):raise RuntimeError('Actual timeout path was not witnessed')
    elif case=='partial-start':
        if not record.get('partialStartInjected') or not isinstance(error,RuntimeError):raise RuntimeError('Actual partial-start path was not witnessed')
    else:raise RuntimeError('Unreviewed smoke case')


def run_smoke(module,evidence,policy):
    if os.name!='nt':raise RuntimeError('Actual Windows smoke requires Windows')
    evidence=Path(evidence);reject_links(evidence);evidence.mkdir(parents=True,exist_ok=True)
    cases=[]
    for case in ['success','timeout','partial-start']:
        record={'owner':policy['owner'],'purpose':'Windows Python-only lifecycle qualification','case':case,'nativeSdkSpawned':False,'ports':[],'containers':[],'persistentData':False}
        target=evidence/(case+'-resources.json')
        def persist():target.write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
        class Backend(module.WindowsBackend):
            def resume_primary(self,handle):
                record['actualJobLimits']=job_limits(self)
                if case=='partial-start':
                    record['partialStartInjected']=True
                    raise RuntimeError('Owned partial-start smoke injection')
                super().resume_primary(handle)
        backend=Backend(record)
        error=None
        try:
            free,jobs=backend.memory_jobs()
            if not module.admitted(free,jobs):raise RuntimeError('Fresh 4096MiB/SDK-empty smoke admission failed')
            record['admissionFreeMiB']=free;record['admissionSdkJobs']=jobs
            api=backend.api;api.kernel.GetCurrentProcess.argtypes=[];api.kernel.GetCurrentProcess.restype=api.wintypes.HANDLE
            parent=api.kernel.GetCurrentProcess();birth=api.ticks(parent)
            record.update(ownerPid=os.getpid(),ownerStartFileTime=birth,ownerStartUtc=api.started(birth),ownerExecutable=backend.image(parent))
            start=time.monotonic()
            # Use actual elapsed time with a two-second deadline for the timeout witness.
            clock=(lambda:(time.monotonic()-start)*301) if case=='timeout' else time.monotonic
            command=[sys.executable,'-B','-c','import time; print("owned-python-smoke",flush=True); time.sleep('+('30' if case=='timeout' else '1')+')']
            with (evidence/(case+'.log')).open('xb') as output:
                try:module.run_owned_phase(backend,command,output,dict(os.environ,PYTHONDONTWRITEBYTECODE='1'),PythonDeadline(),record,persist,lambda:output.tell(),clock=clock)
                except BaseException as caught:error=caught
        except BaseException as caught:error=caught
        finally:module.recover_final_owner(backend,record,persist)
        evaluate_case(case,error,record,backend.verified,backend.released)
        record['controllerFailureType']=None if error is None else type(error).__name__;record['smokePassed']=True;persist();cases.append(record)
    # Actual final raw handle closure/retry after a pre-phase census failure; no child allocation.
    record={'owner':policy['owner'],'case':'pre-phase-final-close','nativeSdkSpawned':False,'ports':[],'persistentData':False}
    target=evidence/'pre-phase-final-close-resources.json'
    def persist():target.write_text(json.dumps(record,indent=2)+'\n',encoding='utf-8')
    class FinalBackend(module.WindowsBackend):
        def close(self):
            if not record.get('finalCloseInjected'):
                record['finalCloseInjected']=True
                raise RuntimeError('Actual retained handle retry injection')
            super().close()
    backend=FinalBackend(record)
    try:
        free,jobs=backend.memory_jobs()
        if not module.admitted(free,jobs):raise RuntimeError('Fresh final-close smoke admission failed')
        api=backend.api;handle=api.kernel.OpenProcess(0x1000,False,os.getpid())
        if not handle:raise RuntimeError('Own process probe handle unavailable')
        backend.handles.append(handle)
        record['probeStartFileTime']=api.ticks(handle);record['probeExecutable']=backend.image(handle)
    finally:module.recover_final_owner(backend,record,persist)
    if not backend.verified or not backend.released or not record.get('finalCloseInjected'):raise RuntimeError('Actual final-close retry proof missing')
    record['smokePassed']=True;persist();cases.append(record)
    receipt={'state':'WindowsOwnedPythonSmokePassed','schemaVersion':1,'sourceSha256':policy['candidateManifestSha256'],'supervisorSha256':policy['supervisorManifestSha256'],'casesPassed':len(cases),'cases':cases,'nativeSdkSpawned':False,'nativeFileTestsExecuted':False,'focused95Verified':False,'full1116Verified':False,'rootSdkPermitMinted':False,'helpersRemaining':[]}
    (evidence/'windows-smoke-receipt.json').write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8')
    return receipt


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--policy',required=True);parser.add_argument('--evidence',required=True);args=parser.parse_args()
    policy=load_policy(args.policy)
    module=load_supervisor(FIXED_ROOT,policy)
    run_smoke(module,args.evidence,policy)
    print('Actual Windows owned-Python lifecycle smoke passed. No SDK phase was started.')

if __name__=='__main__':main()
