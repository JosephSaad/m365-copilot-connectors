// ---------------------------------------------------------------------------
// LogScrubber.cs
// Last line of defence for text that reaches a log sink.
//
// The primary control is that the connector never logs item content, property
// values or secrets in the first place. This class exists for the text the
// connector does not author: exception messages from SqlClient, Azure.Identity
// and the like, which can carry connection strings or tokens.
// ---------------------------------------------------------------------------

namespace SqlConnector.Security.Logging
{
    using System.Text.RegularExpressions;

    /// <summary>
    /// Removes credential shaped text from strings on their way to a sink.
    /// </summary>
    public static class LogScrubber
    {
        /// <summary>Replacement written in place of redacted material.</summary>
        public const string Replacement = "[redacted]";

        private const RegexOptions Options =
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        // PEM encoded private keys, in case a certificate export ever reaches a message.
        private static readonly Regex PrivateKeyBlock = new Regex(
            @"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----",
            Options);

        // Bearer and JWT shaped tokens.
        private static readonly Regex JsonWebToken = new Regex(
            @"eyJ[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]*",
            Options);

        // Individual credential keywords in connection string or query string form.
        private static readonly Regex CredentialKeyword = new Regex(
            @"\b(password|pwd|user\s*id|uid|client[_\s]?secret|access[_\s]?token|api[_\s]?key|secret)\s*=\s*[^;,\s""]*",
            Options);

        // A run of two or more keyword=value pairs is a connection string, whatever
        // else it claims to be. Server and database are logged separately from the
        // parsed builder, so nothing of value is lost here.
        private static readonly Regex ConnectionStringRun = new Regex(
            @"(?:[A-Za-z][A-Za-z0-9 _\.]{1,40}=[^;]{0,200};\s*){2,}",
            Options);

        private static readonly Regex BearerHeader = new Regex(
            @"\bBearer\s+[A-Za-z0-9\-\._~\+/]+=*",
            Options);

        /// <summary>Returns the text with credential shaped material replaced.</summary>
        public static string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            string scrubbed = PrivateKeyBlock.Replace(text, "[private key " + Replacement + "]");
            scrubbed = JsonWebToken.Replace(scrubbed, "[token " + Replacement + "]");
            scrubbed = BearerHeader.Replace(scrubbed, "Bearer [token " + Replacement + "]");
            scrubbed = ConnectionStringRun.Replace(scrubbed, "[connection string " + Replacement + "] ");
            scrubbed = CredentialKeyword.Replace(scrubbed, m => KeywordOf(m.Value) + "=" + Replacement);

            return scrubbed;
        }

        /// <summary>True when scrubbing would change the text. Used to avoid needless allocation.</summary>
        public static bool NeedsScrubbing(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            return PrivateKeyBlock.IsMatch(text) ||
                   JsonWebToken.IsMatch(text) ||
                   BearerHeader.IsMatch(text) ||
                   ConnectionStringRun.IsMatch(text) ||
                   CredentialKeyword.IsMatch(text);
        }

        private static string KeywordOf(string match)
        {
            int equals = match.IndexOf('=');
            return equals <= 0 ? match : match.Substring(0, equals).TrimEnd();
        }
    }
}
