using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace WeiboDelete
{
    /// <summary>
    /// 把外部进程的窗口"收养"到本程序的一个控件里显示。
    /// 用 SetParent，浏览器窗口就变成子窗口了。
    /// </summary>
    public static class Embed
    {
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumProc cb, IntPtr p);
        private delegate bool EnumProc(IntPtr h, IntPtr p);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr h);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr h, int idx);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr h, int idx, int val);

        [DllImport("user32.dll")]
        private static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr h, int cmd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr h);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr h, IntPtr after,
            int x, int y, int cx, int cy, uint flags);

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_CHILD = 0x40000000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_BORDER = 0x00800000;
        private const int WS_DLGFRAME = 0x00400000;
        private const int WS_EX_CLIENTEDGE = 0x00000200;
        private const int WS_EX_WINDOWEDGE = 0x00000100;
        private const int SW_SHOW = 5;

        /// <summary>找出属于指定进程的所有可见顶层窗口句柄。</summary>
        public static List<IntPtr> WindowsOf(int[] pids)
        {
            HashSet<uint> want = new HashSet<uint>();
            for (int i = 0; i < pids.Length; i++) want.Add((uint)pids[i]);

            List<IntPtr> found = new List<IntPtr>();
            EnumProc cb = delegate(IntPtr h, IntPtr p)
            {
                if (!IsWindowVisible(h)) return true;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!want.Contains(pid)) return true;

                // 认窗口类名，Chromium 的顶层窗口是 Chrome_WidgetWin_1
                StringBuilder sb = new StringBuilder(64);
                GetClassName(h, sb, sb.Capacity);
                string cls = sb.ToString();
                if (cls.StartsWith("Chrome_WidgetWin_1", StringComparison.Ordinal))
                    found.Add(h);
                return true;
            };
            EnumWindows(cb, IntPtr.Zero);
            return found;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int L, T, R, B; }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr h, out RECT r);

        public static void GetSize(IntPtr h, out int w, out int hh)
        {
            RECT r;
            if (GetWindowRect(h, out r)) { w = r.R - r.L; hh = r.B - r.T; }
            else { w = 0; hh = 0; }
        }

        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_SHOWWINDOW = 0x0040;

        /// <summary>把子窗口塞进父控件的区域里，去掉标题栏和边框。
        /// 返回 true 表示收养成功（GetParent 已确认）。</summary>
        public static bool Attach(IntPtr child, IntPtr parent, int w, int h)
        {
            if (child == IntPtr.Zero || parent == IntPtr.Zero) return false;

            // 去掉弹窗/标题栏样式，改成子窗口
            int style = GetWindowLong(child, GWL_STYLE);
            style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME);
            style |= WS_CHILD;
            SetWindowLong(child, GWL_STYLE, style);

            int ex = GetWindowLong(child, GWL_EXSTYLE);
            ex &= ~(WS_EX_CLIENTEDGE | WS_EX_WINDOWEDGE);
            SetWindowLong(child, GWL_EXSTYLE, ex);

            // 关键一步：刷新边框，否则样式改动不会生效
            SetWindowPos(child, IntPtr.Zero, 0, 0, w, h,
                         SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);

            SetParent(child, parent);

            // 再次刷新，确保位置和边框重新计算
            SetWindowPos(child, IntPtr.Zero, 0, 0, w, h,
                         SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);

            ShowWindow(child, SW_SHOW);

            // 验证是否真的收养成功
            IntPtr now = GetParent(child);
            return now == parent;
        }

        /// <summary>把子窗口向上偏移，让它的顶部（地址栏/工具栏）藏到父窗口外面。
        /// 父窗口会裁掉超出边界的内容。</summary>
        public static void SetOffset(IntPtr child, int x, int y, int w, int h)
        {
            if (child == IntPtr.Zero) return;
            SetWindowPos(child, IntPtr.Zero, x, y, w, h,
                         SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }

        /// <summary>随容器尺寸变化时同步。</summary>
        public static void Resize(IntPtr child, int w, int h)
        {
            if (child == IntPtr.Zero) return;
            MoveWindow(child, 0, 0, w, h, true);
        }

        /// <summary>解除收养，把窗口还给系统桌面。</summary>
        public static void Detach(IntPtr child)
        {
            if (child == IntPtr.Zero) return;
            try { SetParent(child, IntPtr.Zero); } catch { }
        }
    }
}
