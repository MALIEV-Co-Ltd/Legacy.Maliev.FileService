"""Bounded CURRENT intake for the existing original File native route."""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import sys
import zipfile

from sealed_source_capsule import canonical_path, digest, fetch_git_blob, parse_json, reject_links, write_new

REPOSITORY='MALIEV-Co-Ltd/Legacy.Maliev.FileService'
CAPSULE_OID='fc637c2d4d2c90d8291c7689b06e1c9b27ed824f'
CAPSULE_SHA='8e1002507f45a7d1255ba2ac3ac1ef65ee9d0fec37718fbe65a2ab22c1d5c08c'
CAPSULE_BYTES=444086
BASELINE_OID='af25652fe32d51d33d737ff7e90b63a0adf2b4d5'
BASELINE_SHA='bb7b7a88aeee4a8b52e19aa7b07861cfe5dd0aa2824c995c93a57bcf2e4f5630'
BASELINE_BYTES=799749
INDEX_SHA='2ae3c0383a3ea96510262b321e4b6c9ff97a374a78274039d3be4683c9558281'
SUPERVISOR_SHA='e413a49f152c05fd218119b5ee05d1ec1f724414bb0da0ce0bf2d4d07c12e2ad'

def decode(capsule,index_raw,baseline):
    if len(capsule)!=CAPSULE_BYTES or digest(capsule)!=CAPSULE_SHA:raise ValueError('Exact CURRENT capsule required')
    if len(baseline)!=BASELINE_BYTES or digest(baseline)!=BASELINE_SHA:raise ValueError('Exact retained baseline required')
    if len(index_raw)>128*1024 or digest(index_raw)!=INDEX_SHA:raise ValueError('Exact protected CURRENT index required')
    index=parse_json(index_raw)
    if set(index)!={'schemaVersion','files'} or index['schemaVersion']!=1 or len(index['files'])!=170:raise ValueError('Closed CURRENT index required')
    rows={canonical_path(row['path']):row for row in index['files']}
    if len(rows)!=170 or sum(row['bytes'] for row in rows.values())!=1497320:raise ValueError('Closed original intake quotas required')
    result={}
    with zipfile.ZipFile(io.BytesIO(capsule)) as archive:
        entries=archive.infolist()
        if archive.comment or len(entries)!=170 or len({entry.filename for entry in entries})!=170:raise ValueError('Closed ordinary capsule entries required')
        for entry in entries:
            name=canonical_path(entry.filename)
            if name not in rows or entry.is_dir() or entry.extra or entry.comment or entry.flag_bits & 1 or entry.external_attr != 0o100644<<16 or entry.compress_type!=zipfile.ZIP_DEFLATED:raise ValueError('Canonical ordinary capsule entry required')
            row=rows[name]
            if entry.file_size!=row['bytes'] or entry.file_size>2*1024*1024:raise ValueError('Bounded CURRENT entry required')
            with archive.open(entry) as stream:raw=stream.read(row['bytes']+1)
            if len(raw)!=row['bytes'] or digest(raw)!=row['sha256']:raise ValueError('Actual CURRENT entry differs')
            result[name]=raw
    if set(result)!=set(rows):raise ValueError('Complete CURRENT capsule required')
    return result

def run(files,baseline,base,root,evidence,mode,bundle=None,bundle_sha=None):
    from materialize_file_native import load_current_policy,current_git_read,materialize_current,cleanup_current_materialization
    from qualify_file_native import current_route_main
    root=Path(root);evidence=Path(evidence)
    reject_links(root);reject_links(evidence)
    if root.exists():raise ValueError('Fresh exclusive CURRENT transport root required')
    if mode=='source-association' and (bundle or bundle_sha):raise ValueError('Source route cannot consume grants')
    # Frozen exact loader gates before resource acquisition and cloud grant reads.
    raw=files['input/current-source-policy.json'];policy=load_current_policy(raw)
    if mode=='qualification':
        raise ValueError('CURRENT transport remains source-only: independently reviewed execution-policy successor required')
    base_files,metadata=current_git_read(base,policy)
    created=[];receipts={};roots={}
    root_created=False;root_identity=None;evidence_ready=False;primary=None
    cleanup_failures=[]
    def identity():
        reject_links(root)
        metadata=root.stat()
        if not root.is_dir():raise RuntimeError('Owned transport root type changed')
        return (metadata.st_dev,metadata.st_ino)
    try:
        # Ownership state exists before either acquisition can fail.
        # The parent already exists; this task owns only the exclusive leaf.
        root.mkdir(mode=0o700);root_created=True;root_identity=identity()
        evidence.mkdir(parents=True,exist_ok=True);evidence_ready=True
        for name,data in files.items():created.append(write_new(root,name,data))
        created.append(write_new(root,'supervisor/supervisor-manifest.json',files['input/supervisor-manifest.json']))
        created.append(write_new(root,'input/baseline-artifact.zip',baseline))
        select=lambda key,prefix:{row['path']:files['input/'+prefix+'/'+row['path']] for row in policy[key]}
        posts=select('candidateFiles',policy['candidateRoot'])
        dependencies=select('dependencyFiles',policy['dependencyRoot'])
        fixtures=select('fixtureFiles',policy['fixtureRoot'])
        consumers=select('producerConsumerFiles',policy['producerConsumerRoot'])
        for color in ('RED','GREEN'):
            target=root/('file-current-delete-move-route-'+color.lower());roots[color]=target
            receipt=materialize_current(target,raw,base_files,metadata,posts,dependencies,baseline,color,fixtures,consumers,files['input/dependency-manifest.json'])
            receipts[color]=receipt['receiptSha256']
            write_new(evidence,color.lower()+'-materialization-receipt.json',(target/'materialization-receipt.json').read_bytes())
        current_route_main(['--current-policy',str(root/'input/current-source-policy.json'),
            '--current-red-root',str(roots['RED']),'--current-green-root',str(roots['GREEN']),
            '--red-receipt-sha256',receipts['RED'],'--green-receipt-sha256',receipts['GREEN'],
            '--supervisor-manifest',str(root/'supervisor/supervisor-manifest.json'),
            '--supervisor-sha256',SUPERVISOR_SHA,'--mode','source-association',
            '--association',str(evidence/'source-association.json'),'--evidence',str(evidence)])
    except BaseException as error:
        primary=error
        raise
    finally:
        # Reverify each complete reconstructed source before exact disposal.
        # Partial/changed reconstruction is retained for recovery, never blindly deleted.
        root_verified=False
        if root_created:
            try:
                if root_identity is None or identity()!=root_identity:raise RuntimeError('Owned root identity unproved')
                root_verified=True
            except BaseException:cleanup_failures.append('transport-root-identity')
        if root_verified:
            for color,receipt in receipts.items():
                try:cleanup_current_materialization(roots[color],raw,receipt)
                except BaseException:cleanup_failures.append('source-cleanup-'+color)
            retained=False
            for color,path in roots.items():
                try:
                    reject_links(path)
                    if path.exists():retained=True
                except BaseException:retained=True
            if retained:cleanup_failures.append('source-root-retained')
            else:
                # A rejected root is retained with its recovery inputs. Once all
                # roots are absent, dispose independent verified inputs safely.
                for path in reversed(created):
                    try:
                        if identity()!=root_identity:raise RuntimeError('Owned root identity changed')
                        name=path.relative_to(root).as_posix()
                        expected=baseline if name=='input/baseline-artifact.zip' else files['input/supervisor-manifest.json'] if name=='supervisor/supervisor-manifest.json' else files[name]
                        reject_links(path)
                        if path.read_bytes()!=expected:raise RuntimeError('Changed transport source retained')
                        path.unlink()
                    except BaseException:cleanup_failures.append('transport-input-cleanup')
                if not cleanup_failures:
                    try:
                        for directory in sorted(root.rglob('*'),key=lambda item:len(item.parts),reverse=True):
                            if identity()!=root_identity:raise RuntimeError('Owned root identity changed')
                            reject_links(directory);directory.rmdir()
                        if identity()!=root_identity:raise RuntimeError('Owned root identity changed')
                        root.rmdir()
                    except BaseException:cleanup_failures.append('transport-directory-cleanup')
        if evidence_ready:
            try:
                write_new(evidence,'transport-cleanup.json',(json.dumps({'ownedRootAbsent':not root.exists(),
                    'cleanupFailures':cleanup_failures,'SDKStarted':False,'nativeAcceptance':False})+'\n').encode())
            except BaseException:cleanup_failures.append('cleanup-evidence-write')
        if cleanup_failures:
            failure=primary if primary is not None else RuntimeError('CURRENT transport cleanup not proven')
            try:failure.add_note('Cleanup failures: '+','.join(cleanup_failures))
            except BaseException:pass
            if primary is None:raise failure

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--capsule-blob',required=True);parser.add_argument('--baseline-blob',required=True)
    parser.add_argument('--index',required=True);parser.add_argument('--base-checkout',required=True)
    parser.add_argument('--root',required=True);parser.add_argument('--evidence',required=True)
    parser.add_argument('--mode',choices=('source-association','qualification'),default='source-association')
    parser.add_argument('--grant-bundle-blob');parser.add_argument('--grant-bundle-sha256')
    args=parser.parse_args()
    if args.capsule_blob!=CAPSULE_OID or args.baseline_blob!=BASELINE_OID:raise ValueError('Exact same-repository CURRENT input blob IDs required')
    index=Path(args.index);reject_links(index)
    if index.stat().st_size>128*1024:raise ValueError('Bounded protected index required')
    capsule=fetch_git_blob(REPOSITORY,CAPSULE_OID)
    baseline=fetch_git_blob(REPOSITORY,BASELINE_OID)
    files=decode(capsule,index.read_bytes(),baseline)
    run(files,baseline,args.base_checkout,args.root,args.evidence,args.mode,args.grant_bundle_blob,args.grant_bundle_sha256)

if __name__=='__main__':main()
