using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TiaAutomation.Api
{
    /// <summary>
    /// 极简 HTTP 服务：HttpListener + 路由表 + JSON 体。
    /// 监听 127.0.0.1:{port}，仅本机访问，不做鉴权 / TLS。
    /// </summary>
    public class HttpServer
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly Router _router;
        private readonly int _port;
        private CancellationTokenSource _cts;

        public HttpServer(int port, Router router)
        {
            _port = port;
            _router = router;
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener.Start();
            Console.WriteLine($"[api] listening on http://127.0.0.1:{_port}/");
            Task.Run(() => AcceptLoop(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }

                _ = Task.Run(() => HandleRequest(ctx));
            }
        }

        private async Task HandleRequest(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            // 允许本机不同源（开发时）
            res.AppendHeader("Access-Control-Allow-Origin", "*");
            res.AppendHeader("Access-Control-Allow-Headers", "Content-Type");
            res.AppendHeader("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");

            try
            {
                if (req.HttpMethod == "OPTIONS")
                {
                    res.StatusCode = 204;
                    res.Close();
                    return;
                }

                var match = _router.Match(req.HttpMethod, req.Url.AbsolutePath);
                if (match == null)
                {
                    await JsonResponse(res, 404, new { error = "not_found", path = req.Url.AbsolutePath });
                    return;
                }

                var requestCtx = new RequestContext(req, res, match.PathParams);
                await match.Handler(requestCtx).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[api] handler error: " + ex);
                try
                {
                    await JsonResponse(res, 500, new { error = ex.GetType().Name, message = ex.Message });
                }
                catch { }
            }
            finally
            {
                try { res.Close(); } catch { }
            }
        }

        public static async Task JsonResponse(HttpListenerResponse res, int status, object body)
        {
            res.StatusCode = status;
            res.ContentType = "application/json; charset=utf-8";
            var json = Json.Serialize(body);
            var buf = Encoding.UTF8.GetBytes(json);
            res.ContentLength64 = buf.Length;
            await res.OutputStream.WriteAsync(buf, 0, buf.Length).ConfigureAwait(false);
        }
    }

    public class RequestContext
    {
        public HttpListenerRequest Request { get; }
        public HttpListenerResponse Response { get; }
        public Dictionary<string, string> PathParams { get; }

        public RequestContext(HttpListenerRequest req, HttpListenerResponse res, Dictionary<string, string> pathParams)
        {
            Request = req;
            Response = res;
            PathParams = pathParams ?? new Dictionary<string, string>();
        }

        public string ReadBodyText()
        {
            // 显式 UTF-8：HttpListener 在没有 charset 时返回 us-ascii，会截断中文
            using (var reader = new StreamReader(Request.InputStream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        public T ReadBodyJson<T>()
        {
            var text = ReadBodyText();
            if (string.IsNullOrWhiteSpace(text)) return default;
            return Json.Deserialize<T>(text);
        }

        public Task WriteJson(int status, object body) => HttpServer.JsonResponse(Response, status, body);
    }
}
