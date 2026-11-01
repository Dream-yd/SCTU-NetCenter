import tempfile
import unittest
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'src'))
from account_store import AccountStore

class AccountStoreTests(unittest.TestCase):
    def test_multiple_accounts_passwords_are_encrypted_and_persist(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'accounts.json'
            store = AccountStore(path)
            store.save('personal', 'student1', 'secret-one')
            store.save('work', 'teacher2', 'secret-two')
            self.assertNotIn('secret-one', path.read_text(encoding='utf-8'))
            self.assertNotIn('secret-two', path.read_text(encoding='utf-8'))
            fresh = AccountStore(path)
            self.assertEqual(fresh.get('personal')['password'], 'secret-one')
            self.assertEqual(fresh.get('work')['password'], 'secret-two')
            self.assertEqual(len(fresh.list()), 2)

    def test_delete_and_reject_empty_fields(self):
        with tempfile.TemporaryDirectory() as directory:
            store = AccountStore(Path(directory) / 'accounts.json')
            with self.assertRaises(ValueError):
                store.save('', 'user', 'pass')
            store.save('personal', 'user', 'pass')
            store.delete('personal')
            self.assertEqual(store.list(), [])

    def test_store_writes_under_local_appdata(self):
        import os
        with tempfile.TemporaryDirectory(dir=os.environ['LOCALAPPDATA'], prefix='netcenter-store-test-') as directory:
            path=Path(directory)/'accounts.json'
            store=AccountStore(path)
            store.save('test', 'u', 'secret')
            self.assertEqual(AccountStore(path).get('test')['password'], 'secret')

if __name__ == '__main__':
    unittest.main()
