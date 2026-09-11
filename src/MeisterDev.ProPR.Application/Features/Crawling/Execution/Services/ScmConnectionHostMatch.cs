// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;

/// <summary>
///     Matches a stored SCM connection to the host a pull request lives on.
/// </summary>
/// <remarks>
///     A connection's stored host base URL may carry a path, such as an Azure DevOps organization URL, while a
///     request carries an authority. Both sides are reduced to scheme, host and port before they are compared.
/// </remarks>
internal static class ScmConnectionHostMatch
{
    /// <summary>
    ///     Reduces a provider scope path to <c>scheme://host[:port]</c>, or returns <see langword="null" /> when
    ///     it names no host.
    /// </summary>
    public static string? ToAuthority(string? scopePath)
    {
        return Normalize(scopePath);
    }

    /// <summary>Whether a connection's stored host base URL names the same host as the observed one.</summary>
    /// <param name="connectionHostBaseUrl">The connection's stored host base URL.</param>
    /// <param name="observedHostUrl">
    ///     The host the pull request was observed on, as an absolute URL. A bare authority such as
    ///     <c>example.com:443</c> is not accepted: both sides go through the same reduction, and that reduction
    ///     needs a scheme.
    /// </param>
    /// <remarks>
    ///     Both sides are reduced the same way, so a connection has to state a URL to be matched. A value that
    ///     names no host matches nothing: there is no text comparison behind this, because a stored host that
    ///     carried no scheme could never have equalled the reduced authority it is compared against, and every
    ///     connection this has ever resolved stores a URL.
    /// </remarks>
    public static bool MatchesAuthority(string? connectionHostBaseUrl, string? observedHostUrl)
    {
        var connection = Normalize(connectionHostBaseUrl);
        var host = Normalize(observedHostUrl);

        return connection is not null
               && host is not null
               && string.Equals(connection, host, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Reduces a value to <c>scheme://host[:port]</c>, or <see langword="null" /> when it names no host.
    /// </summary>
    /// <remarks>
    ///     Built from the parsed components instead of <c>GetLeftPart(UriPartial.Authority)</c>, which keeps any
    ///     userinfo the value carried. A scope path written as <c>https://user:pw@host/org</c> would otherwise
    ///     put the credential into every comparison and into the log line that names the authority. Absolute
    ///     parsing alone is also not enough to establish a host: <c>file:///tmp/x</c> and <c>urn:isbn:1</c> both
    ///     parse and name none.
    /// </remarks>
    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed)
            || string.IsNullOrEmpty(parsed.Host))
        {
            return null;
        }

        var port = parsed.IsDefaultPort ? string.Empty : $":{parsed.Port}";

        // A terminal dot is the fully qualified form of the same DNS name, and Uri keeps it. Left in place it
        // would make example.com. and example.com two hosts.
        var host = parsed.Host.TrimEnd('.');
        return $"{parsed.Scheme}://{host}{port}";
    }
}
