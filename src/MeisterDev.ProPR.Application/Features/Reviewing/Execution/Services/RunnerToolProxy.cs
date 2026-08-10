// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Serves the six review-context operations an executor cannot perform itself, using the control
///     plane's own credentials and provider adapters.
///     <para>
///         Each call is authorized against the caller's lease before anything else happens, and the result
///         is the same shape the in-process tool returns, so nothing downstream can tell which side
///         answered.
///     </para>
/// </summary>
public sealed class RunnerToolProxy(
    IRunnerCallAuthorizer authorizer,
    IRunnerJobToolsRegistry registry) : IRunnerToolProxy
{
    /// <inheritdoc />
    public Task<RunnerToolResult<IReadOnlyList<ChangedFileSummary>>> GetChangedFilesAsync(
        RunnerCallContext call,
        CancellationToken ct = default)
    {
        return this.ServeAsync(call, requiresCodeKnowledge: false, tools => tools.GetChangedFilesAsync(ct), ct);
    }

    /// <inheritdoc />
    public Task<RunnerToolResult<ProCursorKnowledgeAnswerDto>> AskKnowledgeAsync(
        RunnerCallContext call,
        string question,
        CancellationToken ct = default)
    {
        return this.ServeAsync(
            call,
            requiresCodeKnowledge: true,
            tools => tools.AskProCursorKnowledgeAsync(question, ct),
            ct);
    }

    /// <inheritdoc />
    public Task<RunnerToolResult<ProCursorSymbolInsightDto>> GetSymbolInsightAsync(
        RunnerCallContext call,
        string symbol,
        string? queryMode,
        int? maxRelations,
        CancellationToken ct = default)
    {
        return this.ServeAsync(
            call,
            requiresCodeKnowledge: true,
            tools => tools.GetProCursorSymbolInfoAsync(symbol, queryMode, maxRelations, ct),
            ct);
    }

    /// <inheritdoc />
    public Task<RunnerToolResult<LinkedItemDetails?>> GetLinkedItemDetailsAsync(
        RunnerCallContext call,
        string providerKey,
        CancellationToken ct = default)
    {
        return this.ServeAsync(
            call,
            requiresCodeKnowledge: false,
            tools => tools.GetLinkedItemDetailsAsync(providerKey, ct),
            ct);
    }

    /// <inheritdoc />
    public Task<RunnerToolResult<IReadOnlyList<LinkedItemComment>>> GetLinkedItemDiscussionAsync(
        RunnerCallContext call,
        string providerKey,
        CancellationToken ct = default)
    {
        return this.ServeAsync(
            call,
            requiresCodeKnowledge: false,
            tools => tools.GetLinkedItemDiscussionAsync(providerKey, ct),
            ct);
    }

    /// <inheritdoc />
    public Task<RunnerToolResult<LinkedItem?>> ResolveLinkedItemAsync(
        RunnerCallContext call,
        string relatedTargetKey,
        CancellationToken ct = default)
    {
        return this.ServeAsync(
            call,
            requiresCodeKnowledge: false,
            tools => tools.ResolveLinkedItemAsync(relatedTargetKey, ct),
            ct);
    }

    /// <summary>
    ///     The one path every proxied tool call takes: authorize, find the job's tools, then delegate.
    ///     Written once so no operation can be added that forgets the first two steps.
    /// </summary>
    private async Task<RunnerToolResult<T>> ServeAsync<T>(
        RunnerCallContext call,
        bool requiresCodeKnowledge,
        Func<IReviewContextTools, Task<T>> invoke,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        var authorization = await authorizer.AuthorizeAsync(call, ct);
        if (!authorization.IsAuthorized)
        {
            return RunnerToolResult<T>.Refused(authorization.Refusal);
        }

        var held = registry.Find(call.JobId);
        if (held is null)
        {
            // Authorized against the job, but this replica is not the one holding it open. To the caller
            // that is the same situation as losing the lease: stop, and let the lease machinery sort it out.
            return RunnerToolResult<T>.Refused(RunnerCallRefusal.JobNotExecuting);
        }

        if (requiresCodeKnowledge && !held.CodeKnowledgeOffered)
        {
            // Not an empty answer. The in-process path does not offer these tools at all on an installation
            // without code knowledge, and an executor told "nothing found" would record that as evidence.
            return RunnerToolResult<T>.NotOffered();
        }

        return RunnerToolResult<T>.Served(await invoke(held.Tools));
    }
}
