import os
import json
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

class AccountUiTests(unittest.TestCase):
    def test_two_profiles_can_be_managed_without_touching_real_accounts(self):
        with tempfile.TemporaryDirectory(prefix='netcenter-ui-test-') as data:
            root=Path(data); campus=root/'campus'; campus.mkdir(); store=root/'store'
            (campus/'config.json').write_text('{}',encoding='utf-8')
            (campus/'campus_auth.py').write_text('def acquire_lock(): return True\ndef release_lock(): pass\ndef load_config(path): return {}\ndef once(cfg): return 0, \"simulated\"\n',encoding='utf-8')
            env=dict(os.environ,NETCENTER_DATA_DIR=str(store),NETCENTER_CAMPUS_DIR=str(campus),NETCENTER_INSTANCE_KEY='ui-test-'+str(os.getpid()))
            script=Path(__file__).resolve().parent/'verify_accounts_ui.ps1'
            p=subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(script)],capture_output=True,timeout=60,env=env)
            self.assertEqual(p.returncode,0,p.stdout.decode('utf-8',errors='replace')+p.stderr.decode('utf-8',errors='replace'))

    def test_corrected_default_profile_retries_after_authentication_failure(self):
        self.check_retry_after_profile_update(False)

    def test_profile_update_during_authentication_retries_after_old_failure(self):
        self.check_retry_after_profile_update(True)

    def check_retry_after_profile_update(self, delayed):
        exe = Path(os.environ['NETCENTER_TEST_EXE'])
        with tempfile.TemporaryDirectory(prefix='netcenter-ui-retry-') as data:
            root = Path(data); campus = root/'campus'; campus.mkdir(); store = root/'store'
            release = root/'release'; shutil.copytree(exe.parent, release)
            (campus/'config.json').write_text('{}', encoding='utf-8')
            (campus/'campus_auth.py').write_text('''\nimport os, time\nfrom pathlib import Path\ndef acquire_lock(): return True\ndef release_lock(): pass\ndef load_config(path): return {}\ndef once(cfg):\n    p=Path(os.environ['NETCENTER_TEST_RETRY_FILE'])\n    n=int(p.read_text())+1 if p.exists() else 1\n    p.write_text(str(n))\n    if n == 1 and os.environ.get('NETCENTER_TEST_RELEASE_FILE'):\n        release=Path(os.environ['NETCENTER_TEST_RELEASE_FILE'])\n        deadline=time.monotonic()+20\n        while not release.exists() and time.monotonic()<deadline: time.sleep(0.05)\n    return (1, 'incorrect credentials') if n == 1 else (0, 'authorized')\n''', encoding='utf-8')
            state = {'Link': {'Sig':'test-link', 'EthIp':'', 'WifiIp':'', 'DefaultRoute':'', 'CampusIps':[], 'Summary':'test'}, 'Portal':{'Code':'walled','Text':'test','Ms':0}, 'Hotspot':{}, 'Clash':{}, 'Speed':{}, 'Gpt':{}, 'Egress':{}, 'Session':{}, 'Overall':'warn'}
            payload = json.dumps(state, ensure_ascii=True, separators=(',',':'))
            (release/'backend'/'collector.ps1').write_text("[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)\nwhile ($true) { [Console]::Out.WriteLine('" + payload + "'); [Console]::Out.Flush(); Start-Sleep -Seconds 1 }\n", encoding='utf-8-sig')
            env=dict(os.environ, NETCENTER_TEST_EXE=str(release/'NetCenter.exe'), NETCENTER_DATA_DIR=str(store), NETCENTER_CAMPUS_DIR=str(campus), NETCENTER_INSTANCE_KEY='ui-retry-'+str(os.getpid()), NETCENTER_TEST_RETRY_FILE=str(root/'calls.txt'))
            if delayed: env['NETCENTER_TEST_RELEASE_FILE'] = str(root/'release-first.txt')
            script=Path(__file__).resolve().parent/'verify_accounts_ui.ps1'
            p=subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(script)],capture_output=True,timeout=75,env=env)
            self.assertEqual(p.returncode,0,p.stdout.decode('utf-8',errors='replace')+p.stderr.decode('utf-8',errors='replace'))
