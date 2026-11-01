#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
四川旅游学院 校园网自动认证（锐捷 RG-SAM+ ePortal）
=====================================================
门户: http://172.16.10.76/eportal/
接口: InterFace.do?method=getOnlineUserInfo | login | logout

用法:
    python campus_auth.py             # 单次：已在线则跳过，掉线则登录（给计划任务用）
    python campus_auth.py --watch     # 常驻：每 30 秒检查一次
    python campus_auth.py --status    # 只看当前在线状态
    python campus_auth.py --logout    # 注销当前在线会话（换账号前用）
    python campus_auth.py -v          # 打印详细过程

仅用标准库，无第三方依赖。
"""
import argparse
import json
import os
import re
import socket
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime

HERE = os.path.dirname(os.path.abspath(__file__))
QS_CACHE = os.path.join(HERE, "query_cache.txt")
STATE_FILE = os.path.join(HERE, "last_state.txt")
LAST_PROBES = []   # 记录 queryString 探测过程，登录失败时一并写进日志，便于事后诊断
LOCK_FILE = os.path.join(HERE, ".run.lock")   # 单实例锁


def acquire_lock(stale_seconds=90):
    """同一时刻只允许一个实例在跑。

    插网线时 OnNetConnect 和 OnConnectivityChange 两个计划任务会**同时**触发，
    两边各登录一次 → 门户里出现同 IP 的两条会话 → getOnlineUserInfo 一直停在
    result=wait（"用户信息不完整"）。互斥之后就不会再双登录。
    """
    for _ in range(2):
        try:
            fd = os.open(LOCK_FILE, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            os.write(fd, str(os.getpid()).encode("ascii"))
            os.close(fd)
            return True
        except FileExistsError:
            try:
                age = time.time() - os.path.getmtime(LOCK_FILE)
            except OSError:
                age = stale_seconds + 1
            if age > stale_seconds:          # 上次异常退出留下的死锁，踢掉
                try:
                    os.remove(LOCK_FILE)
                except OSError:
                    pass
                continue
            return False
        except OSError:
            return True                      # 锁建不出来也别阻塞主流程
    return False


def release_lock():
    try:
        if os.path.exists(LOCK_FILE):
            os.remove(LOCK_FILE)
    except OSError:
        pass

DEFAULT_CONFIG = {
    "portal": "http://172.16.10.76",
    "user_id": "",
    "password": "",
    "service": ["default", "校园网"],
    "password_mode": "plain",
    "timeout": 6,
    "log_file": os.path.join(HERE, "campus_auth.log"),
    "log_max_kb": 512,
    # 本机出口 IP 必须落在这个前缀里，才认为「身处校园网」。手机热点、酒店、家里
    # 的网段全被这一条挡在门外：连探针都不会发出去，更不会碰门户。
    "campus_prefixes": ["172.30."],
}


def load_config(path):
    cfg = dict(DEFAULT_CONFIG)
    if os.path.exists(path):
        with open(path, "r", encoding="utf-8-sig") as f:
            cfg.update(json.load(f))
    if isinstance(cfg.get("service"), str):
        cfg["service"] = [cfg["service"]]
    return cfg


def log(cfg, msg):
    line = "[%s] %s" % (datetime.now().strftime("%Y-%m-%d %H:%M:%S"), msg)
    print(line)
    try:
        lf = cfg.get("log_file")
        if lf:
            maxb = int(cfg.get("log_max_kb", 512)) * 1024
            if os.path.exists(lf) and os.path.getsize(lf) > maxb:
                os.replace(lf, lf + ".1")
            with open(lf, "a", encoding="utf-8") as f:
                f.write(line + "\n")
    except Exception:
        pass


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


_opener_noredirect = urllib.request.build_opener(NoRedirect)
_opener = urllib.request.build_opener()


def http(cfg, url, data=None, follow=False, referer=None):
    """返回 (status, headers(dict), body(bytes), final_url)"""
    headers = {
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
        "Accept": "*/*",
        "Connection": "close",
    }
    if referer:
        headers["Referer"] = referer
    if data is not None:
        data = data.encode("utf-8") if isinstance(data, str) else data
        headers["Content-Type"] = "application/x-www-form-urlencoded; charset=UTF-8"
    req = urllib.request.Request(url, data=data, headers=headers)
    opener = _opener if follow else _opener_noredirect
    try:
        with opener.open(req, timeout=cfg["timeout"]) as r:
            return r.status, dict(r.headers), r.read(), r.geturl()
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers), (e.read() or b""), url
    except Exception as e:
        return 0, {}, str(e).encode("utf-8"), url


def as_json(raw):
    for enc in ("utf-8", "gbk", "latin-1"):
        try:
            return json.loads(raw.decode(enc))
        except Exception:
            continue
    return None


def primary_ip(dest=("223.5.5.5", 53)):
    """本机此刻通往外部真正会用的源 IP —— 也就是门户眼里的「我是谁」。

    UDP connect 不发包，只让内核按当前路由表选一次路，因此拿到的就是
    当前默认出口网卡的地址。插网线/换 WiFi 后会立刻变，正好用来判断
    「门户记录的那条会话是不是我这块网卡的」。
    """
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(dest)
        return s.getsockname()[0]
    except Exception:
        return ""
    finally:
        s.close()


def is_loopback(ip):
    return (not ip) or ip.startswith("127.")


def qs_ip(qs):
    """从 queryString 里取出它声称的客户端 IP（wlanuserip/userip）"""
    m = re.search(r"(?:wlan)?userip=([0-9]{1,3}(?:\.[0-9]{1,3}){3})", qs or "")
    return m.group(1) if m else ""


def local_ipv4s():
    """本机所有 IPv4 地址。

    特意不看路由表：TUN/代理把默认路由抢走时，物理网卡的地址仍然是本机的，
    这里照样能看见。只用来回答两个问题——「这台机器此刻在不在校园网」和
    「门户记的那条会话是不是我的」。
    """
    ips = set()
    try:
        for ip in socket.gethostbyname_ex(socket.gethostname())[2]:
            if not is_loopback(ip):
                ips.add(ip)
    except Exception:
        pass
    for dest in (("223.5.5.5", 53), ("172.16.10.76", 80), ("114.114.114.114", 53)):
        ip = primary_ip(dest)
        if not is_loopback(ip):
            ips.add(ip)
    return ips


def campus_ips(ips, cfg):
    """过滤出落在校园网段的那几个本机地址（可能同时有 WiFi 和有线两条）"""
    prefixes = cfg.get("campus_prefixes") or []
    if not prefixes:
        return set()
    return set(ip for ip in ips if any(ip.startswith(p) for p in prefixes))


def probe_iface(cfg):
    """判断「当前这块网卡」到底有没有被门户放行，顺便现取一份属于它的参数。

    返回 (state, qs, detail)：
      authorized  已被放行（HTTP 没被拦成 302），什么都不能做
      walled      被 NAS 拦到门户了，qs 是本次现取的、属于当前网卡的真参数
      unreachable 连不出去（链路没起来 / 不在校园网）

    这是 2026-10-27 修掉的核心 bug：以前先用缓存，导致永远拿别的网卡的
    身份去登录；现在一律先探针，只有探针拿不到东西时才回头看缓存。
    """
    probes = [
        "http://223.5.5.5/",                        # 纯 IP，未认证必被 NAS 拦截成 302
        "http://www.msftconnecttest.com/redirect",
        cfg["portal"] + "/",
    ]
    del LAST_PROBES[:]
    for url in probes:
        st, hd, raw, final = http(cfg, url, follow=False)
        loc = hd.get("Location") or hd.get("location") or ""
        LAST_PROBES.append("%s -> HTTP %s Location=%s" % (url, st, loc[:180]))
        if st == 0:
            continue
        if "redirectortosuccess" in loc:
            return "authorized", "", "%s -> 门户判定已放行" % url
        q = _qs_from_location(loc) or _qs_from_location(final)
        if q:
            return "walled", q, "%s -> 被拦截，现取参数" % url
        text = raw.decode("gbk", "replace")
        m = re.search(r"(wlanuserip=[^\"'&<> ]+(?:&[^\"'&<> ]+)*)", text)
        if m:
            return "walled", m.group(1), "%s -> 被拦截，正文取参" % url
        # 有正常响应（404/200/204 等）却没被指向门户 => 已经放行
        return "authorized", "", "%s -> HTTP %s，未被拦截" % (url, st)
    return "unreachable", "", "三个探针都不通"


def cached_query_string():
    try:
        if os.path.exists(QS_CACHE):
            with open(QS_CACHE, "r", encoding="utf-8") as f:
                return f.read().strip()
    except Exception:
        pass
    return ""


def _quarantine_cache(cfg=None):
    """把 query_cache.txt 挪进 _backup/（带时间戳），而不是直接删。

    为什么要挪：里面是上一次登录成功的完整 queryString，出问题时还能翻出来
    看当时的 userlocation / wlanparameter。直接删就查不了了。
    """
    try:
        if not os.path.exists(QS_CACHE):
            return ""
        bdir = os.path.join(HERE, "_backup")
        os.makedirs(bdir, exist_ok=True)
        stamp = time.strftime("%Y%m%d-%H%M%S")
        dest = os.path.join(bdir, "query_cache.txt.stale-%s" % stamp)
        os.replace(QS_CACHE, dest)
        name = os.path.basename(dest)
        if cfg:
            log(cfg, "旧缓存已隔离 → _backup/%s" % name)
        return name
    except Exception:
        try:
            os.remove(QS_CACHE)
        except Exception:
            pass
        return ""


def get_online_info(cfg):
    st, hd, raw, _ = http(cfg, cfg["portal"] + "/eportal/InterFace.do?method=getOnlineUserInfo")
    if st != 200:
        return None, "HTTP %s %s" % (st, raw[:120])
    j = as_json(raw)
    if not j:
        return None, "响应不是 JSON: " + raw[:120].decode("utf-8", "replace")
    return j, ""


def is_online(cfg, verbose=False, attempts=3, delay=0.6):
    """在线检查要连续确认，绝不凭一次读数就判定掉线（实测门户会瞬时抖动）"""
    saw_fail = False
    last_msg = ""
    last_err = ""
    for i in range(attempts):
        j, err = get_online_info(cfg)
        if j is None:
            last_err = err
            if verbose:
                log(cfg, "在线检查第 %d/%d 次异常: %s" % (i + 1, attempts, err))
        elif j.get("result") == "success":
            uip = j.get("userIp") or ""
            # 门户说的「在线」可能是另一块网卡那条会话（实测会出现
            # userIp=WiFi地址 而 userMac=有线MAC 的交叉身份）。
            # 它的 IP 不在本机地址里，就等于当前这块网卡没被授权。
            if uip and uip not in local_ipv4s():
                if verbose:
                    log(cfg, "门户在线会话属于 %s，不在本机地址里 → 本网卡未授权" % uip)
                return False, "在线会话属于其他网卡(%s)，当前网卡未授权" % uip
            if verbose:
                log(cfg, "已在线: %s(%s) ip=%s service=%s" % (
                    j.get("userName"), j.get("userId"), j.get("userIp"), j.get("service")))
            return True, ""
        elif j.get("result") == "wait" or "稍后" in (j.get("message") or ""):
            # 刚登录成功的过渡态（实测门户要十几秒把会话信息补全，返回
            # {"result":"wait","message":"用户信息不完整，请稍后重试"}）
            # 此刻绝不能再去登录，否则每分钟的计划任务会反复冲击门户
            if verbose:
                log(cfg, "门户处于过渡态(%s)，本次不做任何动作" % j.get("message"))
            return True, ""
        else:
            saw_fail = True
            last_msg = j.get("message", "")
            if verbose:
                log(cfg, "在线检查第 %d/%d 次: 未在线 (%s)" % (i + 1, attempts, last_msg))
        if i < attempts - 1:
            time.sleep(delay)
    if saw_fail:
        return False, last_msg
    return None, last_err


def _qs_from_location(loc):
    q = urllib.parse.urlparse(loc).query
    if "wlanuserip" in q or "userip" in q or "nasip" in q:
        return q
    return ""


def extract_query_string(cfg, verbose=False):
    """取出 wlanuserip/wlanacname/nasip/mac/... 查询串（门户靠它识别 NAS 设备）

    顺序（2026-10-27 改）：人工覆盖 → 探针现取 → 校验过的缓存。

    **缓存必须排最后**，这是旧版最大的坑：旧顺序是「缓存优先」，只要
    query_cache.txt 存在就永远返回它。而这里面存着上一次成功登录时的
    wlanuserip + wlanparameter(网卡 MAC) + userlocation(交换机端口)，
    一旦换了网卡/网口/IP，就等于拿有线的身份去认 WiFi 的证 —— 门户照单
    全收，会话被建成「WiFi 的 IP + 有线的 MAC」，而真正插上网线时那个
    新 IP 从来没被授权过，于是永远上不了网。
    """
    if cfg.get("query_string_override"):
        if verbose:
            log(cfg, "使用 config 里的 query_string_override")
        return cfg["query_string_override"]

    st, qs, detail = probe_iface(cfg)
    if verbose:
        log(cfg, "探针: " + detail)
    if st == "authorized":
        return "ONLINE"
    if qs:
        return qs

    # 探针没拿到参数才回头看缓存，而且必须校验它属于本机某块网卡
    cached = cached_query_string()
    if cached:
        cip = qs_ip(cached)
        if cip and cip in local_ipv4s():
            if verbose:
                log(cfg, "探针未取到参数；缓存里的 %s 确属本机，沿用" % cip)
            return cached
        log(cfg, "丢弃过期缓存：里面是 %s，不在本机地址 %s 里" % (
            cip or "?", ",".join(sorted(local_ipv4s())) or "?"))
        # 光「丢弃」不够：文件还在，NetCenter 之类的外部消费者会继续把它当成
        # 当前会话显示。这里直接隔离到 _backup/，下次就干净了。
        _quarantine_cache(cfg)

    if verbose:
        log(cfg, "警告: 未取到 queryString，用空串尝试登录")
    return ""


def cache_query_string(qs):
    """只缓存「确定属于当前网卡」的参数，否则宁可不存。

    2026-10-27 之前的版本会无条件缓存，于是一次有线登录的参数会一直
    被 WiFi 复用，直到有人手删文件。
    """
    try:
        if not qs:
            return
        cip = qs_ip(qs)
        if cip and cip not in local_ipv4s():
            return          # 参数不属于本机任何一块网卡，存下来只会害下一次
        with open(QS_CACHE, "w", encoding="utf-8") as f:
            f.write(qs)
    except Exception:
        pass



def do_login(cfg, query_string, verbose=False):
    if cfg.get("password_mode") == "base64":
        import base64
        pwd = base64.b64encode(cfg["password"].encode("utf-8")).decode()
    else:
        pwd = cfg["password"]

    last_msg = ""
    for svc in cfg["service"]:
        body = urllib.parse.urlencode({
            "userId": cfg["user_id"],
            "password": pwd,
            "service": svc,
            "queryString": query_string,
            "operatorPwd": "",
            "operatorUserId": "",
            "validcode": "",
            "passwordEncrypt": "false",
        })
        url = cfg["portal"] + "/eportal/InterFace.do?method=login"
        st, hd, raw, _ = http(cfg, url, data=body, referer=cfg["portal"] + "/eportal/")
        j = as_json(raw)
        if verbose:
            log(cfg, "login(service=%s) -> HTTP %s %s" % (svc, st, (raw[:300].decode("utf-8", "replace"))))
        if j is None:
            last_msg = "HTTP %s / 非 JSON: %s" % (st, raw[:120].decode("utf-8", "replace"))
            continue
        if j.get("result") == "success" or "已在线" in (j.get("message") or ""):
            cache_query_string(query_string)
            return True, "service=%s -> %s" % (svc, j.get("message") or "登录成功")
        last_msg = "service=%s -> %s" % (svc, j.get("message", raw[:120].decode("utf-8", "replace")))
    return False, last_msg


def do_logout(cfg, verbose=False):
    j, err = get_online_info(cfg)
    if not j or j.get("result") != "success":
        return False, "当前不在线"
    ui = j.get("userIndex", "")
    body = urllib.parse.urlencode({"userIndex": ui})
    url = cfg["portal"] + "/eportal/InterFace.do?method=logout"
    st, hd, raw, _ = http(cfg, url, data=body, referer=cfg["portal"] + "/eportal/")
    if verbose:
        log(cfg, "logout -> HTTP %s %s" % (st, raw[:200].decode("utf-8", "replace")))
    jj = as_json(raw)
    if jj and jj.get("result") == "success":
        return True, "已注销 " + j.get("userName", "")
    return False, "注销失败: " + raw[:150].decode("utf-8", "replace")


def portal_reachable(cfg, timeout=1.5):
    """快速判断是否身处校园网。

    回家/换到别的网络时，门户 172.16.10.76 只是个不可达的私网地址，
    这里 1.5 秒内就能判定并让脚本静默退出，绝不产生任何动作或刷日志。
    """
    u = urllib.parse.urlparse(cfg["portal"])
    host = u.hostname
    port = u.port or (443 if u.scheme == "https" else 80)
    if not host:
        return True
    try:
        s = socket.create_connection((host, port), timeout=timeout)
        s.close()
        return True
    except Exception:
        return False


def read_state():
    try:
        with open(STATE_FILE, "r", encoding="utf-8") as f:
            return f.read().strip()
    except Exception:
        return ""


def write_state(state):
    try:
        with open(STATE_FILE, "w", encoding="utf-8") as f:
            f.write(state)
    except Exception:
        pass


def classify(code, msg):
    """把一次运行归成状态，用于『只在状态变化时写日志』"""
    if "失败" in msg or "错误" in msg or "未授权" in msg or "丢弃过期" in msg:
        return "action"          # 真正做了事 / 出了错 → 必须记
    if "登录成功" in msg:
        return "action"
    if "已在线" in msg or "已放行" in msg:
        return "online"
    if code == 2:
        return "offcampus"       # 不在校园网内 → 只在进出校园网时记一条
    return "action"


def once(cfg, verbose=False):
    """跑一次：只处理「当前这块网卡没被授权」这一种情况，其余一律不动。"""
    allips = local_ipv4s()
    cips = campus_ips(allips, cfg)

    # 第一道闸：本机没有任何校园网段地址（手机热点/酒店/家里）→ 连探针都不发
    if not cips:
        return 2, "本机地址 %s 都不在校园网段 %s，本次不执行任何操作" % (
            ",".join(sorted(allips)) or "未知",
            ",".join(cfg.get("campus_prefixes") or []) or "?",
        )
    # 注意：不要用 primary_ip() —— TUN 开着时它会返回 198.18.0.1（隧道地址），
    # 日志里就会写成"出口 198.18.0.1 已授权"，看着像认证绑到了假 IP 上。
    lip = ",".join(sorted(cips)) if cips else (primary_ip() or "未知")

    # 第二道闸：门户 IP 都不可达，说明链路没就绪，静默退出
    if not portal_reachable(cfg):
        return 2, "门户 %s 不可达（链路未就绪？），本次不执行任何操作" % cfg["portal"]

    # 第三道闸：用「有没有被拦到门户」判断当前网卡的真实授权状态
    state, qs, detail = probe_iface(cfg)
    if verbose:
        log(cfg, "本机: 校园地址=%s 出口=%s | %s" % (
            ",".join(sorted(cips)), lip, detail))

    if state == "authorized":
        return 0, "已放行（%s 已被授权），跳过" % lip
    if state == "unreachable":
        return 2, "探针不通（链路未就绪或不在校园网）: " + detail

    # 到这里才是真的「当前这块网卡的 IP 没被授权」—— 插网线后就是这一刻
    if not cfg.get("user_id") or not cfg.get("password"):
        return 3, "未配置账号密码（config.json 里补 user_id / password）"

    if verbose:
        log(cfg, "本机 %s 未授权，用现取参数登录（参数里的 IP=%s）" % (lip, qs_ip(qs) or "?"))
    ok, d = do_login(cfg, qs, verbose)

    if not ok:
        # 失败再试一次：清缓存、重新探针（换网口时 userlocation 会变）
        log(cfg, "登录失败，清缓存后重探再试一次")
        try:
            if os.path.exists(QS_CACHE):
                os.remove(QS_CACHE)
        except Exception:
            pass
        st2, qs2, d2 = probe_iface(cfg)
        if st2 == "walled" and qs2 and qs2 != qs:
            ok, d = do_login(cfg, qs2, verbose)
            qs = qs2

    if not ok:
        log(cfg, "诊断信息 | 本机 IP=%s | queryString=%r | 探针=%s" % (
            lip, qs[:150], " ; ".join(LAST_PROBES)))
        return 1, d

    # 登录返回 success 不等于真的放行 —— 复验一次，别骗自己
    time.sleep(1.0)
    st3, _, d3 = probe_iface(cfg)
    if st3 == "authorized":
        return 0, "%s（已复验放行，%s）" % (d, lip)
    return 1, "%s，但复验仍未放行: %s" % (d, d3)


def main():
    ap = argparse.ArgumentParser(description="校园网 RG-SAM+ ePortal 自动认证")
    ap.add_argument("--config", default=os.path.join(HERE, "config.json"))
    ap.add_argument("--watch", action="store_true", help="常驻循环")
    ap.add_argument("--interval", type=int, default=30, help="--watch 的检查间隔秒数")
    ap.add_argument("--status", action="store_true", help="只查状态")
    ap.add_argument("--tail-log", type=int, nargs="?", const=10, default=None,
                    help="打印日志尾部 N 行（默认 10）")
    ap.add_argument("--logout", action="store_true", help="注销当前会话")
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args()

    cfg = load_config(args.config)

    if args.tail_log is not None:
        lf = cfg.get("log_file")
        print("============== 最近 %d 条日志 ==============" % args.tail_log)
        if lf and os.path.exists(lf):
            with open(lf, "r", encoding="utf-8", errors="replace") as f:
                lines = f.read().splitlines()
            for l in lines[-args.tail_log:]:
                print(l)
        else:
            print("  （还没有日志：说明从未掉线，或脚本从未运行过）")
        return 0

    if args.status:
        j, err = get_online_info(cfg)
        if j is None:
            print("查询失败: " + err)
            return 2
        print(json.dumps({k: j.get(k) for k in
                          ("result", "message", "userName", "userId", "userIp",
                           "userMac", "service", "userGroup", "hasMabInfo", "isAlowMab")},
                         ensure_ascii=False, indent=2))
        if j.get("result") == "wait":
            print("\n注：这是登录后的过渡态（门户十几秒内会自动补全），"
                  "脚本视其为已在线、不会重复登录；也可稍后再查一次。")
        return 0

    if args.logout:
        ok, msg = do_logout(cfg, args.verbose)
        log(cfg, ("注销: " if ok else "注销失败: ") + msg)
        return 0 if ok else 1

    if args.watch:
        log(cfg, "常驻模式启动，间隔 %s 秒" % args.interval)
        last = None
        while True:
            try:
                code, msg = once(cfg, args.verbose)
                if msg != last:
                    log(cfg, msg)
                    last = msg
            except KeyboardInterrupt:
                return 0
            except Exception as e:
                log(cfg, "异常: %r" % (e,))
            time.sleep(args.interval)

    code, msg = once(cfg, args.verbose)
    # 计划任务会周期性跑：只记「状态变化」和「真正做了事」，避免日志被无意义的心跳刷爆
    state = classify(code, msg)
    prev = read_state()
    if args.verbose or state == "action" or state != prev:
        log(cfg, msg)
    write_state(state)
    return code


if __name__ == "__main__":
    if not acquire_lock():
        sys.exit(0)          # 已有实例在跑，直接退出（不写日志，免得刷屏）
    try:
        sys.exit(main())
    finally:
        release_lock()
