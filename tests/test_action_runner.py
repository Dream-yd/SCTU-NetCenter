import subprocess
import os
import sys
import unittest
from pathlib import Path

class ActionRunnerIntegrationTests(unittest.TestCase):
    def test_powershell_actions_return_unicode_and_exit_status(self):
        project=Path(__file__).resolve().parent/'ActionRunnerChecks.csproj'
        p=subprocess.run(['dotnet','run','--project',str(project)],capture_output=True,timeout=60,creationflags=subprocess.CREATE_NO_WINDOW,env=dict(os.environ, NETCENTER_TEST_PYTHON=sys.executable))
        self.assertEqual(p.returncode,0,p.stdout.decode('utf-8',errors='replace')+p.stderr.decode('utf-8',errors='replace'))
        self.assertIn(b'Action runner checks passed',p.stdout)
