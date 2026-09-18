using System;
using System.IO;
using System.Text;

namespace WeiboDelete
{
    /// <summary>参数配置：删除间隔。存在 data/settings.json。</summary>
    public class Settings
    {
        public double Delay = 2.0;
        public double Jitter = 1.5;

        private string path;

        public static Settings Load(string dataDir)
        {
            Settings s = new Settings();
            try
            {
                Directory.CreateDirectory(dataDir);
                s.path = Path.Combine(dataDir, "settings.json");
                if (File.Exists(s.path))
                {
                    string c = File.ReadAllText(s.path, Encoding.UTF8);
                    long d = Json.Num(c, "delay", 0);
                    if (d <= 0)
                    {
                        // 可能是小数，手工找
                        string dv = FindNum(c, "delay");
                        if (dv != null)
                        {
                            double dd;
                            if (double.TryParse(dv, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out dd))
                                s.Delay = dd;
                        }
                    }
                    else s.Delay = d;

                    string jv = FindNum(c, "jitter");
                    if (jv != null)
                    {
                        double jj;
                        if (double.TryParse(jv, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out jj))
                            s.Jitter = jj;
                    }
                }
            }
            catch { }
            return s;
        }

        private static string FindNum(string json, string key)
        {
            string pat = "\"" + key + "\"";
            int i = json.IndexOf(pat);
            if (i < 0) return null;
            int c = json.IndexOf(':', i + pat.Length);
            if (c < 0) return null;
            int p = c + 1;
            while (p < json.Length && (json[p] == ' ' || json[p] == '\t')) p++;
            int st = p;
            while (p < json.Length && (char.IsDigit(json[p]) || json[p] == '.' || json[p] == '-'))
                p++;
            if (p == st) return null;
            return json.Substring(st, p - st);
        }

        public void Save()
        {
            if (path == null) return;
            try
            {
                string c = "{"
                    + "\"delay\":" + Delay.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ",\"jitter\":" + Jitter.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "}";
                File.WriteAllText(path, c, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
