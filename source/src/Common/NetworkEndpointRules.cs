using System;
using System.Linq;

namespace StrmAssistantRebuild.Common;

/// <summary>Pure discovery input validation; never resolves a host or modifies server settings.</summary>
public static class NetworkEndpointRules
{
    public static bool TryNormalizeUrl(string? raw, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var text = raw.Trim();
        if (text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || text.IndexOfAny(new[] { '?', '#', '\\' }) >= 0 ||
            !Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host) || uri.Port < 1 || uri.Port > 65535 || !string.IsNullOrEmpty(uri.UserInfo))
        { error = "Local discovery requires an absolute HTTP(S) URL without credentials, query or fragment and with a valid port."; return false; }
        var authorityStart = text.IndexOf("://", StringComparison.Ordinal);
        var authorityEnd = authorityStart < 0 ? -1 : text.IndexOf('/', authorityStart + 3);
        var authority = authorityStart < 0 ? text : text.Substring(authorityStart + 3, (authorityEnd < 0 ? text.Length : authorityEnd) - authorityStart - 3);
        if (authorityStart < 0 || authority.Contains('@') || authority.EndsWith(":", StringComparison.Ordinal))
        { error = "Local discovery URL authority must be credentials-free with a valid port."; return false; }
        // Original GeneralSave uses UriPartial.Authority: a base path and trailing slash are not advertised.
        normalized = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}
