// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Serves proxied thread-memory reconsideration with the control plane's own store and embedder,
///     through the same service an in-process review calls directly.
///     <para>
///         The protocol id is deliberately not forwarded: the executor's ids are minted locally and mean
///         nothing to this side's recorder, so the executor records the memory trace in its own spool and
///         this side contributes only the memory activity log it already keeps.
///     </para>
/// </summary>
public sealed class RunnerMemoryProxy(
    IRunnerCallAuthorizer authorizer,
    IReviewJobExecutionStore jobs,
    IThreadMemoryService? memoryService = null,
    IClientRegistry? clients = null,
    IPromptOverrideService? promptOverrides = null) : IRunnerMemoryProxy
{
    /// <inheritdoc />
    public async Task<RunnerToolResult<ReviewResult>> ReconsiderAsync(
        RunnerCallContext call,
        string filePath,
        string? changeExcerpt,
        ReviewResult draft,
        float? temperature,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(draft);

        var authorization = await authorizer.AuthorizeAsync(call, ct);
        if (!authorization.IsAuthorized)
        {
            return RunnerToolResult<ReviewResult>.Refused(authorization.Refusal);
        }

        // An installation without a memory store (the offline harness) has nothing to reconsider against.
        // Answered as not-offered rather than an empty success, so the executor records the degradation
        // instead of believing memory ran and found nothing.
        if (memoryService is null)
        {
            return RunnerToolResult<ReviewResult>.NotOffered();
        }

        var job = jobs.GetById(call.JobId);
        if (job is null)
        {
            return RunnerToolResult<ReviewResult>.Refused(RunnerCallRefusal.JobNotExecuting);
        }

        var reconsidered = await memoryService.RetrieveAndReconsiderAsync(
            job.ClientId,
            job,
            filePath,
            changeExcerpt,
            draft,
            protocolId: null,
            ct,
            temperature,
            await this.BuildReconsiderationContextAsync(job.ClientId, ct));

        return RunnerToolResult<ReviewResult>.Served(reconsidered);
    }

    /// <summary>
    ///     The slice of the review context the reconsideration prompts actually read: the client's output
    ///     language, its custom system message, and its overrides for the memory stage.
    ///     <para>
    ///         Passed as null, the prompt rendered the built-in defaults in English. A German client's
    ///         remotely executed review came back with mixed-language findings, and an admin's override for
    ///         this stage was ignored without any record, neither of which the in-process path does.
    ///     </para>
    /// </summary>
    private async Task<ReviewSystemContext?> BuildReconsiderationContextAsync(Guid clientId, CancellationToken ct)
    {
        if (clients is null)
        {
            return null;
        }

        var outputLanguage = await clients.GetOutputLanguageAsync(clientId, ct);
        var customSystemMessage = await clients.GetCustomSystemMessageAsync(clientId, ct);

        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        if (promptOverrides is not null)
        {
            foreach (var key in PromptOverride.ValidPromptKeys.Where(key =>
                         key.StartsWith("MemoryReconsideration", StringComparison.Ordinal)))
            {
                var text = await promptOverrides.GetOverrideAsync(clientId, null, key, ct);
                if (text is not null)
                {
                    overrides[key] = text;
                }
            }
        }

        return new ReviewSystemContext(customSystemMessage, [], null)
        {
            PromptOverrides = overrides,
            OutputLanguage = outputLanguage,
        };
    }
}
