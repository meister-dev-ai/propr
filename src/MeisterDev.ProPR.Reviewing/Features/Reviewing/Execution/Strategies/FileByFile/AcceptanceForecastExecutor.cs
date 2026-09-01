// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     Observe-only acceptance forecaster: after the finding gate has decided what publishes, one bounded model
///     call predicts per finding whether this project's author would accept it, discuss it, or dismiss it. The
///     forecasts are recorded as a protocol event and change nothing about the review — publication, dispositions,
///     and comment text are identical with the executor on or off. The point is calibration: forecasts written to
///     the protocol can be scored against the client's real dismissal stream before any forecast is allowed to
///     influence publication. The persona is the archetype template today; per-client derivation from memory and
///     dismissal history extends the same prompt without changing this executor.
/// </summary>
public sealed class AcceptanceForecastExecutor(IProtocolRecorder protocolRecorder)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     Forecasts acceptance for the findings the gate decided to publish and records one
    ///     <c>acceptance_forecast</c> protocol event carrying the per-finding verdicts. Degraded-safe: without a
    ///     resolvable judge runtime, or when the call or its parse fails, nothing is recorded and the review is
    ///     unaffected.
    /// </summary>
    public async Task RecordForecastsAsync(
        ReviewJob job,
        IReadOnlyList<CandidateReviewFinding> candidateFindings,
        IReadOnlyList<FinalGateDecision> gateDecisions,
        IAiRuntimeResolver? aiRuntimeResolver,
        Guid? protocolId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(candidateFindings);
        ArgumentNullException.ThrowIfNull(gateDecisions);

        if (protocolId is null || aiRuntimeResolver is null)
        {
            return;
        }

        var publishable = new List<CandidateReviewFinding>();
        for (var i = 0; i < candidateFindings.Count && i < gateDecisions.Count; i++)
        {
            if (string.Equals(gateDecisions[i].Disposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal))
            {
                publishable.Add(candidateFindings[i]);
            }
        }

        if (publishable.Count == 0)
        {
            return;
        }

        try
        {
            var runtime = await aiRuntimeResolver
                .ResolveChatRuntimeAsync(job.ClientId, AiPurpose.ReviewVerification, ct)
                .ConfigureAwait(false);

            var list = new StringBuilder();
            for (var i = 0; i < publishable.Count; i++)
            {
                var f = publishable[i];
                list.Append(i + 1).Append(". [").Append(f.Severity).Append("] ")
                    .Append(f.FilePath ?? "PR").Append(':').Append(f.LineNumber?.ToString() ?? "-")
                    .Append(' ').AppendLine(Truncate(f.Message, 300));
            }

            var system = PromptTemplateRuntime.RenderStage(
                PromptStageKeys.AcceptanceForecastSystem,
                new PromptTemplateModels.AcceptanceForecastModel(publishable.Count));

            var response = await runtime.ChatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, system),
                    new ChatMessage(ChatRole.User, $"Findings to forecast:\n{list}"),
                ],
                new ChatOptions { ModelId = runtime.Model.RemoteModelId },
                ct).ConfigureAwait(false);

            var forecasts = ParseForecasts(response.Text, publishable);

            await protocolRecorder.RecordVerificationEventAsync(
                protocolId.Value,
                ReviewProtocolEventNames.AcceptanceForecast,
                JsonSerializer.Serialize(new { findingCount = publishable.Count, model = runtime.Model.RemoteModelId }, JsonOptions),
                forecasts.Count > 0 ? JsonSerializer.Serialize(forecasts, JsonOptions) : null,
                forecasts.Count > 0 ? null : $"no parseable forecasts in response: {Truncate(response.Text ?? string.Empty, 180)}",
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Observe-only: a failed forecast changes nothing about the review. The failure is still recorded
            // as the event's error so a missing forecast is diagnosable from the protocol.
            await protocolRecorder.RecordVerificationEventAsync(
                protocolId.Value,
                ReviewProtocolEventNames.AcceptanceForecast,
                JsonSerializer.Serialize(new { findingCount = publishable.Count }, JsonOptions),
                null,
                $"{ex.GetType().Name}: {Truncate(ex.Message, 220)}",
                ct).ConfigureAwait(false);
        }
    }

    private static List<AcceptanceForecast> ParseForecasts(string? text, IReadOnlyList<CandidateReviewFinding> publishable)
    {
        var results = new List<AcceptanceForecast>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return results;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return results;
        }

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("forecasts", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var item in arr.EnumerateArray())
            {
                if (!item.TryGetProperty("index", out var idxEl) || !idxEl.TryGetInt32(out var idx)
                                                                 || idx < 1 || idx > publishable.Count)
                {
                    continue;
                }

                var forecast = item.TryGetProperty("forecast", out var fEl) ? fEl.GetString() : null;
                if (forecast is not ("accept" or "discuss" or "dismiss"))
                {
                    continue;
                }

                results.Add(
                    new AcceptanceForecast(
                        publishable[idx - 1].FindingId,
                        forecast,
                        item.TryGetProperty("confidence", out var cEl) && cEl.TryGetInt32(out var c) ? c : null,
                        item.TryGetProperty("reason", out var rEl) ? Truncate(rEl.GetString() ?? string.Empty, 200) : null));
            }
        }
        catch (JsonException)
        {
            // A malformed response records nothing; the review is unaffected.
        }

        return results;
    }

    private static string Truncate(string value, int max)
    {
        return value.Length <= max ? value : value[..max];
    }

    /// <summary>One per-finding acceptance forecast as recorded in the protocol event.</summary>
    /// <param name="FindingId">The finding the forecast concerns.</param>
    /// <param name="Forecast">accept, discuss, or dismiss.</param>
    /// <param name="Confidence">Optional 0-100 confidence the judge reported.</param>
    /// <param name="Reason">Optional one-sentence rationale.</param>
    public sealed record AcceptanceForecast(string FindingId, string Forecast, int? Confidence, string? Reason);
}
