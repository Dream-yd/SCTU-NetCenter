import subprocess
import unittest
from pathlib import Path

class MotionTests(unittest.TestCase):
    def test_close_button_animates_before_hiding(self):
        root = Path(__file__).resolve().parents[1]
        script = root / 'tests' / 'verify_motion.ps1'
        result = subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(script)],capture_output=True,timeout=35)
        self.assertEqual(result.returncode,0,result.stdout.decode('utf-8',errors='replace') + result.stderr.decode('utf-8',errors='replace'))
