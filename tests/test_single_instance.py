import os
import subprocess
import tempfile
import time
import unittest
from pathlib import Path

class SingleInstanceTests(unittest.TestCase):
    def test_second_instance_exits_without_starting_another_tray(self):
        exe=Path(os.environ.get('NETCENTER_TEST_EXE') or Path(__file__).resolve().parents[1]/'app/bin/Debug/net8.0-windows/NetCenter.exe')
        with tempfile.TemporaryDirectory() as data:
            env=dict(os.environ, NETCENTER_DATA_DIR=data, NETCENTER_INSTANCE_KEY='test-'+str(os.getpid()))
            first=subprocess.Popen([str(exe)],env=env,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
            second=None
            try:
                time.sleep(2)
                self.assertIsNone(first.poll(),'first instance exited unexpectedly')
                second=subprocess.Popen([str(exe)],env=env,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
                try: second.wait(timeout=5)
                except subprocess.TimeoutExpired: self.fail('second instance remained running')
                self.assertEqual(second.returncode,0)
            finally:
                for p in (second,first):
                    if p and p.poll() is None:
                        subprocess.run(['taskkill','/PID',str(p.pid),'/T','/F'],capture_output=True)
                        p.wait(timeout=10)
