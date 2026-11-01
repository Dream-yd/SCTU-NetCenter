import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock
from urllib.parse import parse_qs
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'src'))
from auth_bridge import authenticate
from account_store import AccountStore

AUTH = Path(__file__).resolve().parents[1] / 'support' / 'campus-auth' / 'campus_auth.py'

class CampusProtocolIntegrationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        spec = importlib.util.spec_from_file_location('campus_auth_integration_test', AUTH)
        self.campus = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.campus)
        self.campus.HERE = str(self.root)
        self.campus.LOCK_FILE = str(self.root / 'run.lock')
        self.campus.QS_CACHE = str(self.root / 'query_cache.txt')
        self.campus.STATE_FILE = str(self.root / 'last_state.txt')
        self.campus.DEFAULT_CONFIG = dict(self.campus.DEFAULT_CONFIG, log_file=None)
        self.config = self.root / 'config.json'
        self.config.write_text(json.dumps({'portal':'http://172.16.10.76', 'campus_prefixes':['172.30.'], 'service':['default']}), encoding='utf-8')
        self.addCleanup(self.temp.cleanup)

    def test_off_campus_makes_no_network_requests(self):
        with mock.patch.object(self.campus,'local_ipv4s',return_value={'192.168.1.20'}), mock.patch.object(self.campus,'http') as network:
            result=authenticate(self.campus, {'username':'one','password':'secret'}, self.config)
        self.assertEqual(result['code'],2)
        network.assert_not_called()
        self.assertFalse((self.root / 'run.lock').exists())

    def test_authorized_session_is_not_logged_out_or_reauthenticated(self):
        with mock.patch.object(self.campus,'local_ipv4s',return_value={'172.30.1.2'}), mock.patch.object(self.campus,'portal_reachable',return_value=True), mock.patch.object(self.campus,'http',return_value=(404,{},b'', 'http://223.5.5.5/')) as network:
            result=authenticate(self.campus, {'username':'other','password':'secret'}, self.config)
        self.assertEqual(result['code'],0)
        self.assertEqual(network.call_count,1)
        self.assertNotIn('logout',network.call_args.args[1])
        self.assertIsNone(network.call_args.kwargs.get('data'))

    def test_walled_session_uses_selected_profile_and_verifies_release(self):
        store=AccountStore(self.root/'accounts.json')
        store.save('one','student-one','password-one')
        store.save('two','student-two','password-two')
        location='http://172.16.10.76/eportal/index.jsp?wlanuserip=172.30.1.2&wlanparameter=test'
        calls=[]
        def fake_http(cfg,url,data=None,follow=False,referer=None):
            calls.append((url,data))
            if 'method=login' in url:
                return 200,{},b'{"result":"success"}',url
            if len([c for c in calls if c[1] is None]) == 1:
                return 302,{'Location':location},b'',url
            return 404,{},b'',url
        with mock.patch.object(self.campus,'local_ipv4s',return_value={'172.30.1.2'}), mock.patch.object(self.campus,'portal_reachable',return_value=True), mock.patch.object(self.campus,'http',side_effect=fake_http), mock.patch.object(self.campus.time,'sleep'):
            result=authenticate(self.campus,store.get('two'),self.config)
        self.assertEqual(result['code'],0)
        login=[data for url,data in calls if 'method=login' in url]
        self.assertEqual(len(login),1)
        submitted=parse_qs(login[0])
        self.assertEqual(submitted['userId'],['student-two'])
        self.assertEqual(submitted['password'],['password-two'])
        self.assertIn('wlanuserip=172.30.1.2',submitted['queryString'][0])
        self.assertEqual(len([c for c in calls if c[1] is None]),2)
        self.assertFalse((self.root/'run.lock').exists())

    def test_portal_wait_state_is_treated_as_online_without_retries(self):
        with mock.patch.object(self.campus,'get_online_info',return_value=({'result':'wait','message':'稍后重试'},'')) as online:
            ok,_=self.campus.is_online(self.campus.load_config(str(self.config)))
        self.assertTrue(ok)
        self.assertEqual(online.call_count,1)
