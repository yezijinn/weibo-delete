using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WeiboDelete
{
    /// <summary>
    /// 启动系统自带的 Edge（或 Chrome），通过 CDP 控制它。
    /// 用的是真实完整的浏览器窗口，不是内嵌控件。
    /// </summary>
    public class Browser : IDisposable
    {
        private Process proc;
        private Cdp cdp;
        private int port;
        public string ProfileDir;
        public IntPtr BrowserHwnd = IntPtr.Zero;

        public bool IsAlive
        {
            get { return cdp != null && cdp.Alive; }
        }

        public static string FindBrowser()
        {
            // 只用程序目录里自带的 Chromium，绝不调用系统浏览器。
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] own = new string[]
            {
                Path.Combine(baseDir, @"browser\chrome-win64\chrome.exe"),
                Path.Combine(baseDir, @"browser\chrome.exe"),
                Path.Combine(baseDir, "chrome.exe"),
            };
            for (int i = 0; i < own.Length; i++)
                if (File.Exists(own[i])) return own[i];
            return null;
        }

        /// <summary>写 Preferences，设默认搜索引擎为百度，关掉各种推荐和欢迎页。</summary>
        private static void WritePreferences(string profileDir)
        {
            try
            {
                string def = Path.Combine(profileDir, "Default");
                Directory.CreateDirectory(def);
                string pf = Path.Combine(def, "Preferences");

                StringBuilder sb = new StringBuilder();
                sb.Append("{");
                sb.Append("\"browser\":{\"show_home_button\":false,\"has_seen_welcome_page\":true},");
                sb.Append("\"distribution\":{");
                sb.Append("  \"import_bookmarks\":false,\"import_history\":false,");
                sb.Append("  \"import_search_engine\":false,\"import_searches\":false,");
                sb.Append("  \"make_chrome_default\":false,\"make_chrome_default_for_user\":false,");
                sb.Append("  \"skip_first_run_ui\":true,\"show_welcome_page\":false,");
                sb.Append("  \"suppress_first_run_default_browser_prompt\":true");
                sb.Append("},");
                sb.Append("\"default_search_provider\":{");
                sb.Append("  \"enabled\":true,");
                sb.Append("  \"name\":\"百度\",");
                sb.Append("  \"keyword\":\"baidu.com\",");
                sb.Append("  \"search_url\":\"https://www.baidu.com/s?wd={searchTerms}\",");
                sb.Append("  \"suggest_url\":\"https://www.baidu.com/sugrec?prod=pc&wd={searchTerms}\",");
                sb.Append("  \"favicon_url\":\"https://www.baidu.com/favicon.ico\",");
                sb.Append("  \"encoding\":\"UTF-8\",");
                sb.Append("  \"id\":0");
                sb.Append("},");
                sb.Append("\"default_search_provider_data\":{");
                sb.Append("  \"template_url_data\":{");
                sb.Append("    \"short_name\":\"百度\",");
                sb.Append("    \"keyword\":\"baidu.com\",");
                sb.Append("    \"url\":\"https://www.baidu.com/s?wd={searchTerms}\",");
                sb.Append("    \"suggestions_url\":\"https://www.baidu.com/sugrec?prod=pc&wd={searchTerms}\",");
                sb.Append("    \"favicon_url\":\"https://www.baidu.com/favicon.ico\",");
                sb.Append("    \"safe_for_autoreplace\":false,");
                sb.Append("    \"prepopulate_id\":0");
                sb.Append("  }");
                sb.Append("},");
                sb.Append("\"intl\":{\"app_locale\":\"zh-CN\",\"intl_app_locale\":\"zh-CN\"},");
                sb.Append("\"profile\":{\"exit_type\":\"Normal\",\"exited_cleanly\":true},");
                sb.Append("\"search\":{\"suggest_enabled\":false},");
                sb.Append("\"signin\":{\"allowed\":false,\"allowed_on_next_startup\":false},");
                sb.Append("\"sync\":{\"requested\":false},");
                sb.Append("\"toolbar\":{\"show_bookmarks\":false}");
                sb.Append("}");

                File.WriteAllText(pf, sb.ToString(), new UTF8Encoding(false));

                string wr = Path.Combine(def, "Welcome");
                if (File.Exists(wr)) File.Delete(wr);
            }
            catch { }
        }

        public void Launch(string profileDir, int debugPort)
        {
            ProfileDir = profileDir;
            port = debugPort;

            string exe = FindBrowser();
            if (exe == null)
                throw new Exception(
                    "找不到内置浏览器。\n\n"
                    + "程序目录下应该有 browser\\chrome-win64\\chrome.exe。\n"
                    + "请确认解压完整，没有漏掉 browser 文件夹，\n"
                    + "也不要单独把 exe 拖出来运行。");

            Directory.CreateDirectory(profileDir);

            // 写一份 Preferences，把默认搜索引擎设为百度（中国用户）
            WritePreferences(profileDir);

            // 删掉 First Run 标记，避免首次启动弹欢迎页
            try
            {
                string fr = Path.Combine(profileDir, "First Run");
                if (File.Exists(fr)) File.Delete(fr);
            }
            catch { }

            StringBuilder a = new StringBuilder();
            a.Append("--remote-debugging-port=").Append(port).Append(" ");
            a.Append("--user-data-dir=\"").Append(profileDir).Append("\" ");

            // 关掉一切欢迎 / 引导 / 同步 / 推荐
            a.Append("--no-first-run ");
            a.Append("--no-default-browser-check ");
            a.Append("--no-service-autorun ");
            a.Append("--disable-sync ");
            a.Append("--disable-background-networking ");
            a.Append("--disable-component-update ");
            a.Append("--disable-default-apps ");
            a.Append("--disable-extensions ");
            a.Append("--disable-features=Translate,OptimizationHints,msEdgeTranslate,"
                    + "ChromeWhatsNewUI,MediaRouter,PrivacySandboxSettings4,"
                    + "EdgeCollections,EdgeShoppingAssistant,EdgeSidebar,"
                    + "ShowRecommendations,SigninInterception ");
            a.Append("--lang=zh-CN ");
            a.Append("--new-window ");

            // 普通窗口 + 指定 URL。不用 --app，因为 --app 模式的 target
            // 类型不是 page，CDP 抓不到，会连不上。
            // 地址栏靠嵌入时的窗口裁剪隐藏。
            a.Append("https://weibo.com");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.Arguments = a.ToString();
            psi.UseShellExecute = false;
            proc = Process.Start(psi);

            WaitForPort();
        }

        /// <summary>轮询找到浏览器的主窗口句柄。
        /// 注意：Chromium 的窗口常常属于子进程，所以要遍历同目录下所有 chrome 进程。</summary>
        public IntPtr FindBrowserWindow()
        {
            for (int i = 0; i < 80; i++)
            {
                List<int> pids = AllBrowserPids();
                List<IntPtr> wins = Embed.WindowsOf(pids.ToArray());
                if (wins.Count > 0)
                {
                    // 挑最大的那个（真正的主窗口）
                    IntPtr best = wins[0];
                    int bestArea = 0;
                    for (int k = 0; k < wins.Count; k++)
                    {
                        int w, h;
                        Embed.GetSize(wins[k], out w, out h);
                        int area = w * h;
                        if (area > bestArea) { bestArea = area; best = wins[k]; }
                    }
                    BrowserHwnd = best;
                    return BrowserHwnd;
                }
                System.Threading.Thread.Sleep(250);
            }
            return IntPtr.Zero;
        }

        /// <summary>找出所有从本程序内置浏览器目录启动的 chrome 进程。</summary>
        private List<int> AllBrowserPids()
        {
            List<int> result = new List<int>();
            try
            {
                string exe = FindBrowser();
                if (exe == null) return result;
                string dir = Path.GetDirectoryName(exe).ToLowerInvariant();

                System.Diagnostics.Process[] all =
                    System.Diagnostics.Process.GetProcessesByName("chrome");
                for (int i = 0; i < all.Length; i++)
                {
                    try
                    {
                        string path = all[i].MainModule.FileName.ToLowerInvariant();
                        if (path.StartsWith(dir))
                            result.Add(all[i].Id);
                    }
                    catch { }
                    finally { try { all[i].Dispose(); } catch { } }
                }
            }
            catch { }
            if (result.Count == 0)
            {
                try { result.Add(proc.Id); } catch { }
            }
            return result;
        }

        /// <summary>把浏览器窗口嵌到指定控件里。</summary>
        public bool AttachTo(IntPtr parentHwnd, int w, int h)
        {
            if (BrowserHwnd == IntPtr.Zero)
                BrowserHwnd = FindBrowserWindow();
            if (BrowserHwnd == IntPtr.Zero) return false;

            bool ok = Embed.Attach(BrowserHwnd, parentHwnd, w, h);
            if (ok && EmbedOffsetY > 0)
                Embed.SetOffset(BrowserHwnd, 0, -EmbedOffsetY, w, h + EmbedOffsetY);
            return ok;
        }

        /// <summary>嵌入时向上偏移的像素数，用来藏掉地址栏。</summary>
        public int EmbedOffsetY = 0;

        public void ResizeEmbedded(int w, int h)
        {
            if (BrowserHwnd == IntPtr.Zero) return;
            if (EmbedOffsetY > 0)
                Embed.SetOffset(BrowserHwnd, 0, -EmbedOffsetY, w, h + EmbedOffsetY);
            else
                Embed.Resize(BrowserHwnd, w, h);
        }

        private void WaitForPort()
        {
            for (int i = 0; i < 80; i++)
            {
                Thread.Sleep(250);
                try
                {
                    string ver = HttpGet("http://127.0.0.1:" + port + "/json/version");
                    if (ver != null && ver.IndexOf("webSocketDebuggerUrl") >= 0)
                        return;
                }
                catch { }
            }
            throw new Exception("浏览器调试端口没起来（端口 " + port + "）。\n"
                + "可能被安全软件拦了，或者端口被占用。");
        }

        /// <summary>连上浏览器的第一个页面标签，然后主动导航到微博。</summary>
        public async Task OpenPageAsync()
        {
            // 挑第一个 page 类型的 target（跳过扩展、service worker 等）
            string wsUrl = null;
            for (int i = 0; i < 60; i++)
            {
                string list = HttpGet("http://127.0.0.1:" + port + "/json/list");
                if (list != null)
                {
                    wsUrl = PickFirstPageWs(list);
                    if (!string.IsNullOrEmpty(wsUrl)) break;
                }
                Thread.Sleep(250);
            }
            if (string.IsNullOrEmpty(wsUrl))
                throw new Exception("浏览器里没有可用的页面标签");

            cdp = new Cdp();
            await cdp.ConnectAsync(wsUrl);

            await cdp.SendAsync("Page.enable", "{}");
            await cdp.SendAsync("Runtime.enable", "{}");

            // 主动导航到微博，等它加载完
            string navParam = "{\"url\":\"https://weibo.com\"}";
            try { await cdp.SendAsync("Page.navigate", navParam); } catch { }

            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(500);
                try
                {
                    string href = await EvalAsync("location.href");
                    if (!string.IsNullOrEmpty(href) && href.IndexOf("weibo.com") >= 0
                        && href.IndexOf("about:") < 0)
                        return;
                }
                catch { }
            }
        }

        /// <summary>从 /json/list 里挑第一个 type=page 且有调试地址的 target。</summary>
        private static string PickFirstPageWs(string listJson)
        {
            int i = 0;
            while (true)
            {
                int start = listJson.IndexOf('{', i);
                if (start < 0) return null;
                int end = FindObjectEnd(listJson, start);
                if (end < 0) return null;

                string obj = listJson.Substring(start, end - start + 1);
                i = end + 1;

                string type = Json.Str(obj, "type");
                if (type != "page") continue;

                string url = Json.Str(obj, "url");
                if (url != null && url.StartsWith("chrome-extension"))
                    continue;

                string ws = Json.Str(obj, "webSocketDebuggerUrl");
                if (!string.IsNullOrEmpty(ws)) return ws;
            }
        }

        /// <summary>从 /json/list 里找出 weibo.com 标签的 webSocketDebuggerUrl。</summary>
        private static string PickWeiboTabWs(string listJson)
        {
            // 手工解析：[{...},{...}]，每个对象里有 "url":"..." 和 "webSocketDebuggerUrl":"..."
            int i = 0;
            while (true)
            {
                int start = listJson.IndexOf('{', i);
                if (start < 0) return null;
                int end = FindObjectEnd(listJson, start);
                if (end < 0) return null;

                string obj = listJson.Substring(start, end - start + 1);
                string url = Json.Str(obj, "url");
                if (url != null && url.IndexOf("weibo.com") >= 0)
                {
                    string ws = Json.Str(obj, "webSocketDebuggerUrl");
                    if (!string.IsNullOrEmpty(ws)) return ws;
                }
                i = end + 1;
            }
        }

        private static int FindObjectEnd(string s, int start)
        {
            int depth = 0;
            bool inStr = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        public async Task<string> EvalAsync(string jsExpr)
        {
            string p = "{\"expression\":" + Json.JsStr(jsExpr)
                     + ",\"returnByValue\":true,\"awaitPromise\":true}";
            string resp = await cdp.SendAsync("Runtime.evaluate", p);
            return ExtractEvalValue(resp);
        }

        private static string ExtractEvalValue(string resp)
        {
            // 有异常就把异常信息带出来
            if (resp.IndexOf("\"exceptionDetails\"") >= 0)
            {
                string ex = Json.Str(resp, "text");
                if (ex != null) return "JS异常: " + ex;
            }

            int i = resp.IndexOf("\"result\":{");
            if (i < 0) return "";
            string sub = resp.Substring(i);

            if (sub.IndexOf("\"subtype\":\"null\"") >= 0) return "";

            string v = Json.Str(sub, "value");
            if (v != null) return v;

            string desc = Json.Str(sub, "description");
            if (desc != null) return desc;
            return "";
        }

        /// <summary>只清 HTTP 缓存，绝不碰 Cookie（删了会掉登录态）。</summary>
        public async Task ClearCacheAsync()
        {
            try { await cdp.SendAsync("Network.enable", "{}"); } catch { }
            try { await cdp.SendAsync("Network.clearBrowserCache", "{}"); } catch { }
            // 注意：不要调 Network.clearBrowserCookies，
            // 那会把微博登录 cookie 一起删掉，后续请求全部 403。
        }

        public async Task NavigateAsync(string url)
        {
            string p = "{\"url\":" + Json.JsStr(url) + "}";
            await cdp.SendAsync("Page.navigate", p);
        }

        private static string HttpGet(string url)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 3000;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        private static string HttpPut(string url)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "PUT";
                req.Timeout = 5000;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        public void Dispose()
        {
            try { if (cdp != null) cdp.Dispose(); } catch { }
            try
            {
                if (proc != null && !proc.HasExited)
                {
                    proc.Kill();
                    proc.WaitForExit(3000);
                }
            }
            catch { }
        }
    }
}
