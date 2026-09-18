using System;
using System.Collections.Generic;
using System.Text;

namespace WeiboDelete
{
    public static class Json
    {
        public static string Str(string json, string key)
        {
            if (json == null) return null;
            string pat = "\"" + key + "\"";
            int i = 0;
            while (true)
            {
                i = json.IndexOf(pat, i, StringComparison.Ordinal);
                if (i < 0) return null;
                int c = json.IndexOf(':', i + pat.Length);
                if (c < 0) return null;
                int p = c + 1;
                while (p < json.Length && (json[p] == ' ' || json[p] == '\t')) p++;
                if (p >= json.Length) return null;
                if (json[p] == '"') return ReadString(json, p);
                i = c + 1;
            }
        }

        public static string ReadString(string s, int p)
        {
            if (p >= s.Length || s[p] != '"') return null;
            StringBuilder sb = new StringBuilder();
            int i = p + 1;
            while (i < s.Length)
            {
                char ch = s[i];
                if (ch == '"') return sb.ToString();
                if (ch == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    i += 2;
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'u':
                            if (i + 4 <= s.Length)
                            {
                                string hex = s.Substring(i, 4);
                                int code;
                                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                                        System.Globalization.CultureInfo.InvariantCulture, out code))
                                {
                                    sb.Append((char)code);
                                    i += 4;
                                }
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                    continue;
                }
                sb.Append(ch);
                i++;
            }
            return sb.ToString();
        }

        public static long Num(string json, string key, long fallback)
        {
            if (json == null) return fallback;
            string pat = "\"" + key + "\"";
            int i = json.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return fallback;
            int c = json.IndexOf(':', i + pat.Length);
            if (c < 0) return fallback;
            int p = c + 1;
            while (p < json.Length && (json[p] == ' ' || json[p] == '\t')) p++;
            int start = p;
            if (p < json.Length && (json[p] == '-' || json[p] == '+')) p++;
            while (p < json.Length && char.IsDigit(json[p])) p++;
            if (p == start) return fallback;
            long v;
            if (long.TryParse(json.Substring(start, p - start), out v)) return v;
            return fallback;
        }

        public static string Arr(string json, string key)
        {
            if (json == null) return null;
            string pat = "\"" + key + "\"";
            int i = json.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return null;
            int c = json.IndexOf(':', i + pat.Length);
            if (c < 0) return null;
            int p = c + 1;
            while (p < json.Length && json[p] != '[') p++;
            if (p >= json.Length) return null;

            int depth = 0;
            bool inStr = false;
            for (int k = p; k < json.Length; k++)
            {
                char ch = json[k];
                if (inStr)
                {
                    if (ch == '\\') { k++; continue; }
                    if (ch == '"') inStr = false;
                    continue;
                }
                if (ch == '"') { inStr = true; continue; }
                if (ch == '[' || ch == '{') depth++;
                else if (ch == ']' || ch == '}')
                {
                    depth--;
                    if (depth == 0) return json.Substring(p + 1, k - p - 1);
                }
            }
            return null;
        }

        public static List<string> SplitObjects(string arr)
        {
            List<string> list = new List<string>();
            if (arr == null) return list;
            int i = 0;
            while (i < arr.Length)
            {
                while (i < arr.Length && arr[i] != '{') i++;
                if (i >= arr.Length) break;
                int start = i;
                int depth = 0;
                bool inStr = false;
                for (; i < arr.Length; i++)
                {
                    char ch = arr[i];
                    if (inStr)
                    {
                        if (ch == '\\') { i++; continue; }
                        if (ch == '"') inStr = false;
                        continue;
                    }
                    if (ch == '"') { inStr = true; continue; }
                    if (ch == '{') depth++;
                    else if (ch == '}')
                    {
                        depth--;
                        if (depth == 0) { i++; break; }
                    }
                }
                list.Add(arr.Substring(start, i - start));
            }
            return list;
        }

        public static bool Ok(string json)
        {
            if (json == null) return false;
            string v = Str(json, "ok");
            if (v == "1") return true;
            long n = Num(json, "ok", 0);
            return n == 1;
        }
    }
}
