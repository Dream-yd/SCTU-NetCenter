import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
class AutomaticTests(unittest.TestCase):
    def test_auto_uses_default_profile_and_never_writes_password_to_config(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d); campus=root/'campus'; campus.mkdir(); data=root/'data'
            (campus/'config.json').write_text('{}', encoding='utf-8')
            (campus/'campus_auth.py').write_text('''\ndef acquire_lock(): return True\ndef release_lock(): pass\ndef load_config(path): return {}\ndef once(cfg):\n    assert cfg["user_id"] == "u" and cfg["password"] == "p"\n    return 0, "authorized"\n''',encoding='utf-8')
            env=dict(os.environ, NETCENTER_DATA_DIR=str(data), NETCENTER_CAMPUS_DIR=str(campus), PYTHONIOENCODING='utf-8')
            def call(body):
                p=subprocess.run([sys.executable,str(ROOT/'src'/'backend.py')],input=json.dumps(body),encoding='utf-8',capture_output=True,env=env,check=True)
                return json.loads(p.stdout)
            call({'action':'save','name':'a','username':'u','password':'p'})
            call({'action':'select_default','name':'a'})
            self.assertTrue(call({'action':'auto'})['ok'])
            self.assertEqual((campus/'config.json').read_text(encoding='utf-8'),'{}')

    def test_auto_without_default_does_nothing(self):
        with tempfile.TemporaryDirectory() as d:
            env=dict(os.environ, NETCENTER_DATA_DIR=d, NETCENTER_CAMPUS_DIR=d, PYTHONIOENCODING='utf-8')
            p=subprocess.run([sys.executable,str(ROOT/'src'/'backend.py')],input='{"action":"auto"}',encoding='utf-8',capture_output=True,env=env,check=True)
            self.assertFalse(json.loads(p.stdout)['ok'])
    def test_headless_runner_executes_without_stdin(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d); campus=root/'campus'; campus.mkdir(); data=root/'data'
            (campus/'config.json').write_text('{}', encoding='utf-8')
            (campus/'campus_auth.py').write_text('''\ndef acquire_lock(): return True\ndef release_lock(): pass\ndef load_config(path): return {}\ndef once(cfg): return 0, "authorized"\n''',encoding='utf-8')
            env=dict(os.environ, NETCENTER_DATA_DIR=str(data), NETCENTER_CAMPUS_DIR=str(campus), PYTHONIOENCODING='utf-8')
            def call(body):
                return subprocess.run([sys.executable,str(ROOT/'src'/'backend.py')], input=json.dumps(body), encoding='utf-8', capture_output=True, env=env, check=True)
            call({'action':'save','name':'a','username':'u','password':'p'})
            call({'action':'select_default','name':'a'})
            p=subprocess.run([sys.executable,str(ROOT/'src'/'auto_auth.py')],stdin=subprocess.DEVNULL,capture_output=True,env=env,timeout=20)
            self.assertEqual(p.returncode,0,p.stderr.decode(errors='replace'))
    def test_default_store_is_in_shared_user_profile(self):
        env=dict(os.environ, PYTHONIOENCODING='utf-8')
        env.pop('NETCENTER_DATA_DIR',None)
        code="import sys;sys.path.insert(0,'src');import backend;print(backend.DATA)"
        p=subprocess.run([sys.executable,'-c',code],cwd=ROOT,encoding='utf-8',capture_output=True,env=env,check=True)
        self.assertEqual(Path(p.stdout.strip()),Path(env['USERPROFILE'])/'.netcenter-next')
