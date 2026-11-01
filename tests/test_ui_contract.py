import unittest
from pathlib import Path
import xml.etree.ElementTree as ET

class UiContractTests(unittest.TestCase):
    def test_dashboard_and_campus_account_controls_exist(self):
        root = ET.parse(Path(__file__).resolve().parents[1] / 'app' / 'MainWindow.xaml').getroot()
        xname = '{http://schemas.microsoft.com/winfx/2006/xaml}Name'
        names = {e.attrib.get(xname) for e in root.iter()}
        handlers = {e.attrib.get('Click') for e in root.iter()}
        self.assertTrue({'HotspotClick', 'BluetoothClick'} <= handlers)
        self.assertTrue({'DashboardPage', 'CampusPage', 'Accounts', 'AccountName', 'UserId', 'Password', 'StatusText', 'DefaultAccount', 'SpeedDown', 'SpeedUp', 'LinkDetail', 'PortalDetail', 'OverallBadge'} <= names)

if __name__ == '__main__': unittest.main()
