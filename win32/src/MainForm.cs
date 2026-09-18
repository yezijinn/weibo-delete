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
        private Button btnCount, btnAll, btnDate, btnTest, btnRetry, btnLogout, btnSetting, btnStop;
        private TextBox dateFrom, dateTo;
        private Settings settings;
        private Panel browserPanel;
        private Label browserHint;
        private System.Windows.Forms.Timer embedTimer;

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
            MinimumSize = new Size(900, 600);

            BuildUI();
            Load += OnLoaded;
            FormClosing += OnClosing;
        }

        private void BuildUI()
        {
            // ============ 左列：程序控件 ============

            // 按钮区（左上角，多行自动换行）
            FlowLayoutPanel top = new FlowLayoutPanel();
            top.Dock = DockStyle.Top;
            top.Height = 40;
            top.Padding = new Padding(6, 2, 6, 2);
            top.WrapContents = false;

            btnCount = MakeBtn("看看有多少条", OnCount);
            btnAll = MakeBtn("全部删除", OnDeleteAll);
            btnDate = MakeBtn("按日期删除", OnDeleteByDate);
            btnTest = MakeBtn("先删 3 条试试", OnTest3);
            btnRetry = MakeBtn("重试跳过的", OnRetrySkipped);
            btnLogout = MakeBtn("退出登录", OnLogout);
            btnSetting = MakeBtn("设置间隔", OnSetting);
            btnStop = MakeBtn("停止", OnStop);
            btnStop.Enabled = false;

            top.Controls.Add(btnCount);
            top.Controls.Add(btnAll);
            top.Controls.Add(btnDate);
            top.Controls.Add(btnTest);
            top.Controls.Add(btnRetry);
            top.Controls.Add(btnLogout);
            top.Controls.Add(btnSetting);

            // 用两个独立 FlowLayoutPanel 强制两行布局
            FlowLayoutPanel row2 = new FlowLayoutPanel();
            row2.Dock = DockStyle.Top;
            row2.Height = 40;
            row2.Padding = new Padding(6, 2, 6, 2);
            row2.WrapContents = false;

            row2.Controls.Add(btnStop);

            Label lblFrom = new Label();
            lblFrom.Text = "日期：";
            lblFrom.AutoSize = true;
            lblFrom.Padding = new Padding(20, 8, 0, 0);
            row2.Controls.Add(lblFrom);

            dateFrom = new TextBox();
            dateFrom.Width = 88;
            dateFrom.Margin = new Padding(3, 6, 3, 3);
            row2.Controls.Add(dateFrom);

            Label lblTo = new Label();
            lblTo.Text = "到";
            lblTo.AutoSize = true;
            lblTo.Padding = new Padding(0, 8, 0, 0);
            row2.Controls.Add(lblTo);

            dateTo = new TextBox();
            dateTo.Width = 88;
            dateTo.Margin = new Padding(3, 6, 3, 3);
            row2.Controls.Add(dateTo);

            Label lblHint = new Label();
            lblHint.Text = "（2020-01-01 格式，留空不限）";
            lblHint.AutoSize = true;
            lblHint.ForeColor = Color.Gray;
            lblHint.Padding = new Padding(6, 8, 0, 0);
            row2.Controls.Add(lblHint);

            // 提示条（按钮区下方）
            Panel hintWrap = new Panel();
            hintWrap.Dock = DockStyle.Top;
            hintWrap.Height = 36;

            hintLabel = new Label();
            hintLabel.Dock = DockStyle.Fill;
            hintLabel.TextAlign = ContentAlignment.MiddleLeft;
            hintLabel.Padding = new Padding(10, 0, 0, 0);
            hintLabel.ForeColor = Color.FromArgb(0, 100, 180);
            hintLabel.Text = "正在启动浏览器……";
            hintWrap.Controls.Add(hintLabel);

            // 日志
            logBox = new RichTextBox();
            logBox.Dock = DockStyle.Fill;
            logBox.ReadOnly = true;
            logBox.BackColor = Color.FromArgb(18, 18, 18);
            logBox.ForeColor = Color.Gainsboro;
            logBox.Font = new Font("Consolas", 9f);
            logBox.WordWrap = false;
            logBox.ScrollBars = RichTextBoxScrollBars.Both;
            logBox.HideSelection = false;

            // 状态栏
            StatusStrip status = new StatusStrip();
            statLabel = new ToolStripStatusLabel("准备中……");
            status.Items.Add(statLabel);

            // 左列容器
            Panel leftPanel = new Panel();
            leftPanel.Dock = DockStyle.Fill;
            leftPanel.BackColor = Color.FromArgb(240, 240, 240);

            // 添加顺序（倒序 Dock 计算）：先 Fill，再 Top/Bottom
            leftPanel.Controls.Add(logBox);     // Fill，占剩余
            leftPanel.Controls.Add(hintWrap);   // Top，提示条（按钮下方）
            leftPanel.Controls.Add(row2);       // Top，第二行（停止+日期）
            leftPanel.Controls.Add(top);        // Top，第一行（7 个按钮，最顶）
            leftPanel.Controls.Add(status);     // Bottom，状态栏

            // ============ 右列：浏览器 ============

            browserPanel = new Panel();
            browserPanel.Dock = DockStyle.Fill;
            browserPanel.BackColor = Color.FromArgb(245, 245, 245);

            browserHint = new Label();
            browserHint.Dock = DockStyle.Fill;
            browserHint.TextAlign = ContentAlignment.MiddleCenter;
            browserHint.ForeColor = Color.Gray;
            browserHint.Text = "正在启动浏览器……";
            browserPanel.Controls.Add(browserHint);

            // ============ 左右分栏 ============

            // 用 TableLayoutPanel 做左右 50/50，避开 SplitContainer 的各种尺寸约束
            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.ColumnCount = 2;
            grid.RowCount = 1;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            grid.Controls.Add(leftPanel, 0, 0);
            grid.Controls.Add(browserPanel, 1, 0);

            Controls.Add(grid);

            // 面板尺寸变化时，同步调整嵌入的浏览器窗口大小
            browserPanel.SizeChanged += OnBrowserPanelResize;

            // Chromium 会自己调整窗口位置，所以用定时器持续校正
            embedTimer = new System.Windows.Forms.Timer();
            embedTimer.Interval = 150;
            embedTimer.Tick += OnEmbedTick;
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

        private void OnEmbedTick(object sender, EventArgs e)
        {
            if (browser == null || browserPanel == null) return;
            if (browser.BrowserHwnd == IntPtr.Zero) return;
            if (browserPanel.IsDisposed || !browserPanel.IsHandleCreated) return;
            browser.ResizeEmbedded(browserPanel.ClientSize.Width,
                                   browserPanel.ClientSize.Height);
        }

        private void OnBrowserPanelResize(object sender, EventArgs e)
        {
            if (browser != null && browserPanel != null)
            {
                browser.ResizeEmbedded(browserPanel.ClientSize.Width,
                                       browserPanel.ClientSize.Height);
            }
        }

        private void SetBrowserHint(string s)
        {
            if (InvokeRequired) { Invoke(new Action<string>(SetBrowserHint), s); return; }
            browserHint.Text = s;
            browserHint.Visible = (browser == null || browser.BrowserHwnd == IntPtr.Zero);
        }

        private void HideBrowserHint()
        {
            if (InvokeRequired) { Invoke(new Action(HideBrowserHint)); return; }
            browserHint.Visible = false;
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
                settings = Settings.Load(dataDir);
                deletedStore = Store.Load(Path.Combine(dataDir, "deleted.jsonl"));
                skippedStore = Store.Load(Path.Combine(dataDir, "skipped.jsonl"));

                AppendLog("本程序完全开源，免费，公开，不收费。");
                AppendLog("作者主页 https://github.com/yezijinn");
                AppendLog("作者主页 https://gitee.com/yezijinn");
                AppendLog("");
                AppendLog("微博批量删除工具");
                AppendLog("数据目录：" + dataDir);
                AppendLog("");

                string exe = Browser.FindBrowser();
                if (exe == null)
                {
                    AppendLog("找不到内置浏览器。");
                    MessageBox.Show(
                        "程序目录下找不到内置浏览器。\n\n"
                        + "应该有 browser\\chrome-win64\\chrome.exe 这个文件。\n\n"
                        + "请确认解压完整，把整个文件夹解压出来，\n"
                        + "不要单独把 exe 拖出来运行。",
                        "找不到内置浏览器", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

                AppendLog("正在等待浏览器窗口……");
                SetBrowserHint("正在启动浏览器……");

                // 等浏览器主窗口出现，然后嵌进程序界面
                IntPtr hwnd = await Task.Run(new Func<IntPtr>(browser.FindBrowserWindow));
                if (hwnd == IntPtr.Zero)
                {
                    AppendLog("没找到浏览器窗口，可能启动失败了。");
                    MessageBox.Show("浏览器窗口没出现，请重试。", "启动失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                AppendLog("找到浏览器窗口句柄：" + hwnd.ToInt64());
                AppendLog("程序面板句柄：" + browserPanel.Handle.ToInt64());
                AppendLog("正在把浏览器嵌入界面……");
                // 藏掉地址栏和标签栏：把浏览器窗口向上偏移
                browser.EmbedOffsetY = 90;
                bool attached = browser.AttachTo(browserPanel.Handle,
                                                 browserPanel.ClientSize.Width,
                                                 browserPanel.ClientSize.Height);
                if (attached)
                {
                    HideBrowserHint();
                    AppendLog("浏览器已嵌入（已验证父窗口）。");
                    // 启动定时校正，防止 Chromium 把窗口撑大盖住按钮区
                    embedTimer.Start();
                }
                else
                {
                    AppendLog("嵌入失败，浏览器会单独显示。");
                    SetBrowserHint("浏览器在单独窗口里，请在那边操作");
                }

                AppendLog("正在连接浏览器……");
                SetHint("正在连接浏览器……");
                await browser.OpenPageAsync();

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
        private double GetDelay() { return settings != null ? settings.Delay : 2.0; }
        private double GetJitter() { return settings != null ? settings.Jitter : 1.5; }

        private void SetBusy(bool b)
        {
            busy = b;
            btnCount.Enabled = !b;
            btnAll.Enabled = !b;
            btnDate.Enabled = !b;
            btnTest.Enabled = !b;
            btnRetry.Enabled = !b;
            btnLogout.Enabled = !b;
            btnSetting.Enabled = !b;
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

        private DateTime? ParseDate(string s, bool isEnd)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            DateTime d;
            if (!DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out d))
                return null;
            if (isEnd) return d.Date.AddDays(1).AddSeconds(-1);
            return d.Date;
        }

        private async void OnDeleteByDate(object sender, EventArgs e)
        {
            if (busy) return;

            string s1 = dateFrom.Text.Trim();
            string s2 = dateTo.Text.Trim();

            if (s1.Length == 0 && s2.Length == 0)
            {
                MessageBox.Show(
                    "请先在日期框里填范围。\n\n格式：2020-01-01\n\n"
                    + "两个都填 = 删这个范围内\n"
                    + "只填开始 = 删这天之后的\n"
                    + "只填结束 = 删这天之前的",
                    "填写日期");
                return;
            }

            DateTime? d1 = ParseDate(s1, false);
            DateTime? d2 = ParseDate(s2, true);

            if (s1.Length > 0 && !d1.HasValue)
            {
                MessageBox.Show("开始日期格式不对，应该像 2020-01-01 这样。", "格式错误");
                return;
            }
            if (s2.Length > 0 && !d2.HasValue)
            {
                MessageBox.Show("结束日期格式不对，应该像 2020-12-31 这样。", "格式错误");
                return;
            }
            if (d1.HasValue && d2.HasValue && d1.Value > d2.Value)
            {
                MessageBox.Show("开始日期不能晚于结束日期。", "范围错误");
                return;
            }

            if (!await EnsureLoginAsync()) return;

            string desc = (d1.HasValue ? d1.Value.ToString("yyyy-MM-dd") : "不限")
                        + " ~ "
                        + (d2.HasValue ? d2.Value.ToString("yyyy-MM-dd") : "不限");
            DialogResult r = MessageBox.Show(
                "会删掉这个范围内的微博：\n\n" + desc + "\n\n"
                + "删了就找不回来。确定继续吗？",
                "确认按日期删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;

            await RunDeleteAsync(0, true, d1, d2);
        }

        private void OnSetting(object sender, EventArgs e)
        {
            if (busy) return;
            if (settings == null) return;

            using (Form f = new Form())
            {
                f.Text = "设置删除间隔";
                f.Width = 380;
                f.Height = 220;
                f.StartPosition = FormStartPosition.CenterParent;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false;
                f.MinimizeBox = false;

                Label l1 = new Label();
                l1.Text = "每条微博之间等多少秒？";
                l1.Left = 16; l1.Top = 16; l1.Width = 340;
                f.Controls.Add(l1);

                Label l2 = new Label();
                l2.Text = "默认 2 秒。太小容易被微博限速，建议 2~5 秒。";
                l2.Left = 16; l2.Top = 38; l2.Width = 340;
                l2.ForeColor = Color.Gray;
                f.Controls.Add(l2);

                TextBox tb = new TextBox();
                tb.Left = 16; tb.Top = 66; tb.Width = 120;
                tb.Text = settings.Delay.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                f.Controls.Add(tb);

                Label l3 = new Label();
                l3.Text = "秒（0.5 ~ 60）";
                l3.Left = 144; l3.Top = 69; l3.AutoSize = true;
                f.Controls.Add(l3);

                Button ok = new Button();
                ok.Text = "确定";
                ok.Left = 180; ok.Top = 120; ok.Width = 80;
                ok.DialogResult = DialogResult.OK;
                f.Controls.Add(ok);

                Button cancel = new Button();
                cancel.Text = "取消";
                cancel.Left = 270; cancel.Top = 120; cancel.Width = 80;
                cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(cancel);

                f.AcceptButton = ok;
                f.CancelButton = cancel;

                if (f.ShowDialog(this) != DialogResult.OK) return;

                double v;
                if (!double.TryParse(tb.Text.Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out v))
                {
                    MessageBox.Show("请输入数字，比如 2 或 3.5", "格式错误");
                    return;
                }
                if (v < 0.5 || v > 60)
                {
                    MessageBox.Show("范围是 0.5 ~ 60 秒。", "超出范围");
                    return;
                }

                settings.Delay = v;
                settings.Jitter = Math.Max(0.5, Math.Min(3.0, v * 0.75));
                settings.Save();
                AppendLog("删除间隔已设为 " + v + " 秒。");
                MessageBox.Show("已保存。\n\n每条等 " + v + " 秒。", "设置完成");
            }
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
            await RunDeleteAsync(maxCount, verify, null, null);
        }

        private async Task RunDeleteAsync(int maxCount, bool verify,
                                          DateTime? startDt, DateTime? endDt)
        {
            SetBusy(true);
            stopFlag = false;
            try
            {
                string uid = await GetUidAsync();

                AppendLog("");
                string rangeHint = "";
                if (startDt.HasValue || endDt.HasValue)
                {
                    rangeHint = "（"
                        + (startDt.HasValue ? startDt.Value.ToString("yyyy-MM-dd") : "-")
                        + " ~ "
                        + (endDt.HasValue ? endDt.Value.ToString("yyyy-MM-dd") : "-")
                        + "）";
                }
                AppendLog("=== 第一轮：开始删除"
                          + (maxCount > 0 ? "（最多 " + maxCount + " 条）" : "")
                          + rangeHint + " ===");
                await logic.RunAsync(uid, maxCount, startDt, endDt);

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
                await logic.RunAsync(uid, 0, startDt, endDt);
                int after = deletedStore.Count;
                int extra = after - before;

                AppendLog("");
                if (logic.LastRunAborted)
                {
                    // 复核中途出错（掉线、403、限速等），结果不可信
                    AppendLog("复核没有跑完：" + logic.LastAbortReason);
                    AppendLog("这次结果不作数，请检查后重新点「全部删除」。");
                    SetHint("复核未完成，请重试");
                }
                else if (extra <= 0)
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
            try { if (embedTimer != null) { embedTimer.Stop(); embedTimer.Dispose(); } } catch { }
            try { if (browser != null) browser.Dispose(); } catch { }
        }
    }
}
