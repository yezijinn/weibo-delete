using System;
using System.Windows.Forms;

namespace WeiboDelete
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show("程序启动失败：\n\n" + ex.Message,
                    "微博批量删除工具", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
