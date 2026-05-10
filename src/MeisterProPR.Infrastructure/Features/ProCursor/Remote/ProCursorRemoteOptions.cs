// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ComponentModel.DataAnnotations;

namespace MeisterProPR.Application.Options;

/// <summary>
///     Configuration for connecting ProPR to a separately hosted ProCursor service.
/// </summary>
public sealed class ProCursorRemoteOptions
{
    /// <summary>
    ///     Mode indicating that the remote ProCursor service is disabled.
    /// </summary>
    public const string DisabledMode = "disabled";

    /// <summary>
    ///     Mode indicating that the remote ProCursor service is managed by ProPR.
    /// </summary>
    public const string ProprManagedRemoteMode = "proprManagedRemote";

    /// <summary>
    ///     Reserved mode selector for future host bindings. When omitted, the presence of the base URL and
    ///     shared key enables the remote mode automatically.
    /// </summary>
    public string? Mode { get; set; }

    /// <summary>
    ///     Internal base URL for the extracted ProCursor service.
    /// </summary>
    [Url]
    public string? ServiceBaseUrl { get; set; }

    /// <summary>
    ///     Shared symmetric key passed in the <c>X-ProCursor-Key</c> header.
    /// </summary>
    public string? SharedKey { get; set; }

    /// <summary>
    ///     Timeout budget for ProPR -&gt; ProCursor requests.
    /// </summary>
    [Range(1, 600)]
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    ///     Health endpoint path exposed by the remote ProCursor service.
    /// </summary>
    public string HealthEndpointPath { get; set; } = "/healthz";

    /// <summary>
    ///     Returns <see langword="true" /> when the remote ProCursor path is configured.
    /// </summary>
    public bool IsRemoteEnabled
    {
        get
        {
            var effectiveMode = this.GetEffectiveMode();
            return string.Equals(effectiveMode, ProprManagedRemoteMode, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    ///     Returns the effective runtime mode after applying the implicit configuration fallback.
    /// </summary>
    public string GetEffectiveMode()
    {
        if (!string.IsNullOrWhiteSpace(this.Mode))
        {
            return this.Mode.Trim();
        }

        return string.IsNullOrWhiteSpace(this.ServiceBaseUrl) || string.IsNullOrWhiteSpace(this.SharedKey)
            ? DisabledMode
            : ProprManagedRemoteMode;
    }
}
