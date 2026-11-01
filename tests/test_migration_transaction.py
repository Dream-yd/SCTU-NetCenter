import subprocess
import unittest
from pathlib import Path

class MigrationTransactionTests(unittest.TestCase):
    def test_simulated_migration_and_failure_rollback(self):
        script=Path(__file__).resolve().parent/'verify_migration.ps1'
        p=subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(script)],capture_output=True,timeout=30)
        self.assertEqual(p.returncode,0,p.stdout.decode('utf-8',errors='replace')+p.stderr.decode('utf-8',errors='replace'))
