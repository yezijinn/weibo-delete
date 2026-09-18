using System;
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

        public bool IsAlive
        {
            get { return cdp != null && cdp.Alive; }
        }

        public static string FindBrowser()
        {
            // 1) 优先用程序目录里自带的 Chromium，不依赖系统环境
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] own = new string[]
            {
                Path.Combine(baseDir, @"browser\chrome-win64\chrome.exe"),
                Path.Combine(baseDir, @"browser\chrome.exe"),
                Path.Combine(baseDir, "chrome.exe"),
            };
            for (int i = 0; i < own.Length; i++)
                if (File.Exists(own[i])) return own[i];

            // 2) 退而求其次，用系统的 Edge / Chrome
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            string[] cands = new string[]
            {
                Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf,   @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf,   @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
            };
            for (int i = 0; i < cands.Length; i++)
                if (File.Exists(cands[i])) return cands[i];
            return null;
        }

        public void Launch(string profileDir, int debugPort)
        {
            ProfileDir = profileDir;
            port = debugPort;

            string exe = FindBrowser();
            if (exe == null)
                throw new Exception("找不到 Microsoft Edge 或 Google Chrome。\n"
                    + "Windows 10/11 一般自带 Edge，如果没有请先装一个。");

            Directory.CreateDirectory(profileDir);

            StringBuilder a = new StringBuilder();
            a.Append("--remote-debugging-port=").Append(port).Append(" ");
            a.Append("--user-data-dir=\"").Append(profileDir).Append("\" ");
            a.Append("--no-first-run --no-default-browser-check ");
            a.Append("--disable-sync --start-maximized ");
            a.Append("about:blank");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = exe;
            psi.Arguments = a.ToString();
            psi.UseShellExecute = false;
            proc = Process.Start(psi);

            WaitForPort();
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

        public async Task OpenPageAsync(string url)
        {
            string body = HttpPut("http://127.0.0.1:" + port + "/json/new?"
                                  + Uri.EscapeDataString(url));
            if (body == null)
                throw new Exception("无法新建浏览器标签页");

            string wsUrl = Json.Str(body, "webSocketDebuggerUrl");
            if (string.IsNullOrEmpty(wsUrl))
                throw new Exception("拿不到调试地址");

            cdp = new Cdp();
            await cdp.ConnectAsync(wsUrl);

            await cdp.SendAsync("Page.enable", "{}");
            await cdp.SendAsync("Runtime.enable", "{}");
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

        public async Task ClearCacheAsync()
        {
            try { await cdp.SendAsync("Network.enable", "{}"); } catch { }
            try { await cdp.SendAsync("Network.clearBrowserCache", "{}"); } catch { }
            try { await cdp.SendAsync("Network.clearBrowserCookies", "{}"); } catch { }
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
