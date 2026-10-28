using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ShruCloud
{
    /// <summary>
    /// Official .NET client for the SHRU Cloud API - the HMAC-signed protocol every
    /// SHRU Cloud platform (S-SERVER) speaks. Create one client and reuse it: it is
    /// safe to share between threads.
    /// </summary>
    /// <example>
    /// <code>
    /// using var client = new ShruClient("https://reseller.example.com", "YOUR_KEY_ID", "YOUR_SECRET");
    /// var services = await client.SyncServicesAsync();
    /// </code>
    /// </example>
    public sealed class ShruClient : IDisposable
    {
        /// <summary>This SDK's version.</summary>
        public const string Version = "1.0.0";

        private const string ApiPath = "/api/shru";
        private const int MaxResponseBytes = 10 * 1024 * 1024;
        private const string TooLarge = "the reply is larger than 10 MB";

        private static readonly byte[] ProbeBody = Encoding.UTF8.GetBytes("{\"action\":\"account_info\"}");
        private static readonly string UserAgent =
            "SHRU_Cloud-DotNet-SDK/" + Version + " (" + RuntimeInformation.FrameworkDescription.Trim() + ")";

        private readonly string _keyId;
        private readonly byte[] _key;
        private readonly TimeSpan _timeout;
        private readonly string _timeoutText;
        private readonly HttpClient _http;
        private readonly IReadOnlyList<string> _candidates;
        private readonly SemaphoreSlim _finding = new SemaphoreSlim(1, 1);
        private volatile string? _endpoint;

        /// <summary>Creates a client. Nothing is sent until the first call.</summary>
        /// <param name="baseUrl">
        /// The address the platform gave you, usually just its domain ("https://reseller.example.com",
        /// even "reseller.example.com"), the full endpoint (".../api/shru") or any page of the site.
        /// </param>
        /// <param name="keyId">Your Key ID.</param>
        /// <param name="secret">Your Secret. Store it like a database password.</param>
        /// <param name="options">Optional settings: time limit, TLS check, plain HTTP for a local test server.</param>
        /// <exception cref="ArgumentException">A value is missing or not usable, or the address is plain HTTP.</exception>
        public ShruClient(string baseUrl, string keyId, string secret, ShruClientOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrEmpty(keyId) || string.IsNullOrEmpty(secret))
            {
                throw new ArgumentException("baseUrl, keyId and secret are all required.");
            }
            options ??= new ShruClientOptions();
            if (double.IsNaN(options.TimeoutSeconds) || options.TimeoutSeconds < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "TimeoutSeconds must be at least 1.");
            }
            if (HasControlCharacter(keyId))
            {
                throw new ArgumentException("keyId must not contain a line break or other control character.", nameof(keyId));
            }
            var address = ParseBaseUrl(baseUrl);
            if (address.Scheme != Uri.UriSchemeHttps && !options.AllowInsecureHttp)
            {
                throw new ArgumentException(
                    "Refusing to connect to \"" + Origin(address) + "\" over plain HTTP - your Key ID, order details and every reply " +
                    "would travel unencrypted. Set AllowInsecureHttp only for a local test server, never in production.",
                    nameof(baseUrl));
            }

            _keyId = keyId;
            _key = Encoding.UTF8.GetBytes(secret);
            _timeout = TimeSpan.FromMilliseconds(Math.Min(options.TimeoutSeconds * 1000, int.MaxValue - 1));
            _timeoutText = options.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            _candidates = EndpointCandidates(address);
            if (_candidates.Count == 1)
            {
                _endpoint = _candidates[0];
            }
            _http = new HttpClient(CreateHandler(options.VerifyTls, _timeout), disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        /// <summary>
        /// The signature the server checks: lowercase hex HMAC-SHA256 of
        /// "POST\n/api/shru\n{timestamp}\n{raw JSON body}" with your Secret.
        /// </summary>
        /// <param name="secret">Your Secret.</param>
        /// <param name="timestamp">Unix time in seconds, exactly as sent in X-Timestamp.</param>
        /// <param name="body">The raw JSON request body, exactly as sent.</param>
        public static string Sign(string secret, string timestamp, string body)
        {
            if (secret == null) throw new ArgumentNullException(nameof(secret));
            if (timestamp == null) throw new ArgumentNullException(nameof(timestamp));
            if (body == null) throw new ArgumentNullException(nameof(body));
            return Sign(Encoding.UTF8.GetBytes(secret), timestamp, Encoding.UTF8.GetBytes(body));
        }

        /// <summary>
        /// The SHRU endpoint this client talks to. Found on first use by asking each candidate
        /// for account_info (read-only) until one answers as a SHRU Cloud endpoint - a success,
        /// or a refusal in SHRU's own format, which still proves the address. Kept for the
        /// client's lifetime once found. Orders are only ever sent to this one endpoint, never
        /// retried at another address.
        /// </summary>
        /// <param name="cancellationToken">Cancels the search.</param>
        public async Task<string> GetEndpointAsync(CancellationToken cancellationToken = default)
        {
            var known = _endpoint;
            if (known != null)
            {
                return known;
            }
            await _finding.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                known = _endpoint;
                if (known != null)
                {
                    return known;
                }
                foreach (var url in _candidates)
                {
                    var reply = await SendAsync(url, ProbeBody, cancellationToken).ConfigureAwait(false);
                    if (reply.Error == null && reply.Decoded is JsonElement decoded
                        && decoded.TryGetProperty("success", out var success)
                        && (success.ValueKind == JsonValueKind.True || success.ValueKind == JsonValueKind.False))
                    {
                        _endpoint = url;
                        return url;
                    }
                }
            }
            finally
            {
                _finding.Release();
            }
            return _candidates[0];
        }

        /// <summary>Your balance, currency and email: a light credentials check.</summary>
        /// <param name="cancellationToken">Cancels the call.</param>
        /// <exception cref="ShruException">The call failed.</exception>
        public async Task<AccountInfo> GetAccountInfoAsync(CancellationToken cancellationToken = default)
        {
            return new AccountInfo(await SendActionAsync("account_info", null, cancellationToken).ConfigureAwait(false));
        }

        /// <summary>The services you may order, priced as your account is billed.</summary>
        /// <param name="cancellationToken">Cancels the call.</param>
        /// <exception cref="ShruException">The call failed.</exception>
        public async Task<IReadOnlyList<Service>> SyncServicesAsync(CancellationToken cancellationToken = default)
        {
            return (await SyncCatalogAsync(cancellationToken).ConfigureAwait(false)).Services;
        }

        /// <summary>The same services plus your current balance, in one call.</summary>
        /// <param name="cancellationToken">Cancels the call.</param>
        /// <exception cref="ShruException">The call failed.</exception>
        public async Task<Catalog> SyncCatalogAsync(CancellationToken cancellationToken = default)
        {
            return new Catalog(await SendActionAsync("sync", null, cancellationToken).ConfigureAwait(false));
        }

        /// <summary>
        /// Places an order. Sending the exact same input, fields and quantity again while that
        /// order is still queued returns the same order instead of a second charge, so a call
        /// that timed out is safe to retry.
        /// </summary>
        /// <param name="serviceId">An <see cref="Service.Id"/> from <see cref="SyncServicesAsync"/>.</param>
        /// <param name="input">The service's primary input ("" when it takes none).</param>
        /// <param name="quantity">Only used by a "quantity" service.</param>
        /// <param name="fields">Extra field values, keyed by each field's <see cref="ServiceField.Key"/>.</param>
        /// <param name="cancellationToken">Cancels the call.</param>
        /// <exception cref="ArgumentException">An argument is not usable; nothing was sent.</exception>
        /// <exception cref="ShruException">The call failed.</exception>
        public Task<PlacedOrder> PlaceOrderAsync(
            int serviceId,
            string input,
            int quantity = 1,
            IReadOnlyDictionary<string, string>? fields = null,
            CancellationToken cancellationToken = default)
        {
            if (serviceId < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(serviceId), "serviceId must be a positive whole number.");
            }
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input), "input must be a string (use \"\" when the service takes none).");
            }
            if (quantity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(quantity), "quantity must be a positive whole number.");
            }
            if (fields != null)
            {
                foreach (var pair in fields)
                {
                    if (pair.Value == null)
                    {
                        throw new ArgumentException("fields must not contain a null value.", nameof(fields));
                    }
                }
            }
            return PlaceOrderCoreAsync(serviceId, input, quantity, fields, cancellationToken);
        }

        /// <summary>
        /// Status and (once finished) result of an order placed with this key. Status is one of:
        /// queued, processing, completed, rejected, refunded.
        /// </summary>
        /// <param name="orderId">An <see cref="PlacedOrder.OrderId"/>.</param>
        /// <param name="cancellationToken">Cancels the call.</param>
        /// <exception cref="ArgumentException">The order ID is not usable; nothing was sent.</exception>
        /// <exception cref="ShruException">The call failed.</exception>
        public Task<OrderStatus> CheckStatusAsync(long orderId, CancellationToken cancellationToken = default)
        {
            if (orderId < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(orderId), "orderId must be a positive whole number.");
            }
            return CheckStatusCoreAsync(orderId, cancellationToken);
        }

        /// <summary>
        /// Calls any action by name and returns the decoded reply ("success" included) - for an
        /// action a platform added after this SDK's release.
        /// </summary>
        /// <param name="action">The action name.</param>
        /// <param name="parameters">The action's parameters, sent next to "action".</param>
        /// <param name="cancellationToken">Cancels the call.</param>
        /// <exception cref="ArgumentException">An argument is not usable; nothing was sent.</exception>
        /// <exception cref="ShruException">The call failed.</exception>
        public Task<JsonElement> RequestAsync(string action, JsonObject? parameters = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(action))
            {
                throw new ArgumentException("action is required.", nameof(action));
            }
            if (parameters != null && parameters.ContainsKey("action"))
            {
                throw new ArgumentException("parameters must not contain \"action\"; pass it as the action argument.", nameof(parameters));
            }
            Action<Utf8JsonWriter>? writeParameters = null;
            if (parameters != null)
            {
                writeParameters = writer =>
                {
                    foreach (var pair in parameters)
                    {
                        writer.WritePropertyName(pair.Key);
                        if (pair.Value is null)
                        {
                            writer.WriteNullValue();
                        }
                        else
                        {
                            pair.Value.WriteTo(writer);
                        }
                    }
                };
            }
            return SendActionAsync(action, writeParameters, cancellationToken);
        }

        /// <summary>Closes the client's connections.</summary>
        public void Dispose()
        {
            _http.Dispose();
        }

        internal static Uri ParseBaseUrl(string baseUrl)
        {
            var trimmed = baseUrl.Trim();
            var withScheme = StartsWithScheme(trimmed) ? trimmed : "https://" + trimmed.TrimStart('/');
            if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri))
            {
                throw new ArgumentException("Could not determine a valid host from base URL: \"" + baseUrl + "\"", nameof(baseUrl));
            }
            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            {
                throw new ArgumentException("Unsupported URL scheme \"" + uri.Scheme + "\" in base URL: \"" + baseUrl + "\"", nameof(baseUrl));
            }
            if (string.IsNullOrEmpty(uri.Host))
            {
                throw new ArgumentException("Could not determine a valid host from base URL: \"" + baseUrl + "\"", nameof(baseUrl));
            }
            return uri;
        }

        internal static IReadOnlyList<string> EndpointCandidates(Uri uri)
        {
            var origin = Origin(uri);
            var query = uri.Query == "?" ? "" : uri.Query;
            var path = uri.AbsolutePath.TrimEnd('/');
            if (path == ApiPath && query.Length == 0)
            {
                return new[] { origin + ApiPath };
            }
            var asGiven = path.Length == 0 && query.Length == 0 ? origin + "/" : origin + uri.AbsolutePath + query;
            var found = new List<string>(3);
            foreach (var url in new[] { origin + ApiPath, asGiven, origin + path + ApiPath })
            {
                if (!found.Contains(url))
                {
                    found.Add(url);
                }
            }
            return found.AsReadOnly();
        }

        private async Task<PlacedOrder> PlaceOrderCoreAsync(
            int serviceId,
            string input,
            int quantity,
            IReadOnlyDictionary<string, string>? fields,
            CancellationToken cancellationToken)
        {
            var reply = await SendActionAsync("submit_order", writer =>
            {
                writer.WriteNumber("service_id", serviceId);
                writer.WriteString("input", input);
                writer.WriteNumber("quantity", quantity);
                writer.WriteStartObject("fields");
                if (fields != null)
                {
                    foreach (var pair in fields)
                    {
                        writer.WriteString(pair.Key, pair.Value);
                    }
                }
                writer.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);
            return new PlacedOrder(reply);
        }

        private async Task<OrderStatus> CheckStatusCoreAsync(long orderId, CancellationToken cancellationToken)
        {
            var reply = await SendActionAsync("check_status", writer => writer.WriteNumber("order_id", orderId), cancellationToken)
                .ConfigureAwait(false);
            return new OrderStatus(reply);
        }

        private async Task<JsonElement> SendActionAsync(string action, Action<Utf8JsonWriter>? writeParameters, CancellationToken cancellationToken)
        {
            var body = BuildBody(action, writeParameters);
            var endpoint = await GetEndpointAsync(cancellationToken).ConfigureAwait(false);
            var reply = await SendAsync(endpoint, body, cancellationToken).ConfigureAwait(false);
            if (reply.Error != null)
            {
                throw new ShruException("Connection to SHRU Cloud failed: " + reply.Error);
            }
            if (!(reply.Decoded is JsonElement decoded))
            {
                throw new ShruException("SHRU Cloud returned a non-JSON response (HTTP " + reply.Status.ToString(CultureInfo.InvariantCulture) + ")", reply.Status);
            }
            if (!decoded.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            {
                var message = decoded.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
                throw new ShruException(message is { Length: > 0 } ? message : "SHRU Cloud rejected the request", reply.Status, decoded);
            }
            return decoded;
        }

        private static byte[] BuildBody(string action, Action<Utf8JsonWriter>? writeParameters)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("action", action);
                writeParameters?.Invoke(writer);
                writer.WriteEndObject();
            }
            return stream.ToArray();
        }

        private async Task<Reply> SendAsync(string url, byte[] body, CancellationToken cancellationToken)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-Key-Id", _keyId);
            request.Headers.Add("X-Timestamp", timestamp);
            request.Headers.Add("X-Signature", Sign(_key, timestamp, body));

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeout);
            try
            {
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (response.Content.Headers.ContentLength > MaxResponseBytes)
                {
                    return Reply.Failed(TooLarge);
                }
                var raw = await ReadLimitedAsync(response.Content, deadline.Token).ConfigureAwait(false);
                return raw == null ? Reply.Failed(TooLarge) : Reply.Answered(status, ParseObject(raw));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Reply.Failed("no answer within " + _timeoutText + " seconds");
            }
            catch (HttpRequestException e)
            {
                return Reply.Failed(Describe(e));
            }
            catch (IOException e)
            {
                return Reply.Failed(Describe(e));
            }
        }

        private static async Task<byte[]?> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
        {
#if NET8_0_OR_GREATER
            using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
            using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (true)
            {
#if NET8_0_OR_GREATER
                var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
#else
                var read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false);
#endif
                if (read == 0)
                {
                    return buffer.ToArray();
                }
                if (buffer.Length + read > MaxResponseBytes)
                {
                    return null;
                }
                buffer.Write(chunk, 0, read);
            }
        }

        private static JsonElement? ParseObject(byte[] raw)
        {
            var start = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF ? 3 : 0;
            try
            {
                using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(raw, start, raw.Length - start));
                return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : (JsonElement?)null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static HttpMessageHandler CreateHandler(bool verifyTls, TimeSpan timeout)
        {
#if NET8_0_OR_GREATER
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = timeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };
            handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            if (!verifyTls)
            {
                handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }
            return handler;
#else
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                SslProtocols = SslProtocols.Tls12,
            };
            if (!verifyTls)
            {
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
            }
            return handler;
#endif
        }

        private static string Sign(byte[] key, string timestamp, byte[] body)
        {
            var prefix = Encoding.UTF8.GetBytes("POST\n" + ApiPath + "\n" + timestamp + "\n");
            var signable = new byte[prefix.Length + body.Length];
            Buffer.BlockCopy(prefix, 0, signable, 0, prefix.Length);
            Buffer.BlockCopy(body, 0, signable, prefix.Length, body.Length);
            using var hmac = new HMACSHA256(key);
            var hash = hmac.ComputeHash(signable);
            const string digits = "0123456789abcdef";
            var hex = new char[hash.Length * 2];
            for (var i = 0; i < hash.Length; i++)
            {
                hex[i * 2] = digits[hash[i] >> 4];
                hex[i * 2 + 1] = digits[hash[i] & 0xF];
            }
            return new string(hex);
        }

        private static string Origin(Uri uri) => uri.Scheme + "://" + uri.Authority;

        private static bool StartsWithScheme(string text)
        {
            var end = text.IndexOf("://", StringComparison.Ordinal);
            if (end < 1 || !IsAsciiLetter(text[0]))
            {
                return false;
            }
            for (var i = 1; i < end; i++)
            {
                var c = text[i];
                if (!IsAsciiLetter(c) && !(c >= '0' && c <= '9') && c != '+' && c != '.' && c != '-')
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

        private static bool HasControlCharacter(string text)
        {
            foreach (var c in text)
            {
                if (char.IsControl(c))
                {
                    return true;
                }
            }
            return false;
        }

        private static string Describe(Exception error)
        {
            var inner = error;
            while (inner.InnerException != null)
            {
                inner = inner.InnerException;
            }
            return inner.Message.Length > 0 ? inner.Message : error.GetType().Name;
        }

        private readonly struct Reply
        {
            private Reply(string? error, int status, JsonElement? decoded)
            {
                Error = error;
                Status = status;
                Decoded = decoded;
            }

            public string? Error { get; }

            public int Status { get; }

            public JsonElement? Decoded { get; }

            public static Reply Failed(string error) => new Reply(error, 0, null);

            public static Reply Answered(int status, JsonElement? decoded) => new Reply(null, status, decoded);
        }
    }
}
