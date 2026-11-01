import json
import os
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
class ConcurrentProfileTests(unittest.TestCase):
    def test_concurrent_profile_saves_keep_both_accounts(self):
        with tempfile.TemporaryDirectory(prefix='netcenter-concurrency-') as data:
            env=dict(os.environ,NETCENTER_DATA_DIR=data,PYTHONIOENCODING='utf-8')
            program='''
import sys,time
sys.path.insert(0,sys.argv[1])
import backend
original=backend.AccountStore._read
def slow_read(self):
 result=original(self)
 if self.path.name == "accounts.json": time.sleep(1)
 return result
backend.AccountStore._read=slow_read
result=backend.run({"action":"save","name":sys.argv[2],"username":sys.argv[2],"password":"dummy"})
if not result.get("ok"): sys.exit(1)
'''
            procs=[subprocess.Popen([sys.executable,'-c',program,str(ROOT/'src'),name],env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE) for name in ('one','two')]
            try:
                for proc in procs:
                    out,err=proc.communicate(timeout=15)
                    self.assertEqual(proc.returncode,0,(out+err).decode('utf-8',errors='replace'))
                items=json.loads((Path(data)/'accounts.json').read_text(encoding='utf-8'))
                self.assertEqual(set(items),{'one','two'})
            finally:
                for proc in procs:
                    if proc.poll() is None: proc.kill();proc.wait()
    def test_lock_released_when_writer_process_is_terminated(self):
        with tempfile.TemporaryDirectory(prefix='netcenter-lock-') as data:
            env=dict(os.environ,NETCENTER_DATA_DIR=data,PYTHONIOENCODING='utf-8')
            marker=Path(data)/'locked'
            code='''
import sys,time
from pathlib import Path
sys.path.insert(0,sys.argv[1])
import backend
with backend.profile_write_lock():
 Path(sys.argv[2]).write_text("ready")
 time.sleep(30)
'''
            proc=subprocess.Popen([sys.executable,'-c',code,str(ROOT/'src'),str(marker)],env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
            try:
                deadline=time.monotonic()+8
                while not marker.exists() and time.monotonic()<deadline:
                    if proc.poll() is not None: break
                    time.sleep(.05)
                self.assertTrue(marker.exists(),'writer did not acquire lock')
                proc.kill();proc.wait(timeout=5)
                p=subprocess.run([sys.executable,str(ROOT/'src'/'backend.py')],input=json.dumps({'action':'save','name':'after','username':'u','password':'p'}),encoding='utf-8',capture_output=True,env=env,timeout=5)
                self.assertEqual(p.returncode,0,p.stderr)
                self.assertTrue(json.loads(p.stdout)['ok'])
            finally:
                if proc.poll() is None:proc.kill();proc.wait()
                if proc.stdout and not proc.stdout.closed:proc.stdout.close()
                if proc.stderr and not proc.stderr.closed:proc.stderr.close()

    def test_delete_and_default_selection_cannot_resurrect_deleted_default(self):
        with tempfile.TemporaryDirectory(prefix='netcenter-race-') as data:
            env=dict(os.environ,NETCENTER_DATA_DIR=data,PYTHONIOENCODING='utf-8')
            def call(obj):
                p=subprocess.run([sys.executable,str(ROOT/'src'/'backend.py')],input=json.dumps(obj),encoding='utf-8',capture_output=True,env=env,timeout=10,check=True)
                return json.loads(p.stdout)
            call({'action':'save','name':'one','username':'u','password':'p'})
            marker=Path(data)/'read-done'
            program='''
import sys,time
from pathlib import Path
sys.path.insert(0,sys.argv[1])
import backend
original=backend._run
def delayed(req):
 result=original(req)
 if req.get("action")=="list":
  Path(sys.argv[2]).write_text("ready")
  time.sleep(1.5)
 return result
backend._run=delayed
backend.run({"action":"delete","name":"one"})
'''
            proc=subprocess.Popen([sys.executable,'-c',program,str(ROOT/'src'),str(marker)],env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
            try:
                deadline=time.monotonic()+8
                while not marker.exists() and time.monotonic()<deadline:
                    if proc.poll() is not None:break
                    time.sleep(.05)
                self.assertTrue(marker.exists(),'delete did not reach the race point')
                selected=call({'action':'select_default','name':'one'})
                out,err=proc.communicate(timeout=10)
                self.assertEqual(proc.returncode,0,(out+err).decode('utf-8',errors='replace'))
                self.assertFalse(selected['ok'])
                call({'action':'save','name':'one','username':'new','password':'new-secret'})
                self.assertIsNone(call({'action':'list'})['default'])
            finally:
                if proc.poll() is None:proc.kill();proc.wait()
                if proc.stdout and not proc.stdout.closed:proc.stdout.close()
                if proc.stderr and not proc.stderr.closed:proc.stderr.close()
