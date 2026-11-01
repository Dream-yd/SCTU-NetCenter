import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / 'src' / 'backend.py'
class DefaultAccountTests(unittest.TestCase):
    def test_select_default_and_delete_clears_selection(self):
        with tempfile.TemporaryDirectory() as d:
            env = dict(os.environ, NETCENTER_DATA_DIR=d, PYTHONIOENCODING='utf-8')
            def call(body):
                p = subprocess.run([sys.executable, str(SCRIPT)], input=json.dumps(body), text=True, capture_output=True, encoding='utf-8', env=env, check=True)
                return json.loads(p.stdout)
            call({'action':'save','name':'one','username':'u','password':'p'})
            self.assertTrue(call({'action':'select_default','name':'one'})['ok'])
            self.assertEqual(call({'action':'list'})['default'], 'one')
            self.assertFalse(call({'action':'select_default','name':'missing'})['ok'])
            call({'action':'delete','name':'one'})
            self.assertIsNone(call({'action':'list'})['default'])
    def test_deleted_default_is_not_reactivated_after_recreation(self):
        with tempfile.TemporaryDirectory() as d:
            env = dict(os.environ, NETCENTER_DATA_DIR=d, PYTHONIOENCODING='utf-8')
            def call(body):
                p = subprocess.run([sys.executable, str(SCRIPT)], input=json.dumps(body), text=True, capture_output=True, encoding='utf-8', env=env, check=True)
                return json.loads(p.stdout)
            call({'action':'save','name':'one','username':'u','password':'p'})
            call({'action':'select_default','name':'one'})
            call({'action':'delete','name':'one'})
            call({'action':'save','name':'one','username':'u2','password':'p2'})
            self.assertIsNone(call({'action':'list'})['default'])
    def test_clear_default_keeps_account(self):
        with tempfile.TemporaryDirectory() as d:
            env = dict(os.environ, NETCENTER_DATA_DIR=d, PYTHONIOENCODING='utf-8')
            def call(body):
                p=subprocess.run([sys.executable,str(SCRIPT)],input=json.dumps(body),text=True,capture_output=True,encoding='utf-8',env=env,check=True)
                return json.loads(p.stdout)
            call({'action':'save','name':'one','username':'u','password':'p'})
            call({'action':'select_default','name':'one'})
            self.assertTrue(call({'action':'clear_default'})['ok'])
            result=call({'action':'list'})
            self.assertIsNone(result['default'])
            self.assertEqual(len(result['accounts']),1)
