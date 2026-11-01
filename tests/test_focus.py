import os
import subprocess
import tempfile
import unittest
from pathlib import Path

class FocusTests(unittest.TestCase):
    def test_clicking_another_window_hides_the_panel(self):
        with tempfile.TemporaryDirectory(prefix='netcenter-focus-') as data:
            env=dict(os.environ, NETCENTER_DATA_DIR=data, NETCENTER_INSTANCE_KEY='focus-'+str(os.getpid()))
            script=Path(__file__).resolve().parent/'verify_focus.ps1'
            p=subprocess.run(['powershell.exe','-STA','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(script)],capture_output=True,timeout=30,env=env)
            self.assertEqual(p.returncode,0,p.stdout.decode('utf-8',errors='replace')+p.stderr.decode('utf-8',errors='replace'))
