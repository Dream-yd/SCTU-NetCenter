import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
SCRIPT=ROOT/'src'/'clear_hotspot_override.ps1'

class HotspotOverrideTests(unittest.TestCase):
    def run_script(self, state, keeper):
        return subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-WindowStyle','Hidden','-File',str(SCRIPT),'-StatePath',str(state),'-KeeperPath',str(keeper)],capture_output=True,timeout=20,creationflags=subprocess.CREATE_NO_WINDOW)

    def test_clear_preserves_other_fields_and_invokes_isolated_keeper(self):
        with tempfile.TemporaryDirectory(dir=ROOT/'artifacts') as d:
            root=Path(d); state=root/'state.json'; marker=root/'marker.txt'; keeper=root/'keeper.ps1'
            state.write_text(json.dumps({'Boot':'boot','Manual':'off','LastLink':'abc','LastOn':False,'Extra':'preserve'}),encoding='utf-8')
            keeper.write_text("[IO.File]::WriteAllText('%s','ran')\n" % marker,encoding='utf-8-sig')
            result=self.run_script(state,keeper)
            self.assertEqual(result.returncode,0,result.stderr.decode('utf-8',errors='replace'))
            value=json.loads(state.read_text(encoding='utf-8-sig'))
            self.assertEqual(value['Manual'],'')
            self.assertIsNone(value['LastOn'])
            self.assertEqual(value['Extra'],'preserve')
            self.assertTrue(marker.exists())
            backups=list(root.glob('state.json.before-clear-*.bak'))
            self.assertEqual(len(backups),1)
            self.assertEqual(json.loads(backups[0].read_text(encoding='utf-8-sig'))['Manual'],'off')

    def test_invalid_state_does_not_change_file_or_run_keeper(self):
        with tempfile.TemporaryDirectory(dir=ROOT/'artifacts') as d:
            root=Path(d); state=root/'state.json'; marker=root/'marker.txt'; keeper=root/'keeper.ps1'
            state.write_text('{bad',encoding='utf-8')
            keeper.write_text("[IO.File]::WriteAllText('%s','ran')\n" % marker,encoding='utf-8-sig')
            result=self.run_script(state,keeper)
            self.assertNotEqual(result.returncode,0)
            self.assertEqual(state.read_text(encoding='utf-8'),'{bad')
            self.assertFalse(marker.exists())
    def test_keeper_failure_is_reported_after_override_is_cleared(self):
        with tempfile.TemporaryDirectory(dir=ROOT/'artifacts') as d:
            root=Path(d); state=root/'state.json'; keeper=root/'keeper.ps1'
            state.write_text(json.dumps({'Boot':'b','Manual':'off','LastLink':'a','LastOn':False}),encoding='utf-8')
            keeper.write_text('exit 8\n',encoding='utf-8-sig')
            result=self.run_script(state,keeper)
            self.assertNotEqual(result.returncode,0)
            self.assertEqual(json.loads(state.read_text(encoding='utf-8-sig'))['Manual'],'')
            self.assertEqual(len(list(root.glob('state.json.before-clear-*.bak'))),1)
