// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     Instruments for model calls, dimensioned by provider and model so spend and failure can be compared
///     across providers rather than only totalled.
/// </summary>
/// <remarks>
///     The meter deliberately reuses the application's existing name, so these instruments are exported by the
///     configuration already in place rather than needing a second registration that a deployment could miss.
///     Client and job identity stay off the metric tags — they belong on spans and logs, where high cardinality
///     costs nothing, instead of multiplying every time series by the number of clients.
/// </remarks>
public sealed class AiProviderMetrics : IDisposable
{
    private readonly Meter _meter;

    /// <summary>Creates the meter and its instruments.</summary>
    public AiProviderMetrics()
    {
        this._meter = new Meter("MeisterProPR", "1.0.0");
        this._calls = this._meter.CreateCounter<long>(
            "meisterpropr_ai_provider_calls_total",
            "calls",
            "Model calls attempted, by provider, model and outcome.");
        this._duration = this._meter.CreateHistogram<double>(
            "meisterpropr_ai_provider_call_duration_seconds",
            "s",
            "Duration of a single model call attempt, by provider and model.");
        this._tokens = this._meter.CreateCounter<long>(
            "meisterpropr_ai_provider_tokens_total",
            "tokens",
            "Tokens reported by the provider, by provider, model and token kind.");
        this._cost = this._meter.CreateCounter<double>(
            "meisterpropr_ai_provider_cost_usd_total",
            "usd",
            "Estimated USD cost of model calls, by provider and model.");
    }

    private readonly Counter<long> _calls;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _tokens;
    private readonly Counter<double> _cost;

    /// <summary>Records one completed attempt and how long it took.</summary>
    /// <param name="providerKind">Provider family the call was routed to.</param>
    /// <param name="modelId">Remote model id the call addressed.</param>
    /// <param name="outcome">How the attempt ended: <c>ok</c>, <c>error</c> or <c>cancelled</c>.</param>
    /// <param name="elapsedSeconds">Wall-clock duration of the attempt.</param>
    public void RecordCall(AiProviderKind providerKind, string modelId, string outcome, double elapsedSeconds)
    {
        var tags = new TagList
        {
            { "ai_provider", providerKind.ToString() },
            { "ai_model", modelId },
            { "outcome", outcome },
        };

        this._calls.Add(1, tags);
        this._duration.Record(elapsedSeconds, tags);
    }

    /// <summary>Records the token counts a provider reported for one call, split by kind.</summary>
    /// <param name="providerKind">Provider family the call was routed to.</param>
    /// <param name="modelId">Remote model id the call addressed.</param>
    /// <param name="kind">Token kind: <c>input</c>, <c>output</c>, <c>cached_input</c>, <c>cache_write</c> or <c>reasoning</c>.</param>
    /// <param name="count">The count reported; zero counts are skipped by the caller.</param>
    public void RecordTokens(AiProviderKind providerKind, string modelId, string kind, long count)
    {
        this._tokens.Add(
            count,
            new TagList
            {
                { "ai_provider", providerKind.ToString() },
                { "ai_model", modelId },
                { "token_kind", kind },
            });
    }

    /// <summary>Records the estimated USD cost of one call.</summary>
    /// <param name="providerKind">Provider family the call was routed to.</param>
    /// <param name="modelId">Remote model id the call addressed.</param>
    /// <param name="usd">The estimate; callers skip unpriced calls rather than recording zero.</param>
    public void RecordCost(AiProviderKind providerKind, string modelId, decimal usd)
    {
        this._cost.Add(
            (double)usd,
            new TagList
            {
                { "ai_provider", providerKind.ToString() },
                { "ai_model", modelId },
            });
    }

    /// <summary>Disposes the underlying meter.</summary>
    public void Dispose()
    {
        this._meter.Dispose();
    }
}
