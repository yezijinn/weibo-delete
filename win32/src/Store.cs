using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WeiboDelete
{
    public class Store
    {
        private readonly string path;
        private readonly HashSet<string> ids = new HashSet<string>();

        private Store(string path) { this.path = path; }

        public int Count { get { return ids.Count; } }

        public bool Contains(string id)
        {
            return id != null && ids.Contains(id);
        }

        public static Store Load(string path)
        {
            Store s = new Store(path);
            try
            {
                if (File.Exists(path))
                {
                    string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i].Trim();
                        if (line.Length == 0) continue;
                        string id = ExtractId(line);
                        if (id != null) s.ids.Add(id);
                    }
                }
            }
            catch { }
            return s;
        }

        private static string ExtractId(string json)
        {
            int i = json.IndexOf("\"id\"");
            if (i < 0) return null;
            int c = json.IndexOf(':', i);
            if (c < 0) return null;
            int q1 = json.IndexOf('"', c + 1);
            if (q1 < 0) return null;
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        public void Add(string id, string reason)
        {
            if (string.IsNullOrEmpty(id) || ids.Contains(id)) return;
            ids.Add(id);
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                StringBuilder sb = new StringBuilder();
                sb.Append("{\"id\":\"").Append(id).Append("\"");
                sb.Append(",\"ts\":\"").Append(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")).Append("\"");
                if (!string.IsNullOrEmpty(reason))
                    sb.Append(",\"reason\":\"").Append(Escape(reason)).Append("\"");
                sb.Append("}");
                File.AppendAllText(path, sb.ToString() + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        private static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\r", "").Replace("\n", " ");
        }

        public void Clear()
        {
            ids.Clear();
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
