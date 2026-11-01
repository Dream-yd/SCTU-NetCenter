import subprocess
import tempfile
import unittest
from pathlib import Path
import shutil
import os


ROOT = Path(__file__).resolve().parents[1]


class BuildReleaseTests(unittest.TestCase):
    def test_packaging_uses_allowlist_and_refuses_existing_destination(self):
        with tempfile.TemporaryDirectory(prefix='netcenter-package-') as tmp:
            root = Path(tmp)
            project = root / 'project'
            project.mkdir()
            for directory in ('support', 'assets', 'third-party-licenses', 'tools', 'docs/images'):
                shutil.copytree(ROOT / directory, project / directory, dirs_exist_ok=True)
            (project / 'app').mkdir()
            (project / 'app/NetCenter.csproj').write_text('', encoding='utf-8')
            (project / 'vendor/BluetoothAudioReceiver').mkdir(parents=True)
            shutil.copy2(ROOT / 'vendor/BluetoothAudioReceiver/LICENSE', project / 'vendor/BluetoothAudioReceiver/LICENSE')
            (project / 'vendor/BluetoothAudioReceiver/BluetoothAudioReceiver.csproj').write_text('', encoding='utf-8')
            for name in ('README.md', 'CONTRIBUTING.md', 'SECURITY.md', 'THIRD_PARTY_NOTICES.md', 'requirements.txt'):
                shutil.copy2(ROOT / name, project / name)
            (project / 'support/campus-auth/config.json').write_text('private fixture', encoding='utf-8')
            (project / 'support/hotspot/state.json').write_text('private fixture', encoding='utf-8')
            fakebin = root / 'fakebin'
            fakebin.mkdir()
            (fakebin / 'dotnet.cmd').write_text('@echo off\r\n:loop\r\nif "%~1"=="" exit /b 1\r\nif "%~1"=="-o" (mkdir "%~2" 2>nul & exit /b 0)\r\nshift\r\ngoto loop\r\n', encoding='ascii')
            output = root / 'output'
            env = dict(os.environ, PATH=str(fakebin) + os.pathsep + os.environ['PATH'])
            command = ['powershell.exe', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', str(project / 'tools/Build-Release.ps1'), '-OutputDirectory', str(output)]
            result = subprocess.run(command, capture_output=True, env=env, timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr.decode(errors='replace'))
            self.assertFalse((output / 'support/campus-auth/config.json').exists())
            self.assertFalse((output / 'support/hotspot/state.json').exists())
            self.assertTrue((output / 'support/campus-auth/config.example.json').is_file())
            self.assertTrue((output / 'app/bluetooth/LICENSE-BluetoothAudioReceiver.txt').is_file())
            second = subprocess.run(command, capture_output=True, env=env, timeout=30)
            self.assertNotEqual(second.returncode, 0)


if __name__ == '__main__':
    unittest.main()
