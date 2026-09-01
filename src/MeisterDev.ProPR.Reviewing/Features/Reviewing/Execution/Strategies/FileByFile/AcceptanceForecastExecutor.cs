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
///     Observe-only acceptance forecaster: after the finding gate has decided what publishes, one model call per
///     finding predicts whether this project's author would accept it, discuss it, or dismiss it. Per-finding
///     calls carry the complete finding message: a shared batched call had to truncate messages to fit, and most
///     finding messages exceeded the cut, so the forecaster judged amputated claims. The forecasts are recorded
///     as one protocol event per review and change nothing about publication. The point is calibration: forecasts
///     written to the protocol can be scored against the client's real dismissal stream before any forecast is
///     allowed to influence publication. The persona is the archetype template today; per-client derivation from
///     memory and dismissal history extends the same prompt without changing this executor.
/// </summary>
public sealed class AcceptanceForecastExecutor(IProtocolRecorder protocolRecorder)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     Forecasts acceptance for the findings the gate decided to publish and records one
    ///     <c>acceptance_forecast</c> protocol event carrying the per-finding verdicts. Degraded-safe: without a
    ///     resolvable judge runtime nothing is recorded; a call or parse failure for one finding skips that
    ///     finding and is counted in the event, and the review is unaffected either way.
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

            var system = PromptTemplateRuntime.RenderStage(
                PromptStageKeys.AcceptanceForecastSystem,
                new PromptTemplateModels.AcceptanceForecastModel());

            var forecasts = new List<AcceptanceForecast>();
            var failedCalls = 0;
            foreach (var finding in publishable)
            {
                var description = new StringBuilder()
                    .Append('[').Append(finding.Severity).Append("] ")
                    .Append(finding.FilePath ?? "PR").Append(':').Append(finding.LineNumber?.ToString() ?? "-")
                    .AppendLine().Append(finding.Message);

                try
                {
                    var response = await runtime.ChatClient.GetResponseAsync(
                        [
                            new ChatMessage(ChatRole.System, system),
                            new ChatMessage(ChatRole.User, $"Finding to forecast:\n{description}"),
                        ],
                        new ChatOptions { ModelId = runtime.Model.RemoteModelId },
                        ct).ConfigureAwait(false);

                    var forecast = ParseForecast(response.Text, finding.FindingId);
                    if (forecast is null)
                    {
                        failedCalls++;
                    }
                    else
                    {
                        forecasts.Add(forecast);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // One finding's failed call skips that forecast; the rest still record.
                    failedCalls++;
                }
            }

            await protocolRecorder.RecordVerificationEventAsync(
                protocolId.Value,
                ReviewProtocolEventNames.AcceptanceForecast,
                JsonSerializer.Serialize(
                    new { findingCount = publishable.Count, model = runtime.Model.RemoteModelId, failedCalls },
                    JsonOptions),
                forecasts.Count > 0 ? JsonSerializer.Serialize(forecasts, JsonOptions) : null,
                forecasts.Count > 0 ? null : $"no forecasts produced for {publishable.Count} findings",
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

    private static AcceptanceForecast? ParseForecast(string? text, string findingId)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var forecast = doc.RootElement.TryGetProperty("forecast", out var fEl) ? fEl.GetString() : null;
            if (forecast is not ("accept" or "discuss" or "dismiss"))
            {
                return null;
            }

            return new AcceptanceForecast(
                findingId,
                forecast,
                doc.RootElement.TryGetProperty("reason", out var rEl)
                    ? Truncate(rEl.GetString() ?? string.Empty, 300)
                    : null,
                doc.RootElement.TryGetProperty("expectedAuthorReply", out var aEl)
                    ? Truncate(aEl.GetString() ?? string.Empty, 300)
                    : null);
        }
        catch (JsonException)
        {
            // A malformed response records nothing for this finding; the review is unaffected.
            return null;
        }
    }

    private static string Truncate(string value, int max)
    {
        return value.Length <= max ? value : value[..max];
    }

    /// <summary>One per-finding acceptance forecast as recorded in the protocol event.</summary>
    /// <param name="FindingId">The finding the forecast concerns.</param>
    /// <param name="Forecast">accept, discuss, or dismiss.</param>
    /// <param name="Reason">Optional one-sentence rationale.</param>
    /// <param name="ExpectedAuthorReply">Optional one-sentence reply predicted in the author's voice.</param>
    public sealed record AcceptanceForecast(string FindingId, string Forecast, string? Reason, string? ExpectedAuthorReply);
}
