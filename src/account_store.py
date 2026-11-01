"""Local account profiles; passwords are protected by the current Windows user."""
import base64
import json
import os
import tempfile
from pathlib import Path
import win32crypt


class AccountStore:
    def __init__(self, path):
        self.path = Path(path)

    def _read(self):
        if not self.path.exists():
            return {}
        data = json.loads(self.path.read_text(encoding='utf-8'))
        if not isinstance(data, dict):
            raise ValueError('Invalid account store')
        return data

    def _write(self, data):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        fd, temporary = tempfile.mkstemp(dir=self.path.parent, prefix='.accounts-', suffix='.tmp')
        try:
            with os.fdopen(fd, 'w', encoding='utf-8') as stream:
                json.dump(data, stream, ensure_ascii=False, indent=2)
                stream.flush()
                os.fsync(stream.fileno())
            # AppData may be redirected by Windows package virtualization.
            # Resolve both paths before the atomic rename to keep them on one volume.
            destination = os.path.join(os.path.realpath(self.path.parent), self.path.name)
            os.replace(os.path.realpath(temporary), destination)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)

    def save(self, name, username, password):
        name, username = name.strip(), username.strip()
        if not name or not username or not password:
            raise ValueError('Account name, username and password are required')
        data = self._read()
        encrypted = win32crypt.CryptProtectData(password.encode('utf-8'), None, None, None, None, 0)
        data[name] = {'username': username, 'password': base64.b64encode(encrypted).decode('ascii')}
        self._write(data)

    def list(self):
        return [{'name': name, 'username': item['username']} for name, item in self._read().items()]

    def get(self, name):
        item = self._read()[name]
        encrypted = base64.b64decode(item['password'], validate=True)
        password = win32crypt.CryptUnprotectData(encrypted, None, None, None, 0)[1].decode('utf-8')
        return {'username': item['username'], 'password': password}

    def delete(self, name):
        data = self._read()
        del data[name]
        self._write(data)
