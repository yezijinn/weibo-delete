using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace WeiboDelete
{
    public class Item
    {
        public string Id;
        public string OriMid;
        public string Mblogid;
        public string Text;
        public bool Quick;
        public bool HasOri;
    }

    public class ListResult
    {
        public List<Item> Items = new List<Item>();
        public string NextSince = "";
        public long Total = -1;
        public string Error;
    }

    public class Logic
    {
        private const int EmptyTolerance = 5;
        private const int TransientRetries = 5;

        private const string ListApi = "https://weibo.com/ajax/statuses/mymblog?uid={0}&page=1&feature=0";
        private const string DestroyApi = "https://weibo.com/ajax/statuses/destroy";

        private const string XhrQuick =
            "{\"x-requested-with\":\"XMLHttpRequest\",\"client-version\":\"v1.1.247\",\"server-version\":\"v2026.09.15.1\"}";
        private const string XhrNormal =
            "{\"x-requested-with\":\"XMLHttpRequest\"}";

        private readonly Api api;
        private readonly Logger log;
        private readonly Store deleted;
        private readonly Store skipped;
        private readonly Action<string> onLog;
        private readonly Action<int, int> onStat;
        private readonly Func<bool> shouldStop;
        private readonly Func<double> getDelay;
        private readonly Func<double> getJitter;

        public int Done;
        public string LastError = "";
        public string LastMsg = "";
        public int LastStatus;

        private static readonly Random rnd = new Random();

        public Logic(Api api, Logger log, Store deleted, Store skipped,
                     Action<string> onLog, Action<int, int> onStat,
                     Func<bool> shouldStop, Func<double> getDelay, Func<double> getJitter)
        {
            this.api = api;
            this.log = log;
            this.deleted = deleted;
            this.skipped = skipped;
            this.onLog = onLog;
            this.onStat = onStat;
            this.shouldStop = shouldStop;
            this.getDelay = getDelay;
            this.getJitter = getJitter;
        }

        private void Log(string s) { if (onLog != null) onLog(s); }

        private async Task SleepSecAsync(double sec)
        {
            int ms = (int)(sec * 1000.0);
            if (ms < 0) ms = 0;
            if (ms > 0) await Task.Delay(ms);
        }

        private async Task PauseAsync()
        {
            double b = getDelay != null ? getDelay() : 2.0;
            double j = getJitter != null ? getJitter() : 1.5;
            double extra = j > 0 ? rnd.NextDouble() * j : 0;
            await SleepSecAsync(b + extra);
        }

        public async Task<ListResult> FetchListAsync(string uid, string sinceId)
        {
            ListResult r = new ListResult();

            string url = string.Format(ListApi, uid);
            if (!string.IsNullOrEmpty(sinceId))
                url += "&since_id=" + Uri.EscapeDataString(sinceId);

            string res;
            try { res = await api.FetchAsync(url, "GET", null, null, null); }
            catch (Exception ex) { r.Error = "请求异常：" + ex.Message; return r; }

            long status = Json.Num(res, "status", 0);
            string text = Json.Str(res, "text");
            if (text == null) text = "";

            if (status != 200)
            {
                string detail = Json.Str(res, "text");
                r.Error = "HTTP " + status + (string.IsNullOrEmpty(detail) ? "" : "  " + Trunc(detail, 200));
                return r;
            }
            if (!Json.Ok(text)) { r.Error = "接口返回失败：" + Trunc(text, 100); return r; }

            string arr = Json.Arr(text, "list");
            if (arr != null)
            {
                List<string> objs = Json.SplitObjects(arr);
                for (int i = 0; i < objs.Count; i++)
                    r.Items.Add(ParseItem(objs[i]));
            }

            string nxt = Json.Str(text, "since_id");
            r.NextSince = (nxt == null || nxt == "0") ? "" : nxt;
            r.Total = Json.Num(text, "total", -1);
            return r;
        }

        private static Item ParseItem(string obj)
        {
            Item it = new Item();
            it.Id = Json.Str(obj, "idstr");
            if (string.IsNullOrEmpty(it.Id)) it.Id = Json.Str(obj, "id");
            it.OriMid = Json.Str(obj, "ori_mid");
            it.Mblogid = Json.Str(obj, "mblogid");
            it.Text = Json.Str(obj, "text_raw");
            if (it.Text == null) it.Text = "";

            it.Quick = obj.IndexOf("quick_forward", StringComparison.Ordinal) >= 0;
            it.HasOri = !string.IsNullOrEmpty(it.OriMid) && it.OriMid != "0";
            if (!it.HasOri && obj.IndexOf("retweeted_status", StringComparison.Ordinal) >= 0)
                it.HasOri = true;
            return it;
        }

        private async Task<bool> PostDestroyAsync(string id, bool quick)
        {
            string headers = quick ? XhrQuick : XhrNormal;
            string body = "id=" + Uri.EscapeDataString(id);

            string res;
            try { res = await api.FetchAsync(DestroyApi, "POST", body, null, headers); }
            catch (Exception ex) { LastStatus = 0; LastMsg = ex.Message; return false; }

            long status = Json.Num(res, "status", 0);
            string text = Json.Str(res, "text");
            if (text == null) text = "";

            LastStatus = (int)status;
            if (status != 200) { LastMsg = "HTTP " + status; return false; }

            if (Json.Ok(text)) { LastMsg = ""; return true; }

            string m = Json.Str(text, "msg");
            if (string.IsNullOrEmpty(m)) m = Json.Str(text, "error");
            if (string.IsNullOrEmpty(m)) m = Trunc(text, 100);
            LastMsg = m;
            return false;
        }

        private List<string> QfIds(Item it, string mid)
        {
            List<string> ids = new List<string>();
            AddId(ids, it.OriMid);
            AddId(ids, it.Mblogid);
            AddId(ids, it.Id);
            AddId(ids, mid);
            return ids;
        }

        private static void AddId(List<string> ids, string v)
        {
            if (!string.IsNullOrEmpty(v) && v != "0" && !ids.Contains(v))
                ids.Add(v);
        }

        public async Task<bool> DestroyAsync(string mid, Item item)
        {
            LastError = "";
            bool quick = item != null && (item.Quick || item.HasOri);

            if (quick && item != null)
            {
                List<string> ids = QfIds(item, mid);
                List<string> tried = new List<string>();
                string hint = null;

                for (int i = 0; i < ids.Count; i++)
                {
                    bool ok = await PostDestroyAsync(ids[i], true);
                    if (ok) return true;
                    tried.Add(Tail6(ids[i]) + "=" + LastStatus);
                    if (hint == null && !string.IsNullOrEmpty(LastMsg)) hint = LastMsg;
                }

                if (!item.HasOri)
                {
                    if (await PostDestroyAsync(mid, false)) return true;
                }

                LastError = "取消快转失败：" + string.Join(" ", tried.ToArray());
                if (hint != null) LastError += " | " + Trunc(hint, 100);
                return false;
            }

            if (await PostDestroyAsync(mid, false)) return true;

            if (item != null && item.HasOri)
            {
                List<string> ids = QfIds(item, mid);
                for (int i = 0; i < ids.Count; i++)
                {
                    if (await PostDestroyAsync(ids[i], true)) return true;
                }
            }

            LastError = (LastMsg == null ? "" : LastMsg) + " http " + LastStatus;
            return false;
        }

        private static readonly string[] AuthKw =
        { "登录", "未登录", "先登录", "login", "logout", "401" };

        private static readonly string[] RateKw =
        { "频繁", "太快", "稍后", "限制", "过于", "休息", "403", "429" };

        private static readonly string[] GoneKw =
        { "已不可见", "不存在", "已被删除", "没有权限", "查看权限", "invalid", "not found", "404" };

        private static readonly string[] TransKw =
        { "timeout", "超时", "network", "网络", "服务器", "系统繁忙", "稍后再试", "请重试",
          "500", "502", "503", "504", "fetch error" };

        public static string Classify(string msg, int status)
        {
            string low = msg == null ? "" : msg.ToLowerInvariant();
            string raw = msg == null ? "" : msg;

            if (raw.Length > 0)
            {
                for (int i = 0; i < AuthKw.Length; i++)
                    if (low.IndexOf(AuthKw[i], StringComparison.Ordinal) >= 0) return "auth";
                for (int i = 0; i < RateKw.Length; i++)
                    if (raw.IndexOf(RateKw[i], StringComparison.Ordinal) >= 0) return "ratelimit";
                for (int i = 0; i < GoneKw.Length; i++)
                    if (raw.IndexOf(GoneKw[i], StringComparison.OrdinalIgnoreCase) >= 0) return "gone";
                for (int i = 0; i < TransKw.Length; i++)
                    if (raw.IndexOf(TransKw[i], StringComparison.OrdinalIgnoreCase) >= 0) return "transient";
            }

            if (status == 500 || status == 502 || status == 503 || status == 504 || status == 0)
                return "transient";
            return "other";
        }

        public async Task RunAsync(string uid, int maxCount)
        {
            Done = 0;
            int stale = 0;
            int rlHits = 0;
            int emptyHits = 0;
            string sinceId = "";

            while (true)
            {
                if (shouldStop != null && shouldStop()) { Log("收到停止指令，收工。"); break; }
                if (maxCount > 0 && Done >= maxCount) { Log("到达上限 " + maxCount + " 条，停。"); break; }

                ListResult lr;
                try { lr = await FetchListAsync(uid, sinceId); }
                catch (Exception ex) { Log("拉列表异常：" + ex.Message); return; }

                if (lr.Error != null)
                {
                    emptyHits++;
                    if (emptyHits >= EmptyTolerance) { Log("连续失败，本轮停止：" + lr.Error); break; }
                    Log("拉列表失败：" + lr.Error + "（" + emptyHits + "/" + EmptyTolerance + "），重试");
                    await SleepSecAsync(4);
                    continue;
                }

                if (lr.Items.Count == 0)
                {
                    emptyHits++;
                    if (emptyHits >= EmptyTolerance)
                    {
                        Log("连续 " + EmptyTolerance + " 次空列表，本轮到底。");
                        break;
                    }
                    Log("列表为空（" + emptyHits + "/" + EmptyTolerance + "），3 秒后重试");
                    await SleepSecAsync(3);
                    continue;
                }
                emptyHits = 0;

                List<Item> pending = new List<Item>();
                for (int i = 0; i < lr.Items.Count; i++)
                {
                    Item it = lr.Items[i];
                    if (string.IsNullOrEmpty(it.Id)) continue;
                    if (deleted.Contains(it.Id) || skipped.Contains(it.Id)) continue;
                    pending.Add(it);
                }

                if (pending.Count == 0)
                {
                    stale++;
                    Log("本页 " + lr.Items.Count + " 条都处理过了，往前挪（连续 " + stale + " 页）");
                    if (string.IsNullOrEmpty(lr.NextSince)) { Log("到底了。"); break; }
                    sinceId = lr.NextSince;
                    continue;
                }

                stale = 0;

                for (int i = 0; i < pending.Count; i++)
                {
                    if (shouldStop != null && shouldStop()) break;
                    if (maxCount > 0 && Done >= maxCount) break;

                    Item it = pending[i];
                    string text = OneLine(it.Text, 40);

                    bool ok = false;
                    try { ok = await DestroyAsync(it.Id, it); }
                    catch (Exception ex) { ok = false; LastError = ex.Message; }
                    string msg = LastError;

                    if (ok)
                    {
                        deleted.Add(it.Id, null);
                        Done++;
                        rlHits = 0;
                        Log("[" + Done + "] 删了 " + it.Id + "  " + text);
                        if (onStat != null) onStat(deleted.Count, skipped.Count);
                    }
                    else
                    {
                        string kind = Classify(msg, LastStatus);

                        if (kind == "auth")
                        {
                            Log("登录失效：" + msg + "，停止。请重新登录。");
                            return;
                        }

                        if (kind == "ratelimit")
                        {
                            rlHits++;
                            if (rlHits > 8) { Log("持续限速，停止。过几小时再来。"); return; }
                            int wait = Math.Min(900, 30 * (1 << (rlHits - 1))) + rnd.Next(0, 20);
                            Log("被限速（" + msg + "），歇 " + wait + " 秒后重试。");
                            await SleepSecAsync(wait);
                            i--;
                            continue;
                        }

                        if (kind == "transient")
                        {
                            bool recovered = false;
                            for (int k = 0; k < TransientRetries; k++)
                            {
                                int w = 4 * (k + 1) + rnd.Next(0, 3);
                                Log("删除失败（" + msg + "），" + w + " 秒后重试（" + (k + 1) + "/" + TransientRetries + "）");
                                await SleepSecAsync(w);
                                try { ok = await DestroyAsync(it.Id, it); }
                                catch (Exception ex) { ok = false; LastError = ex.Message; }
                                msg = LastError;
                                if (ok) { recovered = true; break; }
                                string k2 = Classify(msg, LastStatus);
                                if (k2 == "auth") { Log("登录失效，停止。"); return; }
                                if (k2 == "gone" || k2 == "other") break;
                            }
                            if (recovered)
                            {
                                deleted.Add(it.Id, null);
                                Done++;
                                rlHits = 0;
                                Log("[" + Done + "] 删了 " + it.Id + "  " + text + "（重试后成功）");
                                if (onStat != null) onStat(deleted.Count, skipped.Count);
                            }
                            else
                            {
                                Log("暂时删不掉 " + it.Id + "（" + msg + "），本次放过，下次会重试  " + text);
                            }
                        }
                        else
                        {
                            skipped.Add(it.Id, msg);
                            Log("跳过 " + it.Id + "：" + msg + "  " + text);
                            if (onStat != null) onStat(deleted.Count, skipped.Count);
                        }
                    }

                    await PauseAsync();
                }

                if (maxCount > 0 && Done >= maxCount) break;

                if (string.IsNullOrEmpty(lr.NextSince))
                {
                    Log("到底了。");
                    break;
                }
                sinceId = lr.NextSince;
            }

            Log("本轮结束：删了 " + Done + " 条；累计 " + deleted.Count
                + " 条，跳过 " + skipped.Count + " 条。");
            if (onStat != null) onStat(deleted.Count, skipped.Count);
        }

        private static string Trunc(string s, int n)
        {
            if (s == null) return "";
            if (s.Length <= n) return s;
            return s.Substring(0, n) + "...";
        }

        private static string Tail6(string s)
        {
            if (s == null) return "";
            if (s.Length <= 6) return s;
            return s.Substring(s.Length - 6);
        }

        private static string OneLine(string s, int n)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            bool lastSpace = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\r' || c == '\n' || c == '\t' || c == ' ')
                {
                    if (!lastSpace) { sb.Append(' '); lastSpace = true; }
                }
                else
                {
                    sb.Append(c);
                    lastSpace = false;
                }
                if (sb.Length >= n) break;
            }
            return sb.ToString().Trim();
        }
    }
}
