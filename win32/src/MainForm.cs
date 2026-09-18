using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WeiboDelete
{
    public class MainForm : Form
    {
        private WebView2 web;
        private RichTextBox logBox;
        private ToolStripStatusLabel statLabel;
        private Button btnCount, btnAll, btnTest, btnRetry, btnLogout, btnStop;

        private Logger log;
        private Store deletedStore, skippedStore;
        private Api api;
        private Logic logic;

        private volatile bool stopFlag = false;
        private bool busy = false;
        private bool ready = false;

        private string dataDir;
        private string profileDir;

        public MainForm()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            dataDir = Path.Combine(baseDir, "data");
            profileDir = Path.Combine(baseDir, "profile");

            Text = "微博批量删除工具";

            // 初始窗口 = 桌面工作区的 80%，水平垂直居中
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int w = (int)(wa.Width * 0.8);
            int h = (int)(wa.Height * 0.8);
            if (w < 900) w = 900;
            if (h < 640) h = 640;
            if (w > wa.Width) w = wa.Width;
            if (h > wa.Height) h = wa.Height;

            StartPosition = FormStartPosition.Manual;
            Width = w;
            Height = h;
            Left = wa.Left + (wa.Width - w) / 2;
            Top = wa.Top + (wa.Height - h) / 2;

            MinimumSize = new Size(760, 520);

            BuildUI();
            Load += OnLoaded;
            FormClosing += OnClosing;
        }

        private void BuildUI()
        {
            Panel topWrap = new Panel();
            topWrap.Dock = DockStyle.Top;
            topWrap.Height = 78;

            FlowLayoutPanel top = new FlowLayoutPanel();
            top.Dock = DockStyle.Fill;
            top.Padding = new Padding(6);
            top.WrapContents = true;

            btnCount = MakeBtn("看看有多少条", OnCount);
            btnAll = MakeBtn("全部删除", OnDeleteAll);
            btnTest = MakeBtn("先删 3 条试试", OnTest3);
            btnRetry = MakeBtn("重试跳过的", OnRetrySkipped);
            btnLogout = MakeBtn("退出登录", OnLogout);
            btnStop = MakeBtn("停止", OnStop);
            btnStop.Enabled = false;

            top.Controls.Add(btnCount);
            top.Controls.Add(btnAll);
            top.Controls.Add(btnTest);
            top.Controls.Add(btnRetry);
            top.Controls.Add(btnLogout);
            top.Controls.Add(btnStop);
            topWrap.Controls.Add(top);

            web = new WebView2();
            web.Dock = DockStyle.Fill;

            logBox = new RichTextBox();
            logBox.Dock = DockStyle.Fill;
            logBox.ReadOnly = true;
            logBox.BackColor = Color.FromArgb(18, 18, 18);
            logBox.ForeColor = Color.Gainsboro;
            logBox.Font = new Font("Consolas", 9f);
            logBox.WordWrap = false;
            logBox.ScrollBars = RichTextBoxScrollBars.Both;
            logBox.HideSelection = false;

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.SplitterDistance = 360;
            split.Panel1.Controls.Add(web);
            split.Panel2.Controls.Add(logBox);

            StatusStrip status = new StatusStrip();
            statLabel = new ToolStripStatusLabel("准备中……");
            status.Items.Add(statLabel);

            Controls.Add(split);
            Controls.Add(topWrap);
            Controls.Add(status);

            split.BringToFront();
            topWrap.BringToFront();
            status.BringToFront();
        }

        private Button MakeBtn(string text, EventHandler h)
        {
            Button b = new Button();
            b.Text = text;
            b.Width = 104;
            b.Height = 30;
            b.Margin = new Padding(3);
            b.Click += h;
            return b;
        }

        private async void OnLoaded(object sender, EventArgs e)
        {
            try
            {
                Directory.CreateDirectory(dataDir);
                Directory.CreateDirectory(profileDir);

                log = new Logger(dataDir);
                deletedStore = Store.Load(Path.Combine(dataDir, "deleted.jsonl"));
                skippedStore = Store.Load(Path.Combine(dataDir, "skipped.jsonl"));

                AppendLog("微博批量删除工具（Win32 版）");
                AppendLog("数据目录：" + dataDir);
                AppendLog("正在初始化浏览器内核……");

                // 先检查系统里有没有 WebView2 Runtime
                string ver = null;
                try
                {
                    CoreWebView2Environment tmp =
                        await CoreWebView2Environment.CreateAsync(null, profileDir);
                    ver = tmp.BrowserVersionString;
                }
                catch (Exception ex0)
                {
                    ver = null;
                    AppendLog("检测浏览器内核失败：" + ex0.Message);
                }

                if (string.IsNullOrEmpty(ver))
                {
                    AppendLog("系统未安装 WebView2 Runtime。");
                        DialogResult dr = MessageBox.Show(
                            "本程序需要 Microsoft Edge WebView2 Runtime 才能运行。"
                            + Environment.NewLine + Environment.NewLine
                            + "Win10 1803 以上 / Win11 系统一般自带。你的系统检测不到它。"
                            + Environment.NewLine + Environment.NewLine
                            + "是否现在打开微软官方下载页面？"
                            + Environment.NewLine
                            + "（选「是」会打开浏览器，下载安装后重新运行本程序即可）",
                            "缺少浏览器内核",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (dr == DialogResult.Yes)
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(
                                "https://developer.microsoft.com/microsoft-edge/webview2/");
                        }
                        catch { }
                    }
                    AppendLog("已提示用户安装 WebView2 Runtime，程序暂停。");
                    return;
                }

                AppendLog("浏览器内核版本：" + ver);
                CoreWebView2Environment env =
                    await CoreWebView2Environment.CreateAsync(null, profileDir);
                await web.EnsureCoreWebView2Async(env);

                web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                web.CoreWebView2.Navigate("https://weibo.com");

                api = new Api(web);
                logic = new Logic(api, log, deletedStore, skippedStore,
                                  AppendLog, UpdateStat, IsStop, GetDelay, GetJitter);

                ready = true;
                AppendLog("浏览器就绪。");
                AppendLog("历史进度：已删 " + deletedStore.Count + " 条，跳过 " + skippedStore.Count + " 条");
                AppendLog("");
                AppendLog("如果上方窗口显示微博登录页，请先扫码登录。");
                UpdateStat(deletedStore.Count, skippedStore.Count);
            }
            catch (Exception ex)
            {
                AppendLog("初始化失败：" + ex.Message);
                MessageBox.Show(
                    "浏览器内核初始化失败。\n\n" +
                    "常见原因：\n" +
                    "1. 系统缺少 WebView2 Runtime（Win10/11 一般自带）\n" +
                    "2. 杀毒软件拦截了 WebView2Loader.dll\n\n" +
                    "错误详情：" + ex.Message,
                    "初始化失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void AppendLog(string s)
        {
            if (InvokeRequired) { Invoke(new Action<string>(AppendLog), s); return; }
            logBox.AppendText(s + "\r\n");
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
            if (log != null) log.Info(s);
        }

        private void UpdateStat(int d, int k)
        {
            if (InvokeRequired) { Invoke(new Action<int, int>(UpdateStat), d, k); return; }
            statLabel.Text = "已删 " + d + " 条    跳过 " + k + " 条";
        }

        private bool IsStop() { return stopFlag; }
        private double GetDelay() { return 2.0; }
        private double GetJitter() { return 1.5; }

        private void SetBusy(bool b)
        {
            busy = b;
            btnCount.Enabled = !b;
            btnAll.Enabled = !b;
            btnTest.Enabled = !b;
            btnRetry.Enabled = !b;
            btnLogout.Enabled = !b;
            btnStop.Enabled = b;
        }

        private async Task<string> GetUidAsync()
        {
            return await api.EvalAsync("(window.$CONFIG && window.$CONFIG.uid) || ''");
        }

        private async Task<bool> EnsureLoginAsync()
        {
            if (!ready || api == null)
            {
                MessageBox.Show("浏览器还没准备好，请稍等。", "提示");
                return false;
            }
            string uid = await GetUidAsync();
            if (string.IsNullOrEmpty(uid))
            {
                MessageBox.Show(
                    "还没登录。\n\n请在上方浏览器窗口里扫码登录微博，登录后再点按钮。",
                    "未登录", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            return true;
        }

        private async void OnCount(object sender, EventArgs e)
        {
            if (busy) return;
            if (!await EnsureLoginAsync()) return;

            SetBusy(true);
            stopFlag = false;
            try
            {
                string uid = await GetUidAsync();
                AppendLog("");
                AppendLog("=== 统计中（不会删任何东西）===");
                string since = "";
                int pages = 0, total = 0;
                while (pages < 500)
                {
                    if (stopFlag) break;
                    ListResult lr = await logic.FetchListAsync(uid, since);
                    if (lr.Error != null) { AppendLog("失败：" + lr.Error); break; }
                    if (lr.Items.Count == 0) break;
                    total += lr.Items.Count;
                    pages++;
                    AppendLog("第 " + pages + " 页，累计 " + total + " 条"
                              + (lr.Total > 0 ? "（账号共 " + lr.Total + " 条）" : ""));
                    if (string.IsNullOrEmpty(lr.NextSince)) break;
                    since = lr.NextSince;
                    await Task.Delay(400);
                }
                AppendLog("统计完成：共 " + total + " 条。");
            }
            catch (Exception ex) { AppendLog("出错：" + ex.Message); }
            finally { SetBusy(false); }
        }

        private async void OnDeleteAll(object sender, EventArgs e)
        {
            if (busy) return;
            if (!await EnsureLoginAsync()) return;

            DialogResult r = MessageBox.Show(
                "会删掉账号里所有未删除的微博，删了就找不回来。\n\n确定继续吗？",
                "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;

            await RunDeleteAsync(0);
        }

        private async void OnTest3(object sender, EventArgs e)
        {
            if (busy) return;
            if (!await EnsureLoginAsync()) return;

            DialogResult r = MessageBox.Show(
                "会删掉 3 条微博，用来验证工具能用。\n\n确定继续吗？",
                "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            await RunDeleteAsync(3);
        }

        private async Task RunDeleteAsync(int maxCount)
        {
            SetBusy(true);
            stopFlag = false;
            AppendLog("");
            AppendLog("=== 开始删除" + (maxCount > 0 ? "（最多 " + maxCount + " 条）" : "") + " ===");
            try
            {
                string uid = await GetUidAsync();
                await logic.RunAsync(uid, maxCount);
            }
            catch (Exception ex)
            {
                AppendLog("出错：" + ex.Message);
            }
            finally
            {
                SetBusy(false);
                AppendLog("");
                AppendLog("全部完成。");
            }
        }

        private void OnRetrySkipped(object sender, EventArgs e)
        {
            if (busy) return;
            int n = skippedStore != null ? skippedStore.Count : 0;
            if (n == 0)
            {
                MessageBox.Show("没有跳过记录。", "提示");
                return;
            }
            DialogResult r = MessageBox.Show(
                "当前有 " + n + " 条跳过记录。\n\n" +
                "清空后，这些微博会在下次删除时重新被尝试。\n" +
                "真正删不掉的（原博已不可见）会再次被跳过。\n\n" +
                "确定清空吗？",
                "重试跳过的", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            skippedStore.Clear();
            AppendLog("已清空 " + n + " 条跳过记录。");
            UpdateStat(deletedStore.Count, 0);
            MessageBox.Show("已清空。现在可以点「全部删除」重新尝试。", "完成");
        }

        private void OnLogout(object sender, EventArgs e)
        {
            if (busy) return;
            DialogResult r = MessageBox.Show(
                "会清掉本机保存的登录状态，下次运行要重新扫码。\n\n确定吗？",
                "退出登录", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            try
            {
                if (Directory.Exists(profileDir))
                    Directory.Delete(profileDir, true);
                AppendLog("已退出登录。请关闭程序后重新打开。");
                MessageBox.Show("已退出登录。请关闭程序再重新打开，会重新弹扫码。", "完成");
            }
            catch (Exception ex)
            {
                MessageBox.Show("清理失败：" + ex.Message + "\n\n可能是浏览器还开着，关掉所有窗口再试。",
                    "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnStop(object sender, EventArgs e)
        {
            stopFlag = true;
            AppendLog(">>> 收到停止指令，正在收尾……");
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (busy)
            {
                DialogResult r = MessageBox.Show(
                    "正在删除中，确定退出吗？\n进度已保存，下次可以接着删。",
                    "确认退出", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
                stopFlag = true;
            }
        }
    }
}
