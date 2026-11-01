import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]

class ImportTests(unittest.TestCase):
    def test_import_existing_credentials_without_exposing_or_overwriting(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d); campus=root/'campus'; campus.mkdir(); data=root/'data'
            (campus/'config.json').write_text(json.dumps({'user_id':'student','password':'campus-secret'}),encoding='utf-8')
            env=dict(os.environ,NETCENTER_DATA_DIR=str(data),NETCENTER_CAMPUS_DIR=str(campus),PYTHONIOENCODING='utf-8')
            def call(obj):
                p=subprocess.run([sys.executable,str(ROOT/'src'/'backend.py')],input=json.dumps(obj),encoding='utf-8',capture_output=True,env=env,check=True)
                self.assertNotIn('campus-secret',p.stdout)
                return json.loads(p.stdout)
            self.assertTrue(call({'action':'import_existing'})['ok'])
            listing=call({'action':'list'})
            self.assertEqual(listing['default'],'原校园网账号')
            self.assertEqual(listing['accounts'][0]['username'],'student')
            self.assertNotIn('campus-secret',(data/'accounts.json').read_text(encoding='utf-8'))
            call({'action':'save','name':'原校园网账号','username':'changed','password':'different'})
            self.assertFalse(call({'action':'import_existing'})['ok'])
            self.assertEqual(call({'action':'list'})['accounts'][0]['username'],'changed')
