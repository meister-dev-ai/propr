// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Runner.Execution;

/// <summary>
///     Routes a job's calls to the control-plane replica that granted its lease.
///     <para>
///         The workspace mirror is that replica's local disk and the budget, tool, and workspace
///         registries are that replica's process, so on a multi-replica installation every call a job
///         makes — the execution surface, the workspace fetch, the heartbeat, the release — has to reach
///         the replica named in the manifest. The configured control-plane URL stays what it is for
///         everything that is not job-scoped: enrollment, credential renewal, and asking for work.
///     </para>
/// </summary>
internal static class RunnerReplicaAffinity
{
    /// <summary>
    ///     Checks an advertised address against the same rule the host applies to its configured URL: the
    ///     credential rides on every call, so anything not loopback must be https. An empty address is
    ///     valid — it means the configured URL serves the job, which is the single-replica case.
    /// </summary>
    /// <param name="servedBy">The advertised base URL the manifest carries, when it carries one.</param>
    /// <param name="error">Why the address cannot be used, in words an operator can act on.</param>
    public static bool TryValidate(string? servedBy, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(servedBy))
        {
            return true;
        }

        // The scheme check is not redundant with the absolute-URI check: on Unix a bare path parses as an
        // absolute file:// URI with no host, which then counts as loopback and slips past the https rule.
        if (!Uri.TryCreate(servedBy, UriKind.Absolute, out var advertised)
            || (!string.Equals(advertised.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(advertised.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"the advertised replica address '{servedBy}' is not an absolute http or https URL";
            return false;
        }

        if (!advertised.IsLoopback && !string.Equals(advertised.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = $"the advertised replica address '{servedBy}' is not https, and the runner credential rides on every call to it";
            return false;
        }

        return true;
    }

    /// <summary>
    ///     The request URI for one job-scoped path: absolute against the granting replica when the manifest
    ///     names one, otherwise relative so the client's configured base address answers as before.
    /// </summary>
    /// <param name="servedBy">The advertised base URL, when the manifest carries one.</param>
    /// <param name="path">The path relative to the control plane's base.</param>
    public static Uri Resolve(string? servedBy, string path)
    {
        return string.IsNullOrWhiteSpace(servedBy)
            ? new Uri(path, UriKind.Relative)
            : new Uri(new Uri(servedBy.TrimEnd('/') + "/"), path);
    }

    /// <summary>
    ///     The same resolution for a caller with no base address of its own, so the answer is always
    ///     absolute: the granting replica when named, the configured control plane otherwise.
    /// </summary>
    /// <param name="servedBy">The advertised base URL, when the manifest carries one.</param>
    /// <param name="configuredControlPlaneUrl">The runner's configured control-plane URL.</param>
    /// <param name="path">The path relative to the control plane's base.</param>
    public static Uri ResolveAbsolute(string? servedBy, string configuredControlPlaneUrl, string path)
    {
        var baseUrl = string.IsNullOrWhiteSpace(servedBy) ? configuredControlPlaneUrl : servedBy;
        return new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path);
    }
}
