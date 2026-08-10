// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.ComponentModel.DataAnnotations;

namespace MeisterDev.ProPR.Runner;

/// <summary>
///     Everything the runner host is configured with, all of it from the environment.
///     <para>
///         Note what is not here: no database connection string, no source-control credential, no AI
///         provider key, no data-protection key ring. A runner is handed a manifest and a credential that
///         only lets it talk to the control plane about jobs it holds, and that is the whole point of the
///         host being separate. Adding a secret here would quietly undo it.
///     </para>
/// </summary>
public sealed class RunnerHostOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Runner";

    /// <summary>Base URL of the control plane this runner leases from.</summary>
    [Required(ErrorMessage = "RUNNER_CONTROL_PLANE_URL is required.")]
    public string ControlPlaneUrl { get; set; } = string.Empty;

    /// <summary>
    ///     The credential issued at enrollment. Absent on a host that has not enrolled yet, which is the
    ///     normal state on a first start with a registration token.
    /// </summary>
    public string? Credential { get; set; }

    /// <summary>
    ///     A single-use operator-issued token, used once to enroll and never again. Kept separate from the
    ///     credential so an operator can see at a glance whether a host is enrolling or already enrolled.
    /// </summary>
    public string? RegistrationToken { get; set; }

    /// <summary>Operator-facing name for this runner, shown in the registry.</summary>
    public string DisplayName { get; set; } = Environment.MachineName;

    /// <summary>
    ///     Tags this runner declares. They narrow which clients' work it is offered and can never widen the
    ///     scope the server stamped, so a mis-tagged runner is a routing mistake rather than a leak.
    /// </summary>
    public string Tags { get; set; } = string.Empty;

    /// <summary>
    ///     How many jobs this runner runs at once. It asks for a lease only when it has a free slot, which
    ///     is what lets the control plane dispatch without tracking anybody's capacity.
    /// </summary>
    [Range(1, 64, ErrorMessage = "RUNNER_CAPACITY must be between 1 and 64.")]
    public int Capacity { get; set; } = 2;

    /// <summary>How long to wait between asking for work when the last ask found none.</summary>
    [Range(1, 3600, ErrorMessage = "RUNNER_POLL_INTERVAL_SECONDS must be between 1 and 3600.")]
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>
    ///     Longest backoff between retries while the control plane is unreachable. A runner that cannot
    ///     reach its control plane keeps retrying rather than exiting, because a crash-looping container
    ///     tells an operator far less than a running one reporting that it cannot connect.
    /// </summary>
    [Range(5, 3600, ErrorMessage = "RUNNER_MAX_BACKOFF_SECONDS must be between 5 and 3600.")]
    public int MaxBackoffSeconds { get; set; } = 60;

    /// <summary>Where leased jobs are worked. Job-scoped content lives here and is purged when the job ends.</summary>
    public string WorkRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "propr-runner");

    /// <summary>The declared tags, parsed.</summary>
    public IReadOnlyList<string> DeclaredTags =>
        [.. this.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>The poll interval as a <see cref="TimeSpan" />.</summary>
    public TimeSpan PollInterval => TimeSpan.FromSeconds(this.PollIntervalSeconds);

    /// <summary>The backoff ceiling as a <see cref="TimeSpan" />.</summary>
    public TimeSpan MaxBackoff => TimeSpan.FromSeconds(this.MaxBackoffSeconds);
}
