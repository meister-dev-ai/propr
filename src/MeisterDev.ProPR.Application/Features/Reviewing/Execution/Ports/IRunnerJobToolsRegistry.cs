// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     The review-context tools the control plane holds open for each leased job, so a proxied call can be
///     served without rebuilding them.
///     <para>
///         Building them is not cheap: it needs the pull request, its branches, and the frozen changed set,
///         all of which the control plane already gathered to dispatch the job. Doing that again on every
///         tool call would make the proxy cost more than the work it serves.
///     </para>
///     <para>
///         Entries live exactly as long as the lease. A job that ends without releasing its entry is a leak
///         of both memory and a credentialed object, so release is not optional.
///     </para>
/// </summary>
public interface IRunnerJobToolsRegistry
{
    /// <summary>Holds the tools for a leased job.</summary>
    /// <param name="jobId">The leased job.</param>
    /// <param name="tools">The tools built for it, using control-plane credentials.</param>
    /// <param name="codeKnowledgeOffered">
    ///     Whether this installation offers the code-knowledge tools at all. When it does not, the in-process
    ///     path omits them, and the proxied surface has to omit them the same way rather than answering with
    ///     an empty result that reads as "nothing found".
    /// </param>
    void Register(Guid jobId, IReviewContextTools tools, bool codeKnowledgeOffered);

    /// <summary>Returns the tools held for a job, or null when none are.</summary>
    RunnerJobTools? Find(Guid jobId);

    /// <summary>Drops the tools held for a job. Safe to call when none are held.</summary>
    void Release(Guid jobId);
}

/// <summary>The tools held for one leased job, and what this installation offers through them.</summary>
/// <param name="Tools">The review-context tools, built with control-plane credentials.</param>
/// <param name="CodeKnowledgeOffered">Whether the code-knowledge tools are part of this installation's surface.</param>
public sealed record RunnerJobTools(IReviewContextTools Tools, bool CodeKnowledgeOffered);
