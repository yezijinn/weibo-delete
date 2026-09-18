using System;
using System.Text;
using System.Threading.Tasks;

namespace WeiboDelete
{
    /// <summary>通过 CDP 在真实浏览器里发 fetch 请求。</summary>
    public class Api
    {
        private readonly Browser browser;

        public Api(Browser browser) { this.browser = browser; }

        public async Task<string> EvalAsync(string jsExpr)
        {
            return await browser.EvalAsync(jsExpr);
        }

        public async Task<string> FetchAsync(string url, string method,
                                             string form, string jsonBody,
                                             string extraHeadersJson)
        {
            StringBuilder js = new StringBuilder();
            js.Append("(async()=>{");
            js.Append("const o={method:").Append(Json.JsStr(method == null ? "GET" : method)).Append(",");
            js.Append("credentials:'include',");
            js.Append("headers:Object.assign({'Accept':'application/json, text/plain, */*'},");
            js.Append(extraHeadersJson == null ? "{}" : extraHeadersJson).Append(")};");

            if (form != null)
            {
                js.Append("o.headers['Content-Type']='application/x-www-form-urlencoded';");
                js.Append("o.body=").Append(Json.JsStr(form)).Append(";");
            }
            else if (jsonBody != null)
            {
                js.Append("o.headers['Content-Type']='application/json;charset=UTF-8';");
                js.Append("o.body=").Append(Json.JsStr(jsonBody)).Append(";");
            }

            js.Append("const m=document.cookie.match(/(?:^|;\\s*)XSRF-TOKEN=([^;]+)/);");
            js.Append("if(m){try{o.headers['x-xsrf-token']=decodeURIComponent(m[1]);}");
            js.Append("catch(e){o.headers['x-xsrf-token']=m[1];}}");

            js.Append("let r;try{r=await fetch(").Append(Json.JsStr(url)).Append(",o);}");
            js.Append("catch(e){return JSON.stringify({status:0,text:'fetch error: '+e});}");

            js.Append("const t=await r.text();");
            js.Append("return JSON.stringify({status:r.status,text:t});");
            js.Append("})()");

            return await browser.EvalAsync(js.ToString());
        }
    }
}
