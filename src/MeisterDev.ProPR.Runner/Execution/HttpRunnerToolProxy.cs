// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Net;
using System.Net.Http.Json;
using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Runner.Execution;

/// <summary>
///     The six proxied review-context tools, over HTTP.
///     <para>
///         Everything here needs a credential the runner does not hold: source-control metadata, the
///         code-knowledge service, and the work-item provider. The other twelve tools read the local
///         working copy and never leave the host, which is what keeps a review from becoming network
///         traffic.
///     </para>
///     <para>
///         A refusal is returned, not thrown. The lease can be lost mid-review, and the pipeline's own
///         handling of an unavailable tool is better than an exception unwinding a half-finished file.
///     </para>
/// </summary>
public sealed class HttpRunnerToolProxy(HttpClient http) : IRunnerToolProxy
{
    /// <inheritdoc />
    public Task<RunnerToolResult<IReadOnlyList<ChangedFileSummary>>> GetChangedFilesAsync(
        RunnerCallContext call,
        CancellationToken ct = default)
    {
        return this.PostAsync<IReadOnlyList<ChangedFileSummary>>("tools/changed-files", call, null, ct);
    }

    /// <inheritdoc />
    public Task<RunnerToolResult<ProCursorKnowledgeAnswerDto>> AskKnowledgeAsync(
        RunnerCallContext call,
        string question,
        CancellationToken ct = default)
    {
        return this.PostAsync<ProCursorKnowledgeAnswerDto>("tools/knowledge", call, new { question }, ct);
    }

    /// <inheritdoc />
    public Task<RunnerToolResult<ProCursorSymbolInsightDto>> GetSymbolInsightAsync(
        RunnerCallContext call,
        string symbol,
        string? queryMode,
        int? maxRelations,
        CancellationToken ct = default)
    {
        return this.PostAsync<ProCursorSymbolInsightDto>(
            "tools/symbol-insight",
            call,
            new { symbol, queryMode, maxRelations },
            ct);
    }

    /// <inheritdoc />
    public Task<RunnerToolResult<LinkedItemDetails?>> GetLinkedItemDetailsAsync(
        RunnerCallContext call,
        string providerKey,
        CancellationToken ct = default)
    {
        return this.PostAsync<LinkedItemDetails?>("tools/linked-item-details", call, new { providerKey }, ct);
    }

    /// <inheritdoc />
    public Task<RunnerToolResult<IReadOnlyList<LinkedItemComment>>> GetLinkedItemDiscussionAsync(
        RunnerCallContext call,
        string providerKey,
        CancellationToken ct = default)
    {
        return this.PostAsync<IReadOnlyList<LinkedItemComment>>(
            "tools/linked-item-discussion",
            call,
            new { providerKey },
            ct);
    }

    /// <inheritdoc />
    public Task<RunnerToolResult<LinkedItem?>> ResolveLinkedItemAsync(
        RunnerCallContext call,
        string relatedTargetKey,
        CancellationToken ct = default)
    {
        return this.PostAsync<LinkedItem?>("tools/resolve-linked-item", call, new { relatedTargetKey }, ct);
    }

    /// <summary>
    ///     One shape for every proxied call: the lease identity the control plane authorizes against, plus
    ///     whatever that particular tool asks for.
    /// </summary>
    private async Task<RunnerToolResult<T>> PostAsync<T>(
        string path,
        RunnerCallContext call,
        object? arguments,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        var payload = new Dictionary<string, object?>
        {
            ["jobId"] = call.JobId,
            ["leaseGeneration"] = call.Generation,
            ["contractVersion"] = Contracts.RunnerContractVersion.Current,
        };

        foreach (var property in (arguments ?? new { }).GetType().GetProperties())
        {
            payload[char.ToLowerInvariant(property.Name[0]) + property.Name[1..]] = property.GetValue(arguments);
        }

        try
        {
            using var response = await http.PostAsJsonAsync(path, payload, ct);

            // A lost lease is the expected refusal, not an error: the control plane is telling this
            // executor it no longer owns the job, and the pipeline decides what to do about that.
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                return RunnerToolResult<T>.Refused(RunnerCallRefusal.NotTheLeaseHolder);
            }

            // A server error is a fault, never "not offered": only the control plane's own envelope may
            // say a tool is not part of this review's surface. A 502 during a rolling restart read as
            // not-offered told the reviewer the pull request changed no files, and it believed that.
            if (!response.IsSuccessStatusCode)
            {
                return RunnerToolResult<T>.Faulted($"the control plane answered HTTP {(int)response.StatusCode}");
            }

            var envelope = await response.Content.ReadFromJsonAsync<ToolEnvelope<T>>(ct);
            if (envelope is null)
            {
                return RunnerToolResult<T>.Faulted("the control plane's answer could not be read");
            }

            return envelope.Unavailable
                ? RunnerToolResult<T>.NotOffered()
                : RunnerToolResult<T>.Served(envelope.Value!);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Unreachable is a fault, reported rather than thrown here: the pipeline's tool invoker turns
            // it into a tool error the model sees and can retry, exactly as an in-process provider blip.
            return RunnerToolResult<T>.Faulted(ex.Message);
        }
    }

    /// <summary>The envelope the execution controller wraps every tool answer in.</summary>
    private sealed record ToolEnvelope<T>(bool Unavailable, T? Value);
}
