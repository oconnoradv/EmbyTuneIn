using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace MediaBrowser.Plugins.TuneIn
{
    internal static class TuneInCatalogDiagnostics
    {
        private static readonly string[] NavigationParameters = { "id", "c", "filter", "offset", "pivot" };
        private static readonly Regex Assignments = new Regex(@"(?<key>[A-Za-z][A-Za-z0-9_-]*)\s*=\s*[^&#;]*");
        private const int FieldLimit = 512;

        internal static string DescribeLink(string value)
        {
            if (!Uri.TryCreate(HttpUtility.HtmlDecode(value), UriKind.Absolute, out var uri)
                || !uri.Host.Equals("opml.radiotime.com", StringComparison.OrdinalIgnoreCase)
                || (uri.AbsolutePath != "/Browse.ashx" && uri.AbsolutePath != "/Tune.ashx")
                || !uri.IsDefaultPort || uri.UserInfo.Length != 0
                || (uri.Scheme != "https" && uri.Scheme != "http"))
                return "(trusted catalog link unavailable)";

            var result = new StringBuilder(uri.AbsolutePath);
            var query = HttpUtility.ParseQueryString(uri.Query);
            foreach (var key in NavigationParameters)
            {
                var values = query.GetValues(key);
                if (values == null) continue;
                result.Append("; ").Append(key).Append("[").Append(values.Length).Append("]=");
                result.Append(string.Join(", ", values.Take(4).Select(SafeValue)));
                if (values.Length > 4) result.Append(" [additional values omitted]");
            }
            if (query.AllKeys.Any(key => !NavigationParameters.Contains(key, StringComparer.OrdinalIgnoreCase)))
                result.Append("; non-navigation parameters omitted");
            result.Append("; fragment=").Append(SafeValue(uri.Fragment));
            return result.ToString();
        }

        private static string SafeValue(string value)
        {
            value = value ?? "";
            var length = value.Length;
            if (length > FieldLimit) value = value.Substring(0, FieldLimit);
            string Redact(string text) => Assignments.Replace(text, match =>
                match.Groups["key"].Value + "=[redacted]");
            var redacted = Redact(value);
            // Catch credentials embedded in percent-encoded query tails without retaining the original.
            var decoded = value;
            for (var pass = 0; pass < 2; pass++)
            {
                var next = HttpUtility.UrlDecode(decoded);
                if (next == decoded) break;
                decoded = next;
                if (Redact(decoded) != decoded)
                    redacted = Redact(decoded) + " [decoded for redaction]";
            }
            if (Regex.IsMatch(decoded, @"(?i)%(?:25)*3d"))
                redacted = "[encoded assignment omitted]";
            redacted = redacted.Replace("\\", "\\\\").Replace("\"", "\\\"");
            redacted = Regex.Replace(redacted, @"[\x00-\x1f\x7f]", match =>
                "\\u" + ((int)match.Value[0]).ToString("x4"));
            return "\"" + redacted + "\"" + (length > FieldLimit ? $" [truncated; {length} characters]" : "");
        }
    }
}
