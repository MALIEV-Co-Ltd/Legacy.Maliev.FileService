import base64
import hashlib
import http.client
import socket
import io
import json
from pathlib import Path
import stat
import struct
import sys
import tempfile
import unittest
import warnings
from types import SimpleNamespace
from unittest.mock import patch
import zipfile
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import sealed_source_capsule as s


def capsule(entries):
    out=io.BytesIO()
    with zipfile.ZipFile(out,'w',zipfile.ZIP_DEFLATED) as archive:
        with warnings.catch_warnings():
            warnings.simplefilter('ignore',UserWarning)
            for name,raw,mode in entries:
                info=zipfile.ZipInfo(name,(2026,10,8,0,0,0));info.compress_type=zipfile.ZIP_DEFLATED;info.create_system=3;info.external_attr=mode<<16
                archive.writestr(info,raw)
    data=out.getvalue();rows=[]
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        for info in archive.infolist():
            raw=next(raw for name,raw,_ in entries if name==info.filename)
            rows.append({'path':info.filename,'bytes':len(raw),'sha256':s.digest(raw),'crc32':info.CRC,'compressedBytes':info.compress_size,'createSystem':info.create_system,'externalAttributes':info.external_attr,'flags':info.flag_bits,'compression':info.compress_type,'dateTime':list(info.date_time)})
    return data,rows


class RawCapsuleTests(unittest.TestCase):
    def setUp(self):self.data,self.rows=capsule([('raw/source.txt',b'one\r\ntwo\r\n',stat.S_IFREG|0o600)])
    def validate(self,data=None,rows=None):
        data=self.data if data is None else data
        return s.validate_zip(data,s.digest(data),len(data),self.rows if rows is None else rows)
    def test_raw_crlf_preserved(self):self.assertEqual(self.validate()['raw/source.txt'],b'one\r\ntwo\r\n')
    def test_digest_binding(self):
        with self.assertRaises(ValueError):s.validate_zip(self.data,'0'*64,len(self.data),self.rows)
    def test_size_binding(self):
        with self.assertRaises(ValueError):s.validate_zip(self.data,s.digest(self.data),len(self.data)+1,self.rows)
    def test_missing_entry(self):
        with self.assertRaises(ValueError):self.validate(rows=self.rows+[dict(self.rows[0],path='missing')])
    def test_extra_entry(self):
        data,_=capsule([('raw/source.txt',b'one\r\ntwo\r\n',stat.S_IFREG|0o600),('extra',b'x',stat.S_IFREG|0o600)])
        with self.assertRaises(ValueError):self.validate(data=data)
    def test_duplicate_entry(self):
        data,rows=capsule([('same',b'x',stat.S_IFREG|0o600),('same',b'x',stat.S_IFREG|0o600)])
        with self.assertRaises(ValueError):self.validate(data,rows)
    def test_case_alias(self):
        data,rows=capsule([('Case',b'x',stat.S_IFREG|0o600),('case',b'x',stat.S_IFREG|0o600)])
        with self.assertRaises(ValueError):self.validate(data,rows)
    def test_symlink_header(self):
        data,rows=capsule([('link',b'x',stat.S_IFLNK|0o777)])
        with self.assertRaises(ValueError):self.validate(data,rows)
    def test_directory_header(self):
        data,rows=capsule([('dir',b'',stat.S_IFDIR|0o755)])
        with self.assertRaises(ValueError):self.validate(data,rows)
    def test_wrong_header(self):
        with self.assertRaises(ValueError):self.validate(rows=[dict(self.rows[0],flags=8)])
    def test_wrong_raw_hash(self):
        with self.assertRaises(ValueError):self.validate(rows=[dict(self.rows[0],sha256='0'*64)])
    def test_crc_corruption(self):
        data=bytearray(self.data);central=data.index(b'PK\x01\x02');struct.pack_into('<I',data,14,0);struct.pack_into('<I',data,central+16,0)
        with self.assertRaises((ValueError,zipfile.BadZipFile)):self.validate(bytes(data),[dict(self.rows[0],crc32=0)])
    def test_empty_inventory(self):
        with self.assertRaises(ValueError):self.validate(rows=[])
    def test_member_bound(self):
        with self.assertRaises(ValueError):self.validate(rows=[dict(self.rows[0],bytes=s.MAX_FILE_BYTES+1)])
    def test_path_rejections(self):
        for name in ['../x','/x','a//b','a/./b','C:/x','a\\b','.git/config','a/CON.txt','a/end.','a/end ','a/e\u0301']:
            with self.subTest(name=name),self.assertRaises(ValueError):s.canonical_path(name)
    def test_windows_reserved_superscripts_and_illegal_characters(self):
        for name in ['COM\u00b9.txt', 'LPT\u00b2', 'nested/com\u00b3.ext', 'lpt\u00b9.txt', *('a'+c+'b' for c in '<>"|?*')]:
            with self.subTest(name=name), self.assertRaises(ValueError):
                s.canonical_path(name)
    def test_total_fetch_timeout_terminates_exact_owned_worker(self):
        events = []
        class Worker:
            def __enter__(self): return self
            def __exit__(self, *args): events.append('handles-closed')
            def communicate(self, timeout):
                self.timeout = timeout
                raise s.subprocess.TimeoutExpired('owned', timeout)
            def poll(self): return None
            def kill(self): events.append('exact-handle-kill')
            def wait(self, timeout): events.append('exit-verified'); return 1
        with patch.object(s.subprocess, 'Popen', return_value=Worker()), self.assertRaises(TimeoutError):
            s.fetch_git_blob('owner/repository', 'a'*40)
        self.assertEqual(events, ['exact-handle-kill', 'exit-verified'])
    def test_fetch_worker_failure_never_decodes_provider_output(self):
        class Worker:
            returncode = 1
            def __enter__(self): return self
            def __exit__(self, *args): pass
            def communicate(self, timeout): return b'provider failure', b'sensitive synthetic header'
            def poll(self): return 1
            def wait(self, timeout): return 1
        with patch.object(s.subprocess, 'Popen', return_value=Worker()), self.assertRaisesRegex(ValueError, '^Git blob fetch worker failed$'):
            s.fetch_git_blob('owner/repository', 'a'*40)
    def test_actual_http_response_fixed_chunked_and_connection_eof(self):
        cases = [
            b'HTTP/1.1 200 OK\r\nContent-Length: 6\r\n\r\nonetwo',
            b'HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n3\r\none\r\n3\r\ntwo\r\n0\r\n\r\n',
            b'HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nonetwo',
        ]
        for wire in cases:
            with self.subTest(wire=wire):
                reader, writer = socket.socketpair()
                try:
                    writer.sendall(wire); writer.shutdown(socket.SHUT_WR)
                    with http.client.HTTPResponse(reader) as response:
                        response.begin()
                        self.assertEqual(s.read_deadline(response, s.time.monotonic()+2), b'onetwo')
                        self.assertTrue(response.isclosed())
                finally:
                    reader.close(); writer.close()
    def test_actual_fetch_function_recovers_kill_wait_reader_and_pipe_faults(self):
        events = []; faults = {'kill':1, 'wait':1, 'join':1, 'close':1}
        class Pipe:
            closed = False
            def close(self):
                events.append('close')
                if faults['close']:
                    faults['close'] -= 1; raise OSError('close fault')
                self.closed = True
        class Reader:
            def join(self, timeout):
                events.append('join')
                if faults['join']:
                    faults['join'] -= 1; raise OSError('reader fault')
            def is_alive(self): return False
        class Worker:
            stdout = Pipe(); stderr = Pipe(); _stdout_thread = Reader()
            def communicate(self, timeout): raise s.subprocess.TimeoutExpired('owned', timeout)
            def poll(self): return None
            def kill(self):
                events.append('kill')
                if faults['kill']:
                    faults['kill'] -= 1; raise OSError('kill fault')
            def wait(self, timeout):
                self.assert_timeout = timeout; events.append('wait')
                if faults['wait']:
                    faults['wait'] -= 1; raise OSError('wait fault')
                return 1
        worker = Worker()
        with patch.object(s.subprocess, 'Popen', return_value=worker), patch.object(s.time, 'sleep'), self.assertRaises(TimeoutError):
            s.fetch_git_blob('owner/repository', 'a'*40)
        self.assertEqual(events[:2], ['kill', 'wait'])
        self.assertEqual(worker.assert_timeout, 1)
        self.assertGreaterEqual(events.count('wait'), 2)
        self.assertGreaterEqual(events.count('join'), 2)
        self.assertTrue(worker.stdout.closed and worker.stderr.closed)
        self.assertGreater(events.index('close'), events.index('wait'))
    def test_fetch_trickle_cannot_extend_total_deadline(self):
        now = [0.0]; timeouts = []
        class Response:
            def isclosed(self): return False
            fp = SimpleNamespace(raw=SimpleNamespace(_sock=SimpleNamespace(settimeout=timeouts.append)))
            def read1(self, size):
                now[0] += 3.0
                return b'x'
        with self.assertRaises(TimeoutError):
            s.read_deadline(Response(), 5.0, lambda: now[0])
        self.assertEqual(timeouts, [5.0, 2.0])
    def test_fetch_budget_includes_open_elapsed_time(self):
        class Response:
            def read1(self, size):
                self.fail('must not read after deadline')
        with self.assertRaises(TimeoutError):
            s.read_deadline(Response(), 5.0, lambda: 5.1)
    def test_fetch_eof_returns_exact_bytes(self):
        parts = iter([b'one', b'two', b''])
        class Response:
            def isclosed(self): return False
            fp = SimpleNamespace(raw=SimpleNamespace(_sock=SimpleNamespace(settimeout=lambda _: None)))
            def read1(self, size): return next(parts)
        self.assertEqual(s.read_deadline(Response(), 5.0, lambda: 0.0), b'onetwo')
    def test_json_duplicates(self):
        with self.assertRaises(ValueError):s.parse_json(b'{"a":1,"a":2}')
    def test_json_nonfinite(self):
        with self.assertRaises(ValueError):s.parse_json(b'{"a":NaN}')
    def test_git_blob_binding(self):
        raw=b'raw\r\n';oid=hashlib.sha1(b'blob '+str(len(raw)).encode()+b'\0'+raw).hexdigest()
        payload={'sha':oid,'encoding':'base64','size':len(raw),'content':base64.b64encode(raw).decode()+'\n'}
        self.assertEqual(s.decode_git_blob(json.dumps(payload).encode(),oid),raw)
        payload['size']+=1
        with self.assertRaises(ValueError):s.decode_git_blob(json.dumps(payload).encode(),oid)
    def test_git_blob_bad_identity(self):
        with self.assertRaises(ValueError):s.decode_git_blob(b'{}','not-a-sha')
    def test_git_blob_bad_base64(self):
        payload={'sha':'a'*40,'encoding':'base64','size':1,'content':'!!!!'}
        with self.assertRaises(ValueError):s.decode_git_blob(json.dumps(payload).encode(),'a'*40)
    def test_exclusive_write_readback(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);target=s.write_new(root,'raw/a',b'\r\n')
            self.assertEqual(target.read_bytes(),b'\r\n')
            with self.assertRaises(FileExistsError):s.write_new(root,'raw/a',b'replacement')
    def test_symlink_write_denied(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);real=root/'real';real.mkdir();link=root/'link'
            try:link.symlink_to(real,target_is_directory=True)
            except OSError as error:self.skipTest('OS symlink creation unavailable: '+type(error).__name__)
            with self.assertRaises(ValueError):s.write_new(root,'link/a',b'x')

if __name__=='__main__':unittest.main()
