using System;
using System.Text.Json;

namespace ShruCloud
{
    /// <summary>
    /// The one exception this client throws for a failed call: a connection
    /// problem (status 0), a reply that is not JSON, an authentication refusal
    /// (401/403) or a normal API rejection (insufficient balance, ...).
    /// <see cref="Exception.Message"/> carries the server's own message whenever it sent one.
    /// </summary>
    public sealed class ShruException : Exception
    {
        /// <summary>Creates the exception.</summary>
        /// <param name="message">What went wrong.</param>
        /// <param name="status">The HTTP status the server answered with, or 0 when it was never reached.</param>
        /// <param name="response">The full decoded JSON reply, when there is one.</param>
        public ShruException(string message, int status = 0, JsonElement? response = null)
            : base(message)
        {
            Status = status;
            Response = response;
        }

        /// <summary>The HTTP status the server answered with, or 0 when it was never reached.</summary>
        public int Status { get; }

        /// <summary>The full decoded JSON reply, when there is one.</summary>
        public JsonElement? Response { get; }

        /// <summary>True for 401/403: a wrong Key ID or Secret, or a caller IP not on the key's allowlist.</summary>
        public bool IsAuthError => Status == 401 || Status == 403;
    }
}
