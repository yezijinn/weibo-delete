using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WeiboDelete
{
    public class MainForm : Form
    {
        private RichTextBox logBox;
        private ToolStripStatusLabel statLabel;
        private Label hintLabel;
        private Button btnCount, btnAll, btnTest, btnRetry, btnLogout, btnStop;

        private Logger log;
        private Store deletedStore, skippedStore;
        private Api api;
        private Logic logic;
        private Browser browser;

        private volatile bool stopFlag = false;
        private bool busy = false;
        private bool ready = false;

        private string dataDir;
        private string profileDir;

        public MainForm()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            dataDir = Path.Combine(baseDir, "data");
            profileDir = Path.Combine(baseDir, "browser-profile");

            Text = "微博批量删除工具";

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

            Panel hintWrap = new Panel();
            hintWrap.Dock = DockStyle.Top;
            hintWrap.Height = 40;

            hintLabel = new Label();
            hintLabel.Dock = DockStyle.Fill;
            hintLabel.TextAlign = ContentAlignment.MiddleLeft;
            hintLabel.Padding = new Padding(10, 0, 0, 0);
            hintLabel.ForeColor = Color.FromArgb(0, 100, 180);
            hintLabel.Text = "正在启动浏览器……";
            hintWrap.Controls.Add(hintLabel);

            logBox = new RichTextBox();
            logBox.Dock = DockStyle.Fill;
            logBox.ReadOnly = true;
            logBox.BackColor = Color.FromArgb(18, 18, 18);
            logBox.ForeColor = Color.Gainsboro;
            logBox.Font = new Font("Consolas", 9f);
            logBox.WordWrap = false;
            logBox.ScrollBars = RichTextBoxScrollBars.Both;
            logBox.HideSelection = false;

            StatusStrip status = new StatusStrip();
            statLabel = new ToolStripStatusLabel("准备中……");
            status.Items.Add(statLabel);

            Controls.Add(logBox);
            Controls.Add(hintWrap);
            Controls.Add(topWrap);
            Controls.Add(status);

            logBox.BringToFront();
            hintWrap.BringToFront();
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

        private void SetHint(string s)
        {
            if (InvokeRequired) { Invoke(new Action<string>(SetHint), s); return; }
            hintLabel.Text = s;
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

                AppendLog("微博批量删除工具");
                AppendLog("数据目录：" + dataDir);
                AppendLog("");

                string exe = Browser.FindBrowser();
                if (exe == null)
                {
                    AppendLog("找不到 Edge 或 Chrome。");
                    MessageBox.Show(
                        "系统里找不到 Microsoft Edge 或 Google Chrome。\n\n"
                        + "Windows 10/11 一般自带 Edge，如果没有请先装一个。",
                        "找不到浏览器", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                AppendLog("浏览器：" + exe);
                SetHint("正在启动浏览器……");

                int port = 9222;
                browser = new Browser();
                try
                {
                    await Task.Run(new Action(delegate { browser.Launch(profileDir, port); }));
                }
                catch (Exception ex)
                {
                    AppendLog("启动浏览器失败：" + ex.Message);
                    MessageBox.Show(ex.Message, "启动失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                AppendLog("正在打开微博……");
                SetHint("正在打开微博，请在弹出的浏览器窗口里登录……");
                await browser.OpenPageAsync("https://weibo.com");

                api = new Api(browser);
                logic = new Logic(api, log, deletedStore, skippedStore,
                                  AppendLog, UpdateStat, IsStop, GetDelay, GetJitter);

                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(500);
                    string href = await api.EvalAsync("location.href");
                    if (!string.IsNullOrEmpty(href) && href.IndexOf("weibo.com") >= 0)
                        break;
                }

                ready = true;
                AppendLog("页面加载完成。");
                AppendLog("历史进度：已删 " + deletedStore.Count + " 条，跳过 " + skippedStore.Count + " 条");
                AppendLog("");
                AppendLog("请在弹出的浏览器窗口里扫码登录，登录后再点上方按钮。");
                SetHint("请在浏览器窗口里扫码登录，登录后点上方按钮");
                UpdateStat(deletedStore.Count, skippedStore.Count);
            }
            catch (Exception ex)
            {
                AppendLog("初始化失败：" + ex.Message);
                MessageBox.Show("初始化失败：\n\n" + ex.Message,
                    "出错", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                    "还没登录。\n\n请在浏览器窗口里扫码登录微博，登录后再点按钮。",
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
                "会删掉账号里所有未删除的微博，删了就找不回来。\n\n"
                + "删完后会自动清缓存并做一次全面复核，确保没有遗漏。\n\n"
                + "确定继续吗？",
                "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;

            await RunDeleteAsync(0, true);
        }

        private async void OnTest3(object sender, EventArgs e)
        {
            if (busy) return;
            if (!await EnsureLoginAsync()) return;

            DialogResult r = MessageBox.Show(
                "会删掉 3 条微博，用来验证工具能用。\n\n确定继续吗？",
                "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            await RunDeleteAsync(3, false);
        }

        private async Task RunDeleteAsync(int maxCount, bool verify)
        {
            SetBusy(true);
            stopFlag = false;
            try
            {
                string uid = await GetUidAsync();

                AppendLog("");
                AppendLog("=== 第一轮：开始删除"
                          + (maxCount > 0 ? "（最多 " + maxCount + " 条）" : "") + " ===");
                await logic.RunAsync(uid, maxCount);

                if (!verify || stopFlag) return;

                AppendLog("");
                AppendLog("=== 清空浏览器缓存 ===");
                SetHint("正在清空缓存，准备复核……");
                try
                {
                    await browser.ClearCacheAsync();
                    AppendLog("缓存已清空。");
                }
                catch (Exception ex)
                {
                    AppendLog("清缓存失败（不影响复核）：" + ex.Message);
                }

                AppendLog("");
                AppendLog("=== 第二轮：全面复核，确认没有遗漏 ===");
                SetHint("正在复核，确认没有遗漏……");
                int before = deletedStore.Count;
                await logic.RunAsync(uid, 0);
                int after = deletedStore.Count;
                int extra = after - before;

                AppendLog("");
                if (extra <= 0)
                {
                    AppendLog("复核完成：没有发现遗漏，全部删干净了。");
                    SetHint("完成，全部删干净了");
                }
                else
                {
                    AppendLog("复核完成：又删掉了 " + extra + " 条漏网的。");
                    SetHint("完成，额外删掉 " + extra + " 条");
                }
                UpdateStat(deletedStore.Count, skippedStore.Count);
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
                "当前有 " + n + " 条跳过记录。\n\n"
                + "清空后，这些微博会在下次删除时重新被尝试。\n"
                + "真正删不掉的（原博已不可见）会再次被跳过。\n\n"
                + "确定清空吗？",
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
                "会关掉浏览器并清掉本机保存的登录状态，下次运行要重新扫码。\n\n确定吗？",
                "退出登录", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;

            try
            {
                if (browser != null) { browser.Dispose(); browser = null; }
                if (Directory.Exists(profileDir))
                    Directory.Delete(profileDir, true);
                AppendLog("已退出登录。请关闭程序后重新打开。");
                MessageBox.Show("已退出登录。请关闭程序再重新打开，会重新弹扫码。", "完成");
            }
            catch (Exception ex)
            {
                MessageBox.Show("清理失败：" + ex.Message + "\n\n可能是浏览器还开着，关掉浏览器再试。",
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
            try { if (browser != null) browser.Dispose(); } catch { }
        }
    }
}
