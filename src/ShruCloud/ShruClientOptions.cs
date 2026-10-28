namespace ShruCloud
{
    /// <summary>Optional settings for <see cref="ShruClient"/>.</summary>
    public sealed class ShruClientOptions
    {
        /// <summary>Gives up on a request after this many seconds (at least 1). Default 15.</summary>
        public double TimeoutSeconds { get; set; } = 15;

        /// <summary>Checks the server's TLS certificate. Default true. Turn off only for a local test server.</summary>
        public bool VerifyTls { get; set; } = true;

        /// <summary>Allows plain http://. Default false. Only for a local test server, never in production.</summary>
        public bool AllowInsecureHttp { get; set; }
    }
}
