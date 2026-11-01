"""Headless event-triggered authentication using the selected local profile."""
import json
import sys
from datetime import datetime, timezone
from backend import DATA, run


def main():
    try:
        result = run({'action': 'auto'})
    except Exception as exc:
        result = {'ok': False, 'message': str(exc)}
    DATA.mkdir(parents=True, exist_ok=True)
    # Never log a profile, configuration or password.
    record = {'time': datetime.now(timezone.utc).isoformat(), 'ok': result.get('ok', False), 'code': result.get('code')}
    with (DATA / 'automatic.log').open('a', encoding='utf-8') as stream:
        stream.write(json.dumps(record, ensure_ascii=False) + '\n')
    return 0 if result.get('ok') else 1


if __name__ == '__main__':
    sys.exit(main())
