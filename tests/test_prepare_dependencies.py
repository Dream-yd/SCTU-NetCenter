import json
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


class PrepareDependenciesTests(unittest.TestCase):
    def test_preparation_is_safe_and_never_overwrites_existing_directories(self):
        with tempfile.TemporaryDirectory(prefix='netcenter-setup-') as tmp:
            root = Path(tmp)
            project = root / 'project'
            drive = root / 'fake-drive'
            drive.mkdir()
            (project / 'tools').mkdir(parents=True)
            shutil.copytree(ROOT / 'support', project / 'support')
            shutil.copytree(ROOT / 'assets', project / 'assets')
            source = (ROOT / 'tools' / 'Prepare-Dependencies.ps1').read_text(encoding='utf-8-sig')
            script = project / 'tools' / 'Prepare-Dependencies.ps1'
            script.write_text(source.replace('E:\\', str(drive) + '\\'), encoding='utf-8-sig')
            command = ['powershell.exe', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', str(script)]
            first = subprocess.run(command, capture_output=True, timeout=30)
            self.assertEqual(first.returncode, 0, first.stderr.decode(errors='replace'))
            config = drive / 'CampusAuth' / 'config.json'
            data = json.loads(config.read_text(encoding='utf-8-sig'))
            self.assertEqual((data['user_id'], data['password']), ('', ''))
            hotspot = json.loads((drive / 'MobileHotspotKeeper' / 'config.json').read_text(encoding='utf-8-sig'))
            self.assertFalse(hotspot['Enabled'])
            config.write_text('do not overwrite', encoding='utf-8')
            second = subprocess.run(command, capture_output=True, timeout=30)
            self.assertNotEqual(second.returncode, 0)
            self.assertEqual(config.read_text(encoding='utf-8'), 'do not overwrite')


if __name__ == '__main__':
    unittest.main()
