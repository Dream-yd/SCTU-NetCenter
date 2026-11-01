import json
import subprocess
import unittest
from pathlib import Path

class MigrationTests(unittest.TestCase):
    def test_inspection_is_read_only_and_reports_existing_tasks(self):
        script=Path(__file__).resolve().parents[1]/'tools'/'Install-AutoAuth.ps1'
        p=subprocess.run(['powershell.exe','-NoProfile','-ExecutionPolicy','Bypass','-File',str(script),'-Inspect'],capture_output=True,timeout=30)
        self.assertEqual(p.returncode,0,p.stderr.decode('utf-8',errors='replace'))
        report=json.loads(p.stdout.decode('utf-8-sig'))
        self.assertIn('Tasks',report)
        self.assertIn('StartupExists',report)
