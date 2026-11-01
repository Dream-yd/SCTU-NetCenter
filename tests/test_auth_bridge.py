import sys
import unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'src'))
from auth_bridge import authenticate

class FakeCampus:
    def __init__(self, locked=False):
        self.locked = locked
        self.released = False
        self.received = None
    def acquire_lock(self): return not self.locked
    def release_lock(self): self.released = True
    def load_config(self, path): return {'service': ['default']}
    def once(self, cfg):
        self.received = cfg
        return 0, '已放行'

class AuthTests(unittest.TestCase):
    def test_selected_credentials_go_to_existing_authenticator_and_lock_released(self):
        campus = FakeCampus()
        result = authenticate(campus, {'username': 'a', 'password': 'secret'}, 'config.json')
        self.assertEqual(result['code'], 0)
        self.assertEqual(campus.received['user_id'], 'a')
        self.assertEqual(campus.received['password'], 'secret')
        self.assertTrue(campus.released)
    def test_busy_does_not_attempt_login(self):
        campus = FakeCampus(True)
        result = authenticate(campus, {'username': 'a', 'password': 'secret'}, 'config.json')
        self.assertEqual(result['code'], 4)
        self.assertIsNone(campus.received)
        self.assertFalse(campus.released)
    def test_failed_authentication_releases_lock(self):
        campus = FakeCampus()
        def broken(cfg): raise RuntimeError('connection failed')
        campus.once = broken
        with self.assertRaises(RuntimeError):
            authenticate(campus, {'username':'u','password':'p'}, 'config.json')
        self.assertTrue(campus.released)
