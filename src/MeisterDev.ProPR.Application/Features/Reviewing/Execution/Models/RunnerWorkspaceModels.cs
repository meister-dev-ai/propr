// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>The mirror a job's content is served from, and what may be taken from it.</summary>
/// <param name="MirrorPath">Path to the bare mirror the control plane fetched with its own credentials.</param>
/// <param name="HeadSha">The head commit the executor materialises.</param>
/// <param name="BaseSha">The base commit it compares against.</param>
/// <param name="MaxTransferBytes">Ceiling on the transfer.</param>
public sealed record RunnerWorkspaceSource(
    string MirrorPath,
    string HeadSha,
    string BaseSha,
    long MaxTransferBytes);

/// <summary>Why a fetch was not authorized.</summary>
public enum RunnerWorkspaceRefusal
{
    /// <summary>The fetch is authorized.</summary>
    None = 0,

    /// <summary>The caller may not act on this job.</summary>
    NotAuthorized = 1,

    /// <summary>The control plane is not holding a mirror for this job.</summary>
    NoMirrorHeld = 2,

    /// <summary>
    ///     The content exceeds the configured ceiling. Refused before anything is sent: the point of a
    ///     ceiling is to stop an unbounded transfer, not to notice one afterwards.
    /// </summary>
    ExceedsSizeCeiling = 3,
}

/// <summary>What an executor may fetch, or why it may not.</summary>
/// <param name="Source">The mirror to fetch from, when authorized.</param>
/// <param name="Refusal">Why the fetch was refused.</param>
/// <param name="CallRefusal">Which authorization reason applied, when it was an authorization refusal.</param>
/// <param name="Reason">An operator-readable explanation, when refused.</param>
public sealed record RunnerWorkspaceGrant(
    RunnerWorkspaceSource? Source,
    RunnerWorkspaceRefusal Refusal,
    RunnerCallRefusal CallRefusal = RunnerCallRefusal.None,
    string? Reason = null)
{
    /// <summary>Whether the executor may fetch.</summary>
    public bool IsGranted => this.Refusal == RunnerWorkspaceRefusal.None && this.Source is not null;

    /// <summary>An authorized fetch.</summary>
    public static RunnerWorkspaceGrant Granted(RunnerWorkspaceSource source)
    {
        return new RunnerWorkspaceGrant(source, RunnerWorkspaceRefusal.None);
    }

    /// <summary>A fetch refused because the caller may not act on the job.</summary>
    public static RunnerWorkspaceGrant NotAuthorized(RunnerCallRefusal refusal)
    {
        return new RunnerWorkspaceGrant(null, RunnerWorkspaceRefusal.NotAuthorized, refusal);
    }

    /// <summary>A fetch refused because no mirror is held.</summary>
    public static RunnerWorkspaceGrant NoMirror()
    {
        return new RunnerWorkspaceGrant(
            null,
            RunnerWorkspaceRefusal.NoMirrorHeld,
            RunnerCallRefusal.None,
            "The control plane is not holding repository content for this job.");
    }

    /// <summary>A fetch refused because the content is larger than the ceiling allows.</summary>
    public static RunnerWorkspaceGrant TooLarge(long measuredBytes, long ceilingBytes)
    {
        return new RunnerWorkspaceGrant(
            null,
            RunnerWorkspaceRefusal.ExceedsSizeCeiling,
            RunnerCallRefusal.None,
            $"The repository content for this job is {measuredBytes / (1024 * 1024)} MiB, which exceeds the "
            + $"configured transfer ceiling of {ceilingBytes / (1024 * 1024)} MiB. Raise the ceiling or "
            + "narrow what this review covers.");
    }
}
