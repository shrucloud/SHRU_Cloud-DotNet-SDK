using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ShruCloud.Tests
{
    internal sealed record Received(string Path, string Body, NameValueCollection Headers);

    internal sealed record Answer(int Status, string Body, string ContentType = "application/json", string? Location = null, int DelayMs = 0, bool Chunked = false)
    {
        public static Answer Json(int status, string body) => new Answer(status, body);

        public static Answer Html(int status, string body) => new Answer(status, body, "text/html");
    }

    /// <summary>
    /// A small HTTP server for the tests. <see cref="Shru"/> builds a SHRU endpoint that answers
    /// only at one path and checks every signature the way a platform does.
    /// </summary>
    internal sealed class TestServer : IDisposable
    {
        public const string Key = "hmac_0123456789abcdef";
        public const string Secret = "test-secret-0123456789";

        private readonly HttpListener _listener = new HttpListener();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly Func<Received, Answer> _handler;

        public TestServer(Func<Received, Answer> handler)
        {
            _handler = handler;
            var port = FreePort();
            Base = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            _listener.Prefixes.Add(Base + "/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public string Base { get; }

        public ConcurrentQueue<Received> Seen { get; } = new ConcurrentQueue<Received>();

        public static TestServer Shru(string path, Func<JsonElement, Answer> api) => new TestServer(received =>
        {
            if (received.Path != path)
            {
                return Answer.Html(404, "<h1>Not found</h1>");
            }
            var signable = "POST\n/api/shru\n" + received.Headers["X-Timestamp"] + "\n" + received.Body;
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
            var expected = string.Concat(hmac.ComputeHash(Encoding.UTF8.GetBytes(signable)).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            if (received.Headers["X-Key-Id"] != Key || received.Headers["X-Signature"] != expected)
            {
                return Answer.Json(401, """{"success":false,"message":"Invalid signature"}""");
            }
            using var request = JsonDocument.Parse(received.Body);
            return api(request.RootElement.Clone());
        });

        public static Answer StandardApi(JsonElement request)
        {
            switch (request.GetProperty("action").GetString())
            {
                case "account_info":
                    return Answer.Json(200, """{"success":true,"balance":12.5,"currency":"USD","email":"a@b.c"}""");
                case "sync":
                    return Answer.Json(200, """
                        {"success":true,"balance":12.5,"currency":"USD","services":[
                          {"id":7,"name":"Check","group":"Apple","type":"quantity","price":1.25,"delivery":"Instant",
                           "input_label":"IMEI","input_required":true,"quantity":{"min":1,"max":10},"bulk":false,
                           "fields":[{"label":"Username","key":"username","type":"input","required":true}]},
                          {"id":8,"name":"Info","group":"Apple","type":"fixed","price":0.5,"delivery":"1-24h",
                           "input_label":null,"input_required":false,"quantity":null,"bulk":true,"fields":[]}]}
                        """);
                case "submit_order":
                    return Answer.Json(200, """{"success":true,"order_id":99,"status":"queued"}""");
                case "check_status":
                    return Answer.Json(200, "{\"success\":true,\"order_id\":" + request.GetProperty("order_id").GetRawText() + ",\"status\":\"completed\",\"reply\":\"Done\"}");
                default:
                    return Answer.Json(400, """{"success":false,"message":"Unknown action"}""");
            }
        }

        public static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (ObjectDisposedException)
            {
            }
            _stop.Dispose();
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception e) when (e is HttpListenerException || e is ObjectDisposedException || e is InvalidOperationException)
                {
                    return;
                }
                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                string body;
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);
                }
                var received = new Received(context.Request.Url!.PathAndQuery, body, context.Request.Headers);
                Seen.Enqueue(received);
                var answer = _handler(received);
                if (answer.DelayMs > 0)
                {
                    await Task.Delay(answer.DelayMs, _stop.Token).ConfigureAwait(false);
                }
                var bytes = Encoding.UTF8.GetBytes(answer.Body);
                var response = context.Response;
                response.StatusCode = answer.Status;
                response.ContentType = answer.ContentType;
                if (answer.Location != null)
                {
                    response.RedirectLocation = answer.Location;
                }
                if (answer.Chunked)
                {
                    response.SendChunked = true;
                }
                else
                {
                    response.ContentLength64 = bytes.Length;
                }
                await response.OutputStream.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
                response.Close();
            }
            catch (Exception e) when (e is OperationCanceledException || e is HttpListenerException || e is IOException || e is ObjectDisposedException)
            {
            }
        }
    }
}
