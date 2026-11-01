import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / 'src' / 'backend.py'
class BackendTests(unittest.TestCase):
    def test_save_list_and_delete(self):
        with tempfile.TemporaryDirectory() as d:
            env = dict(os.environ, NETCENTER_DATA_DIR=d, PYTHONIOENCODING='utf-8')
            def call(body):
                p = subprocess.run([sys.executable, str(SCRIPT)], input=json.dumps(body), text=True, capture_output=True, encoding='utf-8', env=env, check=True)
                return json.loads(p.stdout)
            self.assertTrue(call({'action':'save', 'name':'测试', 'username':'abc', 'password':'secret'})['ok'])
            profiles = call({'action':'list'})['accounts']
            self.assertEqual(profiles[0]['username'], 'abc')
            self.assertNotIn('secret', json.dumps(profiles))
            self.assertTrue(call({'action':'delete', 'name':'测试'})['ok'])
            self.assertEqual(call({'action':'list'})['accounts'], [])
