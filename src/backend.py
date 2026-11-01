import contextlib
import errno
import msvcrt
import time
import importlib.util
import io
import json
import os
import sys
from pathlib import Path
from account_store import AccountStore
from auth_bridge import authenticate

DATA = Path(os.environ.get('NETCENTER_DATA_DIR') or (Path(os.environ['USERPROFILE']) / '.netcenter-next'))
CAMPUS = Path(os.environ.get('NETCENTER_CAMPUS_DIR', r'E:\CampusAuth'))

@contextlib.contextmanager
def profile_write_lock(timeout=15):
    """Serialize profile/default read-modify-write operations across processes."""
    DATA.mkdir(parents=True, exist_ok=True)
    with (DATA / '.profiles.lock').open('a+b') as stream:
        if stream.tell() == 0:
            stream.write(b'\0')
            stream.flush()
        deadline = time.monotonic() + timeout
        while True:
            stream.seek(0)
            try:
                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
                break
            except OSError as exc:
                if exc.errno not in (errno.EACCES, errno.EAGAIN, errno.EDEADLK) and getattr(exc, 'winerror', None) not in (33, 36):
                    raise
                if time.monotonic() >= deadline:
                    raise TimeoutError('账号库正被使用，请稍后重试') from exc
                time.sleep(0.05)
        try:
            yield
        finally:
            stream.seek(0)
            msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)


def run(request):
    if request.get('action') in {'save', 'delete', 'select_default', 'clear_default', 'import_existing'}:
        with profile_write_lock():
            return _run(request)
    return _run(request)


def _run(request):
    store = AccountStore(DATA / 'accounts.json')
    action = request['action']
    if action == 'list':
        accounts = store.list()
        default_file = DATA / 'default.json'
        default = json.loads(default_file.read_text(encoding='utf-8')).get('name') if default_file.exists() else None
        if default not in [a['name'] for a in accounts]:
            default = None
        return {'ok': True, 'accounts': accounts, 'default': default}
    if action == 'select_default':
        if request['name'] not in [a['name'] for a in store.list()]:
            raise ValueError('账号不存在')
        store_for_default = AccountStore(DATA / 'default.json')
        store_for_default._write({'name': request['name']})
        return {'ok': True}
    if action == 'clear_default':
        AccountStore(DATA / 'default.json')._write({'name': None})
        return {'ok': True}
    if action == 'import_existing':
        if store.list():
            raise ValueError('已有账号，未覆盖')
        cfg = json.loads((CAMPUS / 'config.json').read_text(encoding='utf-8-sig'))
        user, password = cfg.get('user_id', ''), cfg.get('password', '')
        if not user or not password:
            raise ValueError('原校园网账号或密码为空')
        store.save('原校园网账号', user, password)
        _run({'action': 'select_default', 'name': '原校园网账号'})
        return {'ok': True}
    if action == 'save':
        store.save(request['name'], request['username'], request['password'])
        return {'ok': True}
    if action == 'delete':
        current = _run({'action': 'list'})['default']
        if current == request['name']:
            AccountStore(DATA / 'default.json')._write({'name': None})
        store.delete(request['name'])
        return {'ok': True}
    if action == 'auto':
        default = _run({'action': 'list'})['default']
        if default is None:
            raise ValueError('尚未设置默认账号')
        return _run({'action': 'auth', 'name': default})
    if action == 'auth':
        spec = importlib.util.spec_from_file_location('campus_auth', CAMPUS / 'campus_auth.py')
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        with contextlib.redirect_stdout(io.StringIO()):
            result = authenticate(module, store.get(request['name']), CAMPUS / 'config.json')
        return {'ok': result['code'] == 0, **result}
    raise ValueError('Unknown action')

if __name__ == '__main__':
    try:
        response = run(json.load(sys.stdin))
    except Exception as exc:
        response = {'ok': False, 'message': str(exc)}
    print(json.dumps(response, ensure_ascii=False))
