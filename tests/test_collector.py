import json
import subprocess
import unittest
from pathlib import Path

class CollectorTests(unittest.TestCase):
    def test_one_shot_snapshot_has_all_sections(self):
        script = Path(__file__).resolve().parents[1] / 'src' / 'collector.ps1'
        process = subprocess.run(['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(script),'-Once'], capture_output=True, timeout=20)
        self.assertEqual(process.returncode, 0, process.stderr.decode('utf-8', errors='replace'))
        state = json.loads(process.stdout.decode('utf-8-sig'))
        self.assertTrue({'Link','Portal','Hotspot','Clash','Speed','Gpt','Egress','Overall'} <= set(state))

    def test_stream_yields_successive_snapshots(self):
        import time
        script = Path(__file__).resolve().parents[1] / 'src' / 'collector.ps1'
        proc = subprocess.Popen(['powershell.exe','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',str(script)], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8')
        try:
            first = json.loads(proc.stdout.readline())
            second = json.loads(proc.stdout.readline())
            self.assertIn('Speed', first)
            self.assertIn('Speed', second)
            self.assertNotEqual(first['When'], second['When'])
        finally:
            proc.terminate()
            proc.wait(timeout=5)
            proc.stdout.close()
            proc.stderr.close()

if __name__ == '__main__': unittest.main()
