using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.WinForms;

namespace WeiboDelete
{
    public class Api
    {
        private readonly WebView2 web;

        public Api(WebView2 web) { this.web = web; }

        public async Task<string> EvalAsync(string jsExpr)
        {
            string raw = await web.CoreWebView2.ExecuteScriptAsync(jsExpr);
            return Decode(raw);
        }

        private static string Decode(string raw)
        {
            if (raw == null) return "";
            if (raw == "null") return "";
            if (raw.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"')
                return Json.ReadString(raw, 0);
            return raw;
        }

        public async Task<string> FetchAsync(string url, string method,
                                             string form, string jsonBody,
                                             string extraHeadersJson)
        {
            StringBuilder js = new StringBuilder();
            js.Append("(async()=>{");
            js.Append("const o={method:").Append(JsStr(method == null ? "GET" : method)).Append(",");
            js.Append("credentials:'include',");
            js.Append("headers:Object.assign({'Accept':'application/json, text/plain, */*'},");
            js.Append(extraHeadersJson == null ? "{}" : extraHeadersJson).Append(")};");

            if (form != null)
            {
                js.Append("o.headers['Content-Type']='application/x-www-form-urlencoded';");
                js.Append("o.body=").Append(JsStr(form)).Append(";");
            }
            else if (jsonBody != null)
            {
                js.Append("o.headers['Content-Type']='application/json;charset=UTF-8';");
                js.Append("o.body=").Append(JsStr(jsonBody)).Append(";");
            }

            js.Append("const m=document.cookie.match(/(?:^|;\\s*)XSRF-TOKEN=([^;]+)/);");
            js.Append("if(m){try{o.headers['x-xsrf-token']=decodeURIComponent(m[1]);}");
            js.Append("catch(e){o.headers['x-xsrf-token']=m[1];}}");

            js.Append("let r;try{r=await fetch(").Append(JsStr(url)).Append(",o);}");
            js.Append("catch(e){return JSON.stringify({status:0,text:'fetch error: '+e});}");

            js.Append("const t=await r.text();");
            js.Append("return JSON.stringify({status:r.status,text:t});");
            js.Append("})()");

            string raw = await web.CoreWebView2.ExecuteScriptAsync(js.ToString());
            return Decode(raw);
        }

        public static string JsStr(string s)
        {
            if (s == null) return "''";
            StringBuilder sb = new StringBuilder("'");
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\'': sb.Append("\\'"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append("'");
            return sb.ToString();
        }
    }
}
