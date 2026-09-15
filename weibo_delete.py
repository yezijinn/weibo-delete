#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
批量删微博。用 Playwright 开个真浏览器调微博自己的接口，登录一次就行。

    python weibo_delete.py --dry-run
    python weibo_delete.py --start 2020-01-01 --end 2020-12-31
    python weibo_delete.py --max 100

关于翻页：mymblog 接口是靠 since_id 游标翻的，光改 page 参数没用，会拉回同一批。
不加日期筛选时，删掉一批就 reset 游标回第一页重拉（列表变了，第一页自然是新的）。
加了日期筛选就不能这么干——那样每删一条都要从最新扫到目标日期，几万条得跑好几天，
所以日期模式改成沿游标一路往老扫，扫到比 --start 更老就收工。
"""

import argparse
import json
import random
import re
import shutil
import sys
import time
from datetime import datetime, timedelta
from pathlib import Path

try:
    from playwright.sync_api import sync_playwright
except ImportError:
    sys.exit("缺 playwright，先执行 pip install -r requirements.txt")


HOME_URL = "https://weibo.com"
LIST_API = "https://weibo.com/ajax/statuses/mymblog?uid={uid}&page=1&feature=0"
DELETE_API = "https://weibo.com/ajax/statuses/destroy"
POST_URL = "https://weibo.com/{uid}/{mblogid}"

# 在页面里发请求，cookie 由浏览器自己带。
# XSRF-TOKEN 每次现从 document.cookie 读——从 Python 侧读 context.cookies()
# 拿到的是快照，页面里 fetch 刷新过就不同步了。
JS_CALL = r"""
async ({url, method, body, form, headers}) => {
  const opt = {
    method: method || 'GET',
    credentials: 'include',
    headers: Object.assign({'Accept': 'application/json, text/plain, */*'}, headers || {})
  };
  if (form !== null && form !== undefined) {
    opt.headers['Content-Type'] = 'application/x-www-form-urlencoded';
    opt.body = new URLSearchParams(form).toString();
  } else if (body !== null && body !== undefined) {
    opt.headers['Content-Type'] = 'application/json;charset=UTF-8';
    opt.body = JSON.stringify(body);
  }
  const m = document.cookie.match(/(?:^|;\s*)XSRF-TOKEN=([^;]+)/);
  if (m) {
    try { opt.headers['x-xsrf-token'] = decodeURIComponent(m[1]); }
    catch (e) { opt.headers['x-xsrf-token'] = m[1]; }
  }
  let r;
  try {
    r = await fetch(url, opt);
  } catch (e) {
    return {status: 0, json: null, raw: 'fetch error: ' + e};
  }
  const text = await r.text();
  let json = null;
  try { json = JSON.parse(text); } catch (e) {}
  return {status: r.status, json: json, raw: json ? '' : text.slice(0, 300)};
}
"""

# --mode ui 用的选择器。微博 PC 端早就没删除入口了，大概率命中不了，
# 保留只是万一哪天又放出来
UI_MORE = ['button[title="更多"]', 'i.woo-font--angleDown', 'button:has-text("更多")']
UI_DELETE = [
    'div[role="menuitem"]:has-text("删除")',
    'li:has-text("删除")',
    'a:has-text("删除")',
    'span:has-text("删除")',
]
UI_CONFIRM = [
    'div[role="dialog"] button:has-text("确定")',
    'button:has-text("确定")',
    'button:has-text("确认")',
]


class AuthError(RuntimeError):
    """接口回了未登录。"""


class Logger:
    """同时往控制台和 data/run.log 打一份。"""

    def __init__(self, path):
        self.fh = path.open("a", encoding="utf-8")

    def _line(self, level, msg):
        line = f"[{datetime.now():%Y-%m-%d %H:%M:%S}] [{level}] {msg}"
        print(line, flush=True)
        print(line, file=self.fh, flush=True)

    def info(self, msg):
        self._line("INFO", msg)

    def warn(self, msg):
        self._line("WARN", msg)

    def error(self, msg):
        self._line("ERROR", msg)

    def close(self):
        try:
            self.fh.close()
        except Exception:
            pass


class IdStore:
    """一堆 id 存 jsonl 里，用来断点续删。"""

    def __init__(self, path):
        self.path = path
        self.ids = set()
        if path.exists():
            for line in path.read_text(encoding="utf-8").splitlines():
                line = line.strip()
                if not line:
                    continue
                try:
                    self.ids.add(str(json.loads(line)["id"]))
                except Exception:
                    pass

    def __contains__(self, mid):
        return str(mid) in self.ids

    def __len__(self):
        return len(self.ids)

    def add(self, mid, **extra):
        mid = str(mid)
        if mid in self.ids:
            return
        self.ids.add(mid)
        rec = {"id": mid, "ts": datetime.now().isoformat(timespec="seconds")}
        rec.update(extra)
        with self.path.open("a", encoding="utf-8") as f:
            print(json.dumps(rec, ensure_ascii=False), file=f)


class Api:
    """页面上下文里发请求的薄封装。"""

    def __init__(self, page):
        self.page = page

    def call(self, url, method="GET", body=None, form=None, headers=None):
        return self.page.evaluate(JS_CALL, {
            "url": url,
            "method": method,
            "body": body,
            "form": form,
            "headers": headers or {},
        })


def get_uid(page):
    try:
        return str(page.evaluate("() => (window.$CONFIG && window.$CONFIG.uid) || ''") or "")
    except Exception:
        return ""


def ensure_login(page, log, timeout_sec):
    page.goto(HOME_URL, wait_until="domcontentloaded")
    page.wait_for_timeout(2000)
    uid = get_uid(page)
    if uid:
        log.info(f"已登录，uid = {uid}")
        return uid

    log.warn("未登录，请在弹出的窗口里扫码。")
    log.warn(f"等 {timeout_sec} 秒，登录成功会自动继续。")

    deadline = time.time() + timeout_sec
    while time.time() < deadline:
        page.wait_for_timeout(2000)
        uid = get_uid(page)
        if uid:
            log.info(f"登录成功，uid = {uid}")
            page.wait_for_timeout(1500)
            return uid
    raise RuntimeError("等登录超时了。重跑一次，扫码后马上就会继续。")


def classify(msg):
    """把接口的报错粗分成几类，好决定是重试、退出还是跳过。"""
    if not msg:
        return "other"
    low = msg.lower()
    for kw in ("登录", "未登录", "先登录", "login", "logout", "401"):
        if kw in low:
            return "auth"
    for kw in ("频繁", "太快", "稍后", "限制", "过于", "休息", "403", "429"):
        if kw in msg:
            return "ratelimit"
    return "other"


def parse_date_arg(s):
    try:
        return datetime.strptime(s.strip(), "%Y-%m-%d")
    except ValueError:
        raise argparse.ArgumentTypeError(f"日期得是 YYYY-MM-DD，你给的是：{s}")


def parse_created_at(s):
    """把微博返回的 created_at 转成 naive datetime，认不出来就 None。"""
    if not s:
        return None
    s = s.strip()
    now = datetime.now()

    if s == "刚刚":
        return now
    m = re.match(r"^(\d+)秒前$", s)
    if m:
        return now - timedelta(seconds=int(m.group(1)))
    m = re.match(r"^(\d+)分钟前$", s)
    if m:
        return now - timedelta(minutes=int(m.group(1)))
    m = re.match(r"^今天\s*(\d{1,2}):(\d{2})$", s)
    if m:
        return now.replace(hour=int(m.group(1)), minute=int(m.group(2)),
                           second=0, microsecond=0)
    m = re.match(r"^昨天\s*(\d{1,2}):(\d{2})$", s)
    if m:
        y = now - timedelta(days=1)
        return y.replace(hour=int(m.group(1)), minute=int(m.group(2)),
                         second=0, microsecond=0)
    m = re.match(r"^(\d{1,2})-(\d{1,2})$", s)
    if m:
        try:
            return datetime(now.year, int(m.group(1)), int(m.group(2)))
        except ValueError:
            return None
    # Mon Apr 08 12:00:00 +0800 2024
    for fmt in ("%a %b %d %H:%M:%S %z %Y", "%a %b %d %H:%M:%S %Y"):
        try:
            dt = datetime.strptime(s, fmt)
            return dt.replace(tzinfo=None) if dt.tzinfo else dt
        except ValueError:
            continue
    for fmt in ("%Y-%m-%d %H:%M:%S", "%Y-%m-%d %H:%M", "%Y-%m-%d"):
        try:
            return datetime.strptime(s, fmt)
        except ValueError:
            continue
    return None


def item_datetime(it):
    ts = it.get("created_at_ts")
    if ts:
        try:
            return datetime.fromtimestamp(int(ts))
        except (TypeError, ValueError, OSError):
            pass
    return parse_created_at(str(it.get("created_at") or ""))


def in_date_range(it, start_dt, end_dt):
    """没筛选就恒 True；有筛选但时间解析不出来就 False（宁可不删）。"""
    if start_dt is None and end_dt is None:
        return True
    dt = item_datetime(it)
    if dt is None:
        return False
    if start_dt is not None and dt < start_dt:
        return False
    if end_dt is not None and dt > end_dt:
        return False
    return True


def item_id(it):
    """删除接口要的是 idstr，不是数字 id，也不是 mblogid（短链那个接口不认）。"""
    idstr = it.get("idstr")
    if idstr:
        return str(idstr)
    raw = it.get("id")
    if raw is not None and str(raw) != "":
        return str(raw)
    return ""


def fetch_list(api, uid, since_id=""):
    """拉一页，返回 (items, next_since_id, total)。next_since_id 为空表示到底了。"""
    url = LIST_API.format(uid=uid)
    if since_id:
        url += "&since_id=" + str(since_id)

    res = api.call(url)
    if res.get("status") != 200:
        raise RuntimeError(f"拉列表失败：HTTP {res.get('status')} {res.get('raw')}")

    j = res.get("json") or {}
    if str(j.get("ok")) != "1":
        raise AuthError(str(j.get("msg") or res.get("raw") or "未知错误"))

    data = j.get("data") or {}
    items = data.get("list") or []

    raw = data.get("since_id")
    nxt = "" if raw in (None, "", 0, "0") else str(raw)

    total = data.get("total")
    try:
        total = int(total) if total is not None else None
    except (TypeError, ValueError):
        total = None

    return items, nxt, total


# 快转（快转微博）不能走普通删除，得调取消快转
QUICK_FORWARD_APIS = [
    "https://weibo.com/ajax/statuses/cancelQuickForward",
    "https://weibo.com/ajax/statuses/destroyQuickForward",
]


def _menus_of(item):
    menus = item.get("mblog_menus_new")
    if not isinstance(menus, list):
        return []
    return [m for m in menus if isinstance(m, dict)]


def _menu_blob(m):
    return " ".join(str(m.get(k, "")) for k in ("type", "name", "action"))


def is_quick_forward(item):
    """菜单里有取消快转，就是快转来的微博。"""
    for m in _menus_of(item):
        blob = _menu_blob(m)
        if "quick_forward" in blob or "快转" in blob:
            return True
    return False


def _menu_url(item):
    """菜单里直接带了请求地址的话，优先用它。"""
    for m in _menus_of(item):
        blob = _menu_blob(m)
        if "quick_forward" not in blob and "快转" not in blob:
            continue
        for k in ("url", "api", "request_url", "action"):
            v = m.get(k)
            if isinstance(v, str) and v.startswith("http"):
                return v
    return None


def _post(api, url, mid, as_form):
    if as_form:
        res = api.call(url, method="POST", form={"id": mid})
    else:
        res = api.call(url, method="POST", body={"id": mid})
    j = res.get("json") or {}
    ok = str(j.get("ok")) == "1"
    msg = str(j.get("msg") or j.get("error") or res.get("raw") or "").strip()
    return ok, res.get("status"), msg


def destroy_by_api(api, mid, item=None):
    """删一条。快转的调取消快转，普通微博调 destroy。返回 (ok, status, msg)。"""
    if item is not None and is_quick_forward(item):
        urls = []
        u = _menu_url(item)
        if u:
            urls.append(u)
        urls.extend(QUICK_FORWARD_APIS)
        tried = []
        for as_form in (True, False):
            for url in urls:
                ok, status, msg = _post(api, url, mid, as_form)
                if ok:
                    return True, status, msg
                tag = url.rsplit("/", 1)[-1] + ("(f)" if as_form else "(j)")
                tried.append("%s=%s" % (tag, status))
        return False, 0, "取消快转没成功：" + " ".join(tried)

    return _post(api, DELETE_API, mid, True)


def destroy_by_ui(page, uid, item, timeout=15000):
    """走网页点击。大概率没用，见文件顶部 UI_MORE 的注释。"""
    mblogid = item.get("mblogid") or ""
    if not mblogid:
        return False
    page.goto(POST_URL.format(uid=uid, mblogid=mblogid), wait_until="domcontentloaded")
    page.wait_for_timeout(1500)

    def click_first(selectors):
        for sel in selectors:
            loc = page.locator(sel)
            try:
                if loc.count() > 0:
                    loc.first.click(timeout=timeout)
                    return True
            except Exception:
                continue
        return False

    if not click_first(UI_MORE):
        return False
    page.wait_for_timeout(700)
    if not click_first(UI_DELETE):
        return False
    page.wait_for_timeout(700)
    if not click_first(UI_CONFIRM):
        return False
    page.wait_for_timeout(1500)
    return True


DEFAULT_SETTINGS = {"delay": 2.0, "jitter": 1.5}


def settings_path(data_dir="data"):
    return Path(data_dir) / "settings.json"


def load_settings(data_dir="data"):
    cfg = dict(DEFAULT_SETTINGS)
    p = settings_path(data_dir)
    if p.exists():
        try:
            raw = json.loads(p.read_text(encoding="utf-8"))
            for k in DEFAULT_SETTINGS:
                if k in raw:
                    cfg[k] = float(raw[k])
        except Exception:
            pass
    return cfg


def save_settings(cfg, data_dir="data"):
    d = Path(data_dir)
    d.mkdir(parents=True, exist_ok=True)
    p = settings_path(data_dir)
    p.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")


def apply_settings(args):
    """命令行没写 --delay / --jitter 的，用设置文件里的值补上。"""
    cfg = load_settings(args.data_dir)
    if args.delay is None:
        args.delay = cfg["delay"]
    if args.jitter is None:
        args.jitter = cfg["jitter"]
    return args


def parse_args(argv=None):
    p = argparse.ArgumentParser(description="批量删微博")
    p.add_argument("--dry-run", action="store_true", help="只统计，不删")
    p.add_argument("--max", type=int, default=0, help="这次最多删多少条，0 表示不限")
    p.add_argument("--max-pages", type=int, default=0, help="dry-run 最多扫多少页，0 不限")
    p.add_argument("--mode", choices=["api", "ui"], default="api", help="删除方式")
    p.add_argument("--delay", type=float, default=None, help="每条之间等多少秒，不填就用设置里的")
    p.add_argument("--jitter", type=float, default=None, help="在 delay 之上再加 0~jitter 秒随机，不填就用设置里的")
    p.add_argument("--batch-size", type=int, default=50, help="删多少条长休一次")
    p.add_argument("--batch-pause", type=float, default=45.0, help="长休多少秒")
    p.add_argument("--scan-limit", type=int, default=20, help="连续多少页没得删就认为删完了")
    p.add_argument("--data-dir", default="data", help="进度和日志放哪")
    p.add_argument("--profile-dir", default=".browser-profile", help="浏览器用户目录")
    p.add_argument("--login-timeout", type=int, default=300, help="等登录多少秒")
    p.add_argument("--channel", default="", help="用系统浏览器：chrome / msedge，留空就用自带的")
    p.add_argument("--reset-skip", action="store_true", help="清掉 skip 记录重新试")
    p.add_argument("--start", type=parse_date_arg, default=None,
                   help="起始日期（含），YYYY-MM-DD。加了就走日期模式，翻页改成单向")
    p.add_argument("--end", type=parse_date_arg, default=None,
                   help="结束日期（含），YYYY-MM-DD")
    p.add_argument("--menu", action="store_true", help=argparse.SUPPRESS)
    return p.parse_args(argv)


def dry_run_scan(api, uid, args, log):
    since_id = ""
    seen = set()
    matched = 0
    unparsable = 0
    pages = 0
    total = None
    start_dt = args.start
    end_dt = args.end.replace(hour=23, minute=59, second=59) if args.end else None
    date_filter = start_dt is not None or end_dt is not None

    if date_filter:
        s = start_dt.strftime("%Y-%m-%d") if start_dt else "-"
        e = end_dt.strftime("%Y-%m-%d") if end_dt else "-"
        log.info(f"[dry-run] 日期筛选：{s} ~ {e}")

    while True:
        items, nxt, t = fetch_list(api, uid, since_id)
        if t is not None:
            total = t
        if not items:
            break

        for it in items:
            mid = item_id(it)
            if mid:
                seen.add(mid)
            if date_filter:
                if item_datetime(it) is None:
                    unparsable += 1
                elif in_date_range(it, start_dt, end_dt):
                    matched += 1

        pages += 1
        if date_filter:
            tip = f"，落在范围内 {matched} 条"
            if unparsable:
                tip += f"，时间认不出 {unparsable} 条"
        else:
            tip = ""
        total_tip = f"（账号共 {total} 条）" if total is not None else ""
        log.info(f"[dry-run] 第 {pages} 页，累计 {len(seen)} 条{tip} {total_tip}")

        # 本页最后一条已经比 --start 还老，再往后只会更老，收工
        if date_filter and start_dt is not None:
            oldest = item_datetime(items[-1])
            if oldest is not None and oldest < start_dt:
                log.info(f"[dry-run] 扫到 {oldest:%Y-%m-%d}，已经比 --start 早了，停。")
                break

        if args.max_pages and pages >= args.max_pages:
            break
        if not nxt:
            break
        since_id = nxt

    if date_filter:
        log.info(f"[dry-run] 共 {len(seen)} 条，其中 {matched} 条在日期范围内。没删东西。")
    else:
        log.info(f"[dry-run] 共 {len(seen)} 条。没删东西。")
    return 0


def delete_loop(page, api, uid, args, log, deleted, skipped):
    done = 0
    stale_pages = 0
    since_id = ""
    rl_hits = 0
    start_dt = args.start
    end_dt = args.end.replace(hour=23, minute=59, second=59) if args.end else None
    date_filter = start_dt is not None or end_dt is not None

    if date_filter:
        s = start_dt.strftime("%Y-%m-%d") if start_dt else "-"
        e = end_dt.strftime("%Y-%m-%d") if end_dt else "-"
        log.info(f"日期模式：{s} ~ {e}（单向扫，不回第一页）")

    while True:
        if args.max and done >= args.max:
            log.info(f"到 --max {args.max} 了，停。")
            break
        if not date_filter and stale_pages >= args.scan_limit:
            log.info("连着好几页都没得删，收工。")
            break

        try:
            items, nxt, total = fetch_list(api, uid, since_id)
        except AuthError as e:
            log.error(f"接口说没登录：{e}")
            log.error(f"删掉 {args.profile_dir} 目录重跑，会重新弹登录。")
            return 2
        except RuntimeError as e:
            log.error(str(e))
            return 2

        if not items:
            log.info("列表返回空，收工。")
            break

        pending = []
        for it in items:
            mid = item_id(it)
            if not mid:
                continue
            if mid in deleted or mid in skipped:
                continue
            if date_filter and not in_date_range(it, start_dt, end_dt):
                continue
            pending.append((mid, it))

        if not pending:
            # 本页全是处理过的，挪游标看下一页，别原地转圈
            stale_pages += 1
            log.info(f"本页 {len(items)} 条都处理过了，往前挪（连续 {stale_pages} 页）")
            if not nxt:
                log.info("到底了。")
                break
            since_id = nxt
            continue

        stale_pages = 0
        deleted_any = False

        for mid, it in pending:
            if args.max and done >= args.max:
                break

            text = " ".join(str(it.get("text_raw") or "").split())[:40]

            if args.mode == "api":
                ok, status, msg = destroy_by_api(api, mid, it)
            else:
                ok = destroy_by_ui(page, uid, it)
                status, msg = (200 if ok else 0), ("" if ok else "UI 点击失败")

            if ok:
                deleted.add(mid)
                deleted_any = True
                done += 1
                rl_hits = 0
                log.info(f"[{done}] 删了 {mid}  {text}")
            else:
                kind = classify(msg)
                if kind == "auth":
                    log.error(f"登录挂了（{msg}），重跑重新登录。")
                    return 2
                if kind == "ratelimit":
                    rl_hits += 1
                    if rl_hits > 8:
                        log.error("一直撞限速，停了，过几小时再来。")
                        return 3
                    wait = min(900.0, 30.0 * (2 ** (rl_hits - 1))) + random.uniform(0, 20)
                    log.warn(f"被限速了（{msg}），歇 {wait:.0f} 秒再试。")
                    time.sleep(wait)
                    continue
                skipped.add(mid, reason=msg or f"http {status}")
                log.warn(f"跳过 {mid}：{msg or status}  {text}")

            if args.batch_size and done and done % args.batch_size == 0:
                log.info(f"删了 {done} 条，歇 {args.batch_pause:.0f} 秒 ...")
                time.sleep(args.batch_pause)
            else:
                time.sleep(args.delay + random.uniform(0, args.jitter))

        if args.max and done >= args.max:
            break

        if date_filter:
            if start_dt is not None:
                oldest = item_datetime(items[-1])
                if oldest is not None and oldest < start_dt:
                    log.info(f"扫到 {oldest:%Y-%m-%d}，已经比 --start 早了，停。")
                    break
            if not nxt:
                log.info("到底了。")
                break
            since_id = nxt
        else:
            if deleted_any:
                # 删过了，列表变了，回第一页重拉
                since_id = ""
            else:
                if not nxt:
                    log.info("到底了。")
                    break
                since_id = nxt

    log.info(f"这次删了 {done} 条；累计 {len(deleted)} 条，跳过 {len(skipped)} 条。")
    return 0


def run(args):
    apply_settings(args)
    data_dir = Path(args.data_dir)
    data_dir.mkdir(parents=True, exist_ok=True)

    skip_file = data_dir / "skipped.jsonl"
    if args.reset_skip and skip_file.exists():
        skip_file.unlink()

    log = Logger(data_dir / "run.log")
    deleted = IdStore(data_dir / "deleted.jsonl")
    skipped = IdStore(skip_file)

    log.info("=" * 60)
    log.info(f"启动：mode={args.mode} dry_run={args.dry_run} max={args.max or 'unlimited'}")
    if args.start or args.end:
        s = args.start.strftime("%Y-%m-%d") if args.start else "-"
        e = args.end.strftime("%Y-%m-%d") if args.end else "-"
        log.info(f"日期：{s} ~ {e}")
    log.info(f"历史：已删 {len(deleted)} 条，跳过 {len(skipped)} 条")

    launch = dict(
        user_data_dir=str(Path(args.profile_dir).resolve()),
        headless=False,
        viewport={"width": 1440, "height": 900},
        args=["--disable-blink-features=AutomationControlled"],
    )
    if args.channel:
        launch["channel"] = args.channel

    code = 0
    with sync_playwright() as pw:
        context = pw.chromium.launch_persistent_context(**launch)
        page = context.pages[0] if context.pages else context.new_page()
        try:
            uid = ensure_login(page, log, args.login_timeout)
            api = Api(page)
            if args.dry_run:
                code = dry_run_scan(api, uid, args, log)
            else:
                code = delete_loop(page, api, uid, args, log, deleted, skipped)
        finally:
            try:
                context.close()
            except Exception:
                pass

    log.close()
    return code


def _run_once(argv):
    """跑一次，把中断和报错都处理掉。返回 True 表示正常结束。"""
    try:
        args = parse_args(argv)
    except SystemExit:
        return False
    try:
        run(args)
        return True
    except KeyboardInterrupt:
        print()
        print("  中断了。进度已经存好，下次跑会接着删。")
        return True
    except Exception as e:
        print()
        print(f"  出错了：{e}")
        print("  把上面这段截图发出来就能排查。")
        return False


def do_logout():
    """清掉本地登录状态，方便换账号。"""
    profile = Path(".browser-profile")
    print()
    print("   这会清掉本机保存的微博登录状态，下次运行要重新扫码。")
    print()
    try:
        a = input("   确定要退出登录吗？输入 yes 继续：").strip()
    except (EOFError, KeyboardInterrupt):
        print()
        return
    if a.lower() != "yes":
        return
    if not profile.exists():
        print()
        print("   没找到登录记录，本来就是未登录状态。")
        return
    try:
        shutil.rmtree(profile)
        print()
        print("   已退出登录。下次运行会重新弹扫码。")
    except Exception as e:
        print()
        print(f"   删除失败：{e}")
        print("   可能是浏览器还开着。把所有浏览器窗口关掉再试一次。")


def do_settings():
    """设置每条之间的等待秒数。"""
    cfg = load_settings()
    print()
    print("   当前设置：")
    print("     基础间隔  %.1f 秒" % cfg["delay"])
    print("     随机抖动  0 ~ %.1f 秒" % cfg["jitter"])
    print()
    print("   间隔越大越安全，但删得越慢。")
    print("   默认每条 2 秒，一万条大约 6 小时。")
    print()
    try:
        d = input("   每条等几秒？（直接回车保持不变）：").strip()
    except (EOFError, KeyboardInterrupt):
        print()
        return
    if not d:
        return
    try:
        v = float(d)
    except ValueError:
        print()
        print("   请输入数字，比如 3 或 2.5")
        return
    if v < 0.5:
        print()
        print("   太小了，容易被微博限速。最少 0.5 秒。")
        return
    if v > 60:
        print()
        print("   太慢了，一万条要跑好几天。最多 60 秒。")
        return
    cfg["delay"] = v
    cfg["jitter"] = max(0.5, min(3.0, v * 0.75))
    save_settings(cfg)
    print()
    print("   已保存：每条等 %.1f 秒（上下浮动 %.1f 秒）" % (cfg["delay"], cfg["jitter"]))


def menu():
    """双击 run.bat 后看到的菜单。"""
    while True:
        print()
        print("=" * 46)
        print("           微博批量删除工具")
        print("=" * 46)
        print()
        print("   [1] 先看看有多少条（不删，推荐先跑这个）")
        print("   [2] 全部删除")
        print("   [3] 按日期删除")
        print("   [4] 先删 3 条试试（验证能不能用）")
        print("   [5] 手动输入命令")
        print("   [6] 退出登录（换一个账号用）")
        cfg = load_settings()
        print("   [7] 设置删除间隔（当前每条 %.1f 秒）" % cfg["delay"])
        print("   [0] 退出")
        print()
        try:
            c = input("   请输入数字后回车：").strip()
        except (EOFError, KeyboardInterrupt):
            print()
            return

        argv = None

        if c == "0":
            return
        elif c == "1":
            argv = ["--dry-run"]
        elif c == "2":
            print()
            print("   ！！会删掉所有微博，删了就找不回来了！！")
            print()
            a = input("   确定要全部删除吗？输入 yes 继续：").strip()
            if a.lower() != "yes":
                continue
            argv = []
        elif c == "3":
            print()
            print("   日期格式：2020-01-01")
            print("   两个都填  = 删这个范围内的")
            print("   只填开始  = 删这天之后的")
            print("   只填结束  = 删这天之前的")
            print()
            d1 = input("   开始日期（不填直接回车）：").strip()
            d2 = input("   结束日期（不填直接回车）：").strip()
            if not d1 and not d2:
                print()
                print("   两个都没填，返回菜单。")
                continue
            argv = []
            if d1:
                argv += ["--start", d1]
            if d2:
                argv += ["--end", d2]
        elif c == "4":
            argv = ["--max", "3"]
        elif c == "5":
            print()
            raw = input("   输入参数，例如 --start 2020-01-01 --end 2020-12-31：").strip()
            argv = raw.split()
            if not argv:
                continue
        elif c == "6":
            do_logout()
            continue
        elif c == "7":
            do_settings()
            continue
        else:
            print()
            print("   没有这个选项，重新选。")
            continue

        print()
        _run_once(argv)
        print()
        print("=" * 46)
        print("   跑完了。删除进度已经存好，下次接着来就行。")
        print("=" * 46)
        try:
            input("   按回车回到菜单（或直接关窗口）：")
        except (EOFError, KeyboardInterrupt):
            print()
            return


def main():
    args = parse_args()
    if args.menu:
        menu()
        return
    try:
        code = run(args)
    except KeyboardInterrupt:
        print()
        print("中断了。进度已经存好，重跑就能接着删。")
        code = 130
    sys.exit(code)


if __name__ == "__main__":
    main()
