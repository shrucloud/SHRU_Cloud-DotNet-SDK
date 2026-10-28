using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ShruCloud.Tests
{
    public sealed class ClientTests
    {
        private static CancellationToken Token => TestContext.Current.CancellationToken;

        private static ShruClient Local(string baseUrl, double timeoutSeconds = 15) =>
            new ShruClient(baseUrl, TestServer.Key, TestServer.Secret, new ShruClientOptions { AllowInsecureHttp = true, TimeoutSeconds = timeoutSeconds });

        [Fact]
        public void TestsTheExpectedBuildOfTheLibrary()
        {
            var framework = typeof(ShruClient).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName;
#if SDK_NETSTANDARD
            Assert.Equal(".NETStandard,Version=v2.0", framework);
#else
            Assert.Equal(".NETCoreApp,Version=v8.0", framework);
#endif
        }

        [Fact]
        public void SignsExactlyLikeTheApiDocumentationTestVector()
        {
            const string secret = "9f2d6c1e8b4a7d3f5e0c2b9a6d8f1e4c7b3a5d9e2f6c8b1a4d7e0f3c6b9a2d5e8f1c4b7a0d3e6f9c2b5a8d1e4f7c0b3a6d9e2f5c8b1a4d7e0f3c6b9a2d5e8f1c";
            const string body = "{\"action\":\"submit_order\",\"service_id\":1042,\"input\":\"356789101234567\"}";
            Assert.Equal("e2bf80ed81ffaa94829122e0655d795c446609fadfb952c9025866d54f6b8fa5", ShruClient.Sign(secret, "1767225600", body));
        }

        [Fact]
        public void RefusesPlainHttpBadInputAndATimeoutBelowOneSecond()
        {
            Assert.Contains("plain HTTP", Assert.ThrowsAny<ArgumentException>(() => new ShruClient("http://reseller.example.com", TestServer.Key, TestServer.Secret)).Message);
            Assert.Contains("Unsupported URL scheme", Assert.ThrowsAny<ArgumentException>(() => new ShruClient("ftp://reseller.example.com", TestServer.Key, TestServer.Secret)).Message);
            Assert.Contains("required", Assert.ThrowsAny<ArgumentException>(() => new ShruClient("", TestServer.Key, TestServer.Secret)).Message);
            Assert.Contains("at least 1", Assert.ThrowsAny<ArgumentException>(() => new ShruClient("https://reseller.example.com", TestServer.Key, TestServer.Secret, new ShruClientOptions { TimeoutSeconds = 0 })).Message);
            Assert.Contains("control character", Assert.ThrowsAny<ArgumentException>(() => new ShruClient("https://reseller.example.com", "key\r\nX-Evil: 1", TestServer.Secret)).Message);
            using var client = new ShruClient("reseller.example.com", TestServer.Key, TestServer.Secret);
        }

        [Theory]
        [InlineData("reseller.example.com", "https://reseller.example.com/api/shru|https://reseller.example.com/")]
        [InlineData(" https://Reseller.Example.com/#top ", "https://reseller.example.com/api/shru|https://reseller.example.com/")]
        [InlineData("https://reseller.example.com/shop/", "https://reseller.example.com/api/shru|https://reseller.example.com/shop/|https://reseller.example.com/shop/api/shru")]
        [InlineData("https://reseller.example.com:8443/index.php?page=api", "https://reseller.example.com:8443/api/shru|https://reseller.example.com:8443/index.php?page=api|https://reseller.example.com:8443/index.php/api/shru")]
        [InlineData("https://reseller.example.com:443/api/shru/", "https://reseller.example.com/api/shru")]
        public void LooksForTheEndpointInTheDocumentedOrder(string baseUrl, string expected)
        {
            Assert.Equal(expected.Split('|'), ShruClient.EndpointCandidates(ShruClient.ParseBaseUrl(baseUrl)));
        }

        [Fact]
        public async Task TheFullApiShruAddressIsUsedAsGivenWithoutACheck()
        {
            using var client = new ShruClient("https://reseller.example.com/api/shru/", TestServer.Key, TestServer.Secret);
            Assert.Equal("https://reseller.example.com/api/shru", await client.GetEndpointAsync(Token));
        }

        [Fact]
        public async Task FindsTheEndpointFromTheDomainAloneAndCallsEveryAction()
        {
            using var api = TestServer.Shru("/api/shru", TestServer.StandardApi);
            using var client = Local(api.Base);

            var account = await client.GetAccountInfoAsync(Token);
            Assert.Equal(12.5m, account.Balance);
            Assert.Equal("USD", account.Currency);
            Assert.Equal("a@b.c", account.Email);
            Assert.True(account.Raw.GetProperty("success").GetBoolean());
            Assert.Equal(api.Base + "/api/shru", await client.GetEndpointAsync(Token));

            var catalog = await client.SyncCatalogAsync(Token);
            Assert.Equal(12.5m, catalog.Balance);
            Assert.Equal(2, catalog.Services.Count);
            var check = catalog.Services[0];
            Assert.Equal(7, check.Id);
            Assert.Equal("Check", check.Name);
            Assert.Equal("Apple", check.Group);
            Assert.Equal("quantity", check.Type);
            Assert.Equal(1.25m, check.Price);
            Assert.Equal("IMEI", check.InputLabel);
            Assert.True(check.InputRequired);
            Assert.Equal(1, check.Quantity!.Min);
            Assert.Equal(10, check.Quantity.Max);
            Assert.False(check.Bulk);
            Assert.Equal("username", Assert.Single(check.Fields).Key);
            Assert.True(check.Fields[0].Required);
            var info = catalog.Services[1];
            Assert.Null(info.InputLabel);
            Assert.Null(info.Quantity);
            Assert.True(info.Bulk);
            Assert.Empty(info.Fields);
            Assert.Equal(new[] { 7, 8 }, (await client.SyncServicesAsync(Token)).Select(s => s.Id));

            var order = await client.PlaceOrderAsync(7, "356789101234567", 2, new Dictionary<string, string> { ["username"] = "bob" }, Token);
            Assert.Equal(99, order.OrderId);
            Assert.Equal("queued", order.Status);
            var status = await client.CheckStatusAsync(99, Token);
            Assert.Equal(99, status.OrderId);
            Assert.Equal("completed", status.Status);
            Assert.Equal("Done", status.Reply);

            var sent = api.Seen.First(r => r.Body.Contains("submit_order"));
            Assert.Equal("{\"action\":\"submit_order\",\"service_id\":7,\"input\":\"356789101234567\",\"quantity\":2,\"fields\":{\"username\":\"bob\"}}", sent.Body);
            Assert.StartsWith("SHRU_Cloud-DotNet-SDK/" + ShruClient.Version + " (", sent.Headers["User-Agent"]);
            Assert.Equal("application/json", sent.Headers["Accept"]);
            Assert.Equal("application/json", sent.Headers["Content-Type"]);
        }

        [Fact]
        public async Task FindsAPlatformInstalledInASubfolder()
        {
            using var api = TestServer.Shru("/shop/api/shru", TestServer.StandardApi);
            using var client = Local(api.Base + "/shop");
            await client.GetAccountInfoAsync(Token);
            Assert.Equal(api.Base + "/shop/api/shru", await client.GetEndpointAsync(Token));
        }

        [Fact]
        public async Task ASHRUFormatRefusalProvesTheAddressAndBecomesAShruException()
        {
            using var api = TestServer.Shru("/api/shru", _ => Answer.Json(403, """{"success":false,"message":"Unauthorized access"}"""));
            using var client = Local(api.Base);
            var error = await Assert.ThrowsAsync<ShruException>(() => client.GetAccountInfoAsync(Token));
            Assert.Equal(403, error.Status);
            Assert.True(error.IsAuthError);
            Assert.Equal("Unauthorized access", error.Message);
            Assert.False(error.Response!.Value.GetProperty("success").GetBoolean());
            Assert.Equal(api.Base + "/api/shru", await client.GetEndpointAsync(Token));
        }

        [Fact]
        public async Task AWrongSecretIsAnAuthError()
        {
            using var api = TestServer.Shru("/api/shru", TestServer.StandardApi);
            using var client = new ShruClient(api.Base, TestServer.Key, "not-the-secret", new ShruClientOptions { AllowInsecureHttp = true });
            var error = await Assert.ThrowsAsync<ShruException>(() => client.GetAccountInfoAsync(Token));
            Assert.Equal(401, error.Status);
            Assert.True(error.IsAuthError);
            Assert.Equal("Invalid signature", error.Message);
        }

        [Fact]
        public async Task AnApiRejectionKeepsTheServerMessageAndIsNotAnAuthError()
        {
            using var api = TestServer.Shru("/api/shru", request => request.GetProperty("action").GetString() == "account_info"
                ? Answer.Json(200, """{"success":true,"balance":0,"currency":"USD","email":""}""")
                : Answer.Json(400, """{"success":false,"message":"Insufficient balance"}"""));
            using var client = Local(api.Base);
            var error = await Assert.ThrowsAsync<ShruException>(() => client.PlaceOrderAsync(7, "x", cancellationToken: Token));
            Assert.Equal("Insufficient balance", error.Message);
            Assert.Equal(400, error.Status);
            Assert.False(error.IsAuthError);
        }

        [Fact]
        public async Task AReplyThatIsNotJsonIsReportedWithItsHttpStatus()
        {
            using var server = new TestServer(_ => Answer.Html(502, "<html>Bad gateway</html>"));
            using var client = Local(server.Base + "/api/shru");
            var error = await Assert.ThrowsAsync<ShruException>(() => client.GetAccountInfoAsync(Token));
            Assert.Equal(502, error.Status);
            Assert.Contains("non-JSON", error.Message);
            Assert.Null(error.Response);
        }

        [Fact]
        public async Task NeverFollowsARedirect()
        {
            using var elsewhere = TestServer.Shru("/api/shru", TestServer.StandardApi);
            using var server = new TestServer(_ => new Answer(301, "", "text/html", elsewhere.Base + "/api/shru"));
            using var client = Local(server.Base + "/api/shru");
            var error = await Assert.ThrowsAsync<ShruException>(() => client.GetAccountInfoAsync(Token));
            Assert.Equal(301, error.Status);
            Assert.Empty(elsewhere.Seen);
        }

        [Fact]
        public async Task GivesUpAfterTheTimeout()
        {
            using var server = new TestServer(_ => new Answer(200, "{}", DelayMs: 10000));
            using var client = Local(server.Base + "/api/shru", timeoutSeconds: 1);
            var clock = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<ShruException>(() => client.GetAccountInfoAsync(Token));
            Assert.Equal(0, error.Status);
            Assert.Contains("no answer within 1 seconds", error.Message);
            Assert.True(clock.ElapsedMilliseconds < 3000);
        }

        [Fact]
        public async Task ACallersCancellationIsNotTurnedIntoAShruException()
        {
            using var server = new TestServer(_ => new Answer(200, "{}", DelayMs: 10000));
            using var client = Local(server.Base + "/api/shru");
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
            cancel.CancelAfter(200);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAccountInfoAsync(cancel.Token));
        }

        [Fact]
        public async Task AServerThatCannotBeReachedIsStatusZero()
        {
            using var client = Local("http://127.0.0.1:" + TestServer.FreePort() + "/api/shru");
            var error = await Assert.ThrowsAsync<ShruException>(() => client.GetAccountInfoAsync(Token));
            Assert.Equal(0, error.Status);
            Assert.StartsWith("Connection to SHRU Cloud failed: ", error.Message);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RefusesAReplyLargerThanTenMegabytes(bool chunked)
        {
            var huge = "{\"success\":true,\"pad\":\"" + new string('x', 11 * 1024 * 1024) + "\"}";
            using var server = new TestServer(_ => new Answer(200, huge, Chunked: chunked));
            using var client = Local(server.Base + "/api/shru");
            var error = await Assert.ThrowsAsync<ShruException>(() => client.GetAccountInfoAsync(Token));
            Assert.Equal(0, error.Status);
            Assert.Contains("larger than 10 MB", error.Message);
        }

        [Fact]
        public async Task CallsMadeTogetherLookForTheEndpointOnce()
        {
            using var api = TestServer.Shru("/api/shru", TestServer.StandardApi);
            using var client = Local(api.Base);
            await Task.WhenAll(client.GetAccountInfoAsync(Token), client.GetAccountInfoAsync(Token), client.SyncServicesAsync(Token));
            Assert.Equal(3, api.Seen.Count(r => r.Body == "{\"action\":\"account_info\"}"));
            Assert.Equal(4, api.Seen.Count);
        }

        [Fact]
        public async Task RequestSendsAnyActionAndReturnsTheWholeReply()
        {
            using var api = TestServer.Shru("/api/shru", request => request.GetProperty("action").GetString() == "custom"
                ? Answer.Json(200, """{"success":true,"echo":"ok"}""")
                : TestServer.StandardApi(request));
            using var client = Local(api.Base);
            var reply = await client.RequestAsync("custom", new JsonObject { ["page"] = 2, ["filter"] = "apple", ["deep"] = new JsonObject { ["on"] = true } }, Token);
            Assert.Equal("ok", reply.GetProperty("echo").GetString());
            Assert.Contains(api.Seen, r => r.Body == "{\"action\":\"custom\",\"page\":2,\"filter\":\"apple\",\"deep\":{\"on\":true}}");
        }

        [Fact]
        public void RejectsUnusableArgumentsBeforeSendingAnything()
        {
            using var client = new ShruClient("https://reseller.example.com/api/shru", TestServer.Key, TestServer.Secret);
            Assert.ThrowsAny<ArgumentException>(() => { _ = client.PlaceOrderAsync(0, "x", cancellationToken: Token); });
            Assert.ThrowsAny<ArgumentException>(() => { _ = client.PlaceOrderAsync(7, null!, cancellationToken: Token); });
            Assert.ThrowsAny<ArgumentException>(() => { _ = client.PlaceOrderAsync(7, "x", 0, cancellationToken: Token); });
            Assert.ThrowsAny<ArgumentException>(() => { _ = client.PlaceOrderAsync(7, "x", 1, new Dictionary<string, string> { ["k"] = null! }, Token); });
            Assert.ThrowsAny<ArgumentException>(() => { _ = client.CheckStatusAsync(0, Token); });
            Assert.ThrowsAny<ArgumentException>(() => { _ = client.RequestAsync("", cancellationToken: Token); });
            Assert.ThrowsAny<ArgumentException>(() => { _ = client.RequestAsync("sync", new JsonObject { ["action"] = "submit_order" }, Token); });
        }
    }
}
