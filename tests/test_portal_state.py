import json
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


class PortalStateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        # Load only the production classifier, without starting the collector loop
        # or contacting a real portal. Simulate TUN accepting a private-IP TCP dial.
        command = r"""
param([string]$Source)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$tokens = $null; $errors = $null
$tree = [System.Management.Automation.Language.Parser]::ParseFile($Source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
$node = $tree.Find({param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq 'Get-PortalState'}, $true)
Invoke-Expression $node.Extent.Text
$Cfg = @{ portal = '172.16.10.76'; probeUrl = 'http://223.5.5.5/' }
function Test-TcpTimed { $script:TcpCalls++; [pscustomobject]@{Ok=$script:TcpOk; Ms=7} }
function Get-Probe { $script:ProbeCalls++; $script:Probe }
$cases = @(
    @{Name='home_tun'; Ips=@(); Tcp=$true; Reachable=$true; Code=404; Location=''},
    @{Name='home_redirect'; Ips=@(); Tcp=$true; Reachable=$true; Code=302; Location='http://172.16.10.76/eportal/'},
    @{Name='offline'; Ips=@(); Tcp=$false; Reachable=$false; Code=0; Location=''},
    @{Name='campus_authorized'; Ips=@('172.30.1.2'); Tcp=$true; Reachable=$true; Code=404; Location=''},
    @{Name='campus_walled'; Ips=@('172.30.1.2'); Tcp=$true; Reachable=$true; Code=302; Location='http://172.16.10.76/eportal/'},
    @{Name='campus_blocked'; Ips=@('172.30.1.2'); Tcp=$true; Reachable=$false; Code=0; Location=''},
    @{Name='campus_unreachable'; Ips=@('172.30.1.2'); Tcp=$false; Reachable=$false; Code=0; Location=''}
)
$results = foreach ($case in $cases) {
    $script:TcpCalls = 0; $script:ProbeCalls = 0
    $script:TcpOk = $case.Tcp
    $script:Probe = [pscustomobject]@{Reachable=$case.Reachable; Code=$case.Code; Location=$case.Location}
    $state = Get-PortalState -Link ([pscustomobject]@{CampusIps=$case.Ips})
    [pscustomobject]@{Name=$case.Name; State=$state; TcpCalls=$script:TcpCalls; ProbeCalls=$script:ProbeCalls}
}
ConvertTo-Json -InputObject @($results) -Depth 5 -Compress
"""
        # -Command does not bind a trailing argument to param(), so provide the
        # local source path as a PowerShell single-quoted literal.
        source = str(ROOT / 'src' / 'collector.ps1').replace("'", "''")
        command = command.replace('param([string]$Source)', "$Source = '" + source + "'")
        result = subprocess.run(
            ['powershell.exe', '-NoProfile', '-NonInteractive', '-Command', command],
            capture_output=True, timeout=15,
        )
        if result.returncode:
            raise RuntimeError(result.stderr.decode('utf-8', errors='replace'))
        cls.results = {item['Name']: item for item in json.loads(result.stdout.decode('utf-8-sig'))}

    def test_non_campus_links_never_claim_portal_authorization(self):
        for name in ('home_tun', 'home_redirect', 'offline'):
            with self.subTest(name=name):
                result = self.results[name]
                self.assertEqual(result['State']['Code'], 'offcampus')
                self.assertEqual(result['State']['Text'], '非校园网')
                self.assertEqual(result['State']['Severity'], 'idle')
                self.assertEqual(result['State']['Ms'], -1)
                self.assertEqual(result['TcpCalls'], 0)
                self.assertEqual(result['ProbeCalls'], 0)

    def test_campus_portal_states_remain_distinct(self):
        for name, code, severity in (
            ('campus_authorized', 'authorized', 'ok'),
            ('campus_walled', 'walled', 'warn'),
            ('campus_blocked', 'blocked', 'err'),
            ('campus_unreachable', 'offcampus', 'warn'),
        ):
            with self.subTest(name=name):
                state = self.results[name]['State']
                self.assertEqual(state['Code'], code)
                self.assertEqual(state['Severity'], severity)


if __name__ == '__main__':
    unittest.main()
