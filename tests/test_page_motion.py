import subprocess
import unittest
from pathlib import Path


class PageMotionTests(unittest.TestCase):
    def test_repeated_navigation_keeps_the_requested_page_visible(self):
        project = Path(__file__).resolve().parent / 'PageMotionChecks.csproj'
        process = subprocess.run(['dotnet', 'run', '--project', str(project)], capture_output=True, timeout=60, creationflags=subprocess.CREATE_NO_WINDOW)
        self.assertEqual(process.returncode, 0, process.stdout.decode('utf-8', errors='replace') + process.stderr.decode('utf-8', errors='replace'))
        self.assertIn(b'Page motion checks passed', process.stdout)
