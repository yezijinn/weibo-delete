using System;
using System.IO;
using System.Text;

namespace WeiboDelete
{
    public class Logger
    {
        private readonly string path;
        private readonly object gate = new object();

        public Logger(string dataDir)
        {
            try
            {
                Directory.CreateDirectory(dataDir);
                path = Path.Combine(dataDir, "run.log");
            }
            catch { }
        }

        public void Info(string msg) { Write("INFO", msg); }
        public void Warn(string msg) { Write("WARN", msg); }
        public void Error(string msg) { Write("ERROR", msg); }

        private void Write(string level, string msg)
        {
            if (path == null) return;
            try
            {
                lock (gate)
                {
                    string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                                + "] [" + level + "] " + msg;
                    File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
