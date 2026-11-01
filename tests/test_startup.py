import ctypes
import os
import subprocess
import tempfile
import time
import unittest
from pathlib import Path

class StartupTests(unittest.TestCase):
    def test_background_start_has_no_visible_window(self):
        exe=Path(os.environ.get('NETCENTER_TEST_EXE') or Path(__file__).resolve().parents[1]/'artifacts/validation-build/NetCenter.exe')
        with tempfile.TemporaryDirectory() as data:
            env=dict(os.environ, NETCENTER_DATA_DIR=data, NETCENTER_INSTANCE_KEY='background-'+str(os.getpid()))
            p=subprocess.Popen([str(exe),'--background'],env=env,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
            try:
                time.sleep(4)
                self.assertIsNone(p.poll())
                user32=ctypes.windll.user32
                found=[]
                @ctypes.WINFUNCTYPE(ctypes.c_bool,ctypes.c_void_p,ctypes.c_void_p)
                def visit(hwnd,lparam):
                    pid=ctypes.c_ulong()
                    user32.GetWindowThreadProcessId(hwnd,ctypes.byref(pid))
                    if pid.value==p.pid and user32.IsWindowVisible(hwnd): found.append(hwnd)
                    return True
                user32.EnumWindows(visit,0)
                self.assertEqual(found,[],'background startup showed a window')
            finally:
                subprocess.run(['taskkill','/PID',str(p.pid),'/T','/F'],capture_output=True)
                p.wait(timeout=10)
