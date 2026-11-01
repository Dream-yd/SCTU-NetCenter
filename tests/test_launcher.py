import os
import subprocess
import unittest
from pathlib import Path

class LauncherTests(unittest.TestCase):
    def test_launcher_exits_but_tray_remains_running(self):
        script=Path(__file__).resolve().parent/'verify_launcher.ps1'
        p=subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(script)],capture_output=True,timeout=35,env=os.environ.copy())
        self.assertEqual(p.returncode,0,p.stdout.decode('utf-8',errors='replace')+p.stderr.decode('utf-8',errors='replace'))
