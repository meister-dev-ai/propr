// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Runner.Contracts;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     What a manifest is resolved for: the leased job, and the scope frozen when it was dispatched.
/// </summary>
/// <param name="Job">The leased review job.</param>
/// <param name="Lease">The lease the manifest is issued under; its generation travels in the manifest.</param>
/// <param name="TargetBranch">The branch the review targets, which repository configuration is read from.</param>
/// <param name="ChangedPaths">
///     The changed-path scope, frozen at dispatch. The control plane fetches this with its own credentials
///     because the executor has none, and freezing it is also what stops a push mid-review changing what
///     the review is of.
/// </param>
/// <param name="WorkspaceFetchPath">Where the executor fetches repository content from.</param>
/// <param name="MaxWorkspaceTransferBytes">Ceiling on that transfer.</param>
/// <param name="Description">The review's description, when the author wrote one.</param>
/// <param name="ExistingThreads">
///     The conversation already on the review, read here because reading it needs a credential. The
///     reviewer uses it to avoid raising again what has already been answered.
/// </param>
/// <param name="Conversation">
///     The metadata-only pull request the description and threads above were read from, when dispatch
///     could fetch one. The resolver uses it to discover linked items, which needs the request object the
///     provider's link mechanism keys on.
/// </param>
public sealed record RunnerJobManifestRequest(
    ReviewJob Job,
    ReviewJobLease Lease,
    string TargetBranch,
    IReadOnlyList<string> ChangedPaths,
    string WorkspaceFetchPath,
    long MaxWorkspaceTransferBytes,
    string? Description = null,
    IReadOnlyList<PrCommentThread>? ExistingThreads = null,
    PullRequest? Conversation = null);

/// <summary>
///     The outcome of resolving a manifest: either a complete manifest, or a refusal explaining why the job
///     cannot be offered. There is deliberately no third state carrying a partly filled manifest.
/// </summary>
public sealed record RunnerJobManifestResolution
{
    private RunnerJobManifestResolution(RunnerJobManifest? manifest, string? refusal)
    {
        this.Manifest = manifest;
        this.Refusal = refusal;
    }

    /// <summary>The resolved manifest, or null when resolution failed.</summary>
    public RunnerJobManifest? Manifest { get; }

    /// <summary>An operator-readable explanation of why the job could not be offered, or null on success.</summary>
    public string? Refusal { get; }

    /// <summary>Whether a manifest was produced.</summary>
    public bool Succeeded => this.Manifest is not null;

    /// <summary>A successfully resolved manifest.</summary>
    public static RunnerJobManifestResolution Resolved(RunnerJobManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new RunnerJobManifestResolution(manifest, null);
    }

    /// <summary>
    ///     A refusal. The lease is not offered at all rather than offered with something missing, because an
    ///     executor cannot tell an absent value from a deliberately empty one.
    /// </summary>
    /// <param name="reason">Why the job could not be dispatched.</param>
    public static RunnerJobManifestResolution Refused(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new RunnerJobManifestResolution(null, reason);
    }
}
