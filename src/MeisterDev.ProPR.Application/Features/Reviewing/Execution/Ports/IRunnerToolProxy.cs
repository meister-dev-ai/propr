// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     The review-context operations an executor cannot perform itself, served by the control plane with
///     its own credentials.
///     <para>
///         Only six of the eighteen review-context tools are here. The rest read the working copy the
///         executor already holds, including every chatty one, so proxying them would turn the bulk of a
///         review into per-call network traffic for nothing. What is left needs either a source-control
///         credential or the code-knowledge service, and neither may reach an executor.
///     </para>
///     <para>
///         Results are the same shapes the in-process tools return, so the pipeline cannot tell which side
///         answered. Every call is authorized against the caller's lease first.
///     </para>
/// </summary>
public interface IRunnerToolProxy
{
    /// <summary>Source-control metadata: the files this review's revision changed.</summary>
    Task<RunnerToolResult<IReadOnlyList<ChangedFileSummary>>> GetChangedFilesAsync(
        RunnerCallContext call,
        CancellationToken ct = default);

    /// <summary>A repository-aware knowledge answer, through the existing code-knowledge gateway.</summary>
    Task<RunnerToolResult<ProCursorKnowledgeAnswerDto>> AskKnowledgeAsync(
        RunnerCallContext call,
        string question,
        CancellationToken ct = default);

    /// <summary>Symbol-aware insight, through the existing code-knowledge gateway.</summary>
    Task<RunnerToolResult<ProCursorSymbolInsightDto>> GetSymbolInsightAsync(
        RunnerCallContext call,
        string symbol,
        string? queryMode,
        int? maxRelations,
        CancellationToken ct = default);

    /// <summary>The structured fields of a work item linked to the review.</summary>
    Task<RunnerToolResult<LinkedItemDetails?>> GetLinkedItemDetailsAsync(
        RunnerCallContext call,
        string providerKey,
        CancellationToken ct = default);

    /// <summary>The discussion on a work item linked to the review, oldest first.</summary>
    Task<RunnerToolResult<IReadOnlyList<LinkedItemComment>>> GetLinkedItemDiscussionAsync(
        RunnerCallContext call,
        string providerKey,
        CancellationToken ct = default);

    /// <summary>Resolves a related link on a linked item into a full summary.</summary>
    Task<RunnerToolResult<LinkedItem?>> ResolveLinkedItemAsync(
        RunnerCallContext call,
        string relatedTargetKey,
        CancellationToken ct = default);
}
