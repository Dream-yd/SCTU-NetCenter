"""Use the existing campus authenticator without persisting decrypted credentials."""
def authenticate(campus, account, config_path):
    if not campus.acquire_lock():
        return {'code': 4, 'message': '认证正在进行，请稍后重试'}
    try:
        cfg = campus.load_config(str(config_path))
        cfg['user_id'] = account['username']
        cfg['password'] = account['password']
        code, message = campus.once(cfg)
        return {'code': code, 'message': message}
    finally:
        campus.release_lock()
