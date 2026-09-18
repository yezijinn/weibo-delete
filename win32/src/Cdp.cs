using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WeiboDelete
{
    /// <summary>
    /// 极简 Chrome DevTools Protocol 客户端。
    /// 通过 WebSocket 连到浏览器，发命令、收结果。
    /// </summary>
    public class Cdp : IDisposable
    {
        private ClientWebSocket ws;
        private int nextId = 1;
        private readonly Dictionary<int, TaskCompletionSource<string>> pending
            = new Dictionary<int, TaskCompletionSource<string>>();
        private readonly object gate = new object();
        private volatile bool closed = false;

        public async Task ConnectAsync(string wsUrl)
        {
            ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
            Task.Run(new Func<Task>(ReceiveLoop));
        }

        private async Task ReceiveLoop()
        {
            byte[] buf = new byte[65536];
            StringBuilder sb = new StringBuilder();
            while (!closed)
            {
                try
                {
                    if (ws.State != WebSocketState.Open) break;
                    sb.Length = 0;
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(new ArraySegment<byte>(buf),
                                                  CancellationToken.None);
                        if (r.MessageType == WebSocketMessageType.Close)
                        {
                            closed = true;
                            return;
                        }
                        sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                    } while (!r.EndOfMessage);

                    Dispatch(sb.ToString());
                }
                catch
                {
                    closed = true;
                    return;
                }
            }
        }

        private void Dispatch(string json)
        {
            long id = Json.Num(json, "id", -1);
            if (id < 0) return;

            TaskCompletionSource<string> tcs = null;
            lock (gate)
            {
                int key = (int)id;
                if (pending.ContainsKey(key))
                {
                    tcs = pending[key];
                    pending.Remove(key);
                }
            }
            if (tcs != null) tcs.TrySetResult(json);
        }

        public async Task<string> SendAsync(string method, string paramsJson)
        {
            if (closed) throw new Exception("浏览器连接已断开");

            int id;
            TaskCompletionSource<string> tcs;
            lock (gate)
            {
                id = nextId++;
                tcs = new TaskCompletionSource<string>();
                pending[id] = tcs;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"id\":").Append(id);
            sb.Append(",\"method\":\"").Append(method).Append("\"");
            if (!string.IsNullOrEmpty(paramsJson))
                sb.Append(",\"params\":").Append(paramsJson);
            sb.Append("}");

            byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
            await ws.SendAsync(new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text, true, CancellationToken.None);

            Task done = await Task.WhenAny(tcs.Task, Task.Delay(60000));
            if (done != tcs.Task)
                throw new TimeoutException("CDP 命令超时：" + method);
            return tcs.Task.Result;
        }

        public bool Alive
        {
            get { return !closed && ws != null && ws.State == WebSocketState.Open; }
        }

        public void Dispose()
        {
            closed = true;
            try { if (ws != null) ws.Dispose(); } catch { }
        }
    }
}
