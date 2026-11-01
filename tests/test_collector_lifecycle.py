import os
import subprocess
import tempfile
import time
import unittest
from pathlib import Path

class CollectorLifecycleTests(unittest.TestCase):
    def test_collector_exits_when_parent_is_forcibly_terminated(self):
        exe=Path(os.environ.get('NETCENTER_TEST_EXE') or Path(__file__).resolve().parents[1]/'app/bin/Debug/net8.0-windows/NetCenter.exe')
        with tempfile.TemporaryDirectory() as directory:
            env=dict(os.environ, NETCENTER_DATA_DIR=directory, NETCENTER_INSTANCE_KEY='lifecycle-'+str(os.getpid()))
            parent=subprocess.Popen([str(exe),'--background'],env=env,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
            child_pid=None
            def powershell(command):
                return subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-Command',command],capture_output=True,text=True,timeout=15).stdout.strip()
            try:
                for _ in range(30):
                    result=powershell("Get-CimInstance Win32_Process -Filter 'ParentProcessId=%d' | Where-Object {$_.CommandLine -match 'collector.ps1'} | Select-Object -ExpandProperty ProcessId" % parent.pid)
                    if result:
                        child_pid=int(result.splitlines()[0]); break
                    time.sleep(.2)
                self.assertIsNotNone(child_pid,'collector did not start')
                time.sleep(3)
                self.assertTrue(powershell("Get-Process -Id %d -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id" % child_pid), 'collector exited while parent was alive')
                parent.kill(); parent.wait(timeout=5)
                for _ in range(30):
                    if not powershell("Get-Process -Id %d -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id" % child_pid): break
                    time.sleep(.2)
                else: self.fail('collector survived parent termination')
            finally:
                if parent.poll() is None: parent.kill(); parent.wait(timeout=5)
                if child_pid:
                    powershell("Stop-Process -Id %d -Force -ErrorAction SilentlyContinue" % child_pid)
