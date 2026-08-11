// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Resilience;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Domain.Services;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     Emits one span, one log line and one set of measurements per model call, each carrying the provider, the
///     model and the profile the call was made against.
/// </summary>
/// <remarks>
///     <para>
///         Without the provider on the record, "which provider is this spend coming from" and "which provider is
///         failing" can only be answered by inference from model names — and two profiles reaching the same model
///         through different providers are then indistinguishable.
///     </para>
///     <para>
///         It sits inside the retry stage, so each attempt is measured separately rather than only the one that
///         eventually succeeded, and outside the budget stage, so a refusal is recorded against the attempt that
///         provoked it.
///     </para>
///     <para>
///         Sitting inside retry also means it sees failures the retry stage goes on to absorb. A throttled
///         attempt is one of those, so it is recorded as throttling rather than as a fault; reporting it as an
///         error would put a stack trace and an error span behind something the review recovered from.
///     </para>
/// </remarks>
/// <param name="innerClient">The client whose calls are measured.</param>
/// <param name="target">The profile, provider and model being called.</param>
/// <param name="pricing">Pricing for the model, so cost can be measured alongside tokens.</param>
/// <param name="metrics">Instruments the measurements are recorded on.</param>
/// <param name="logger">Optional logger for the per-call log line.</param>
/// <param name="clientId">Owning client, tagged on spans only.</param>
/// <param name="logicalModelName">The logical-model role the call was resolved under, when there was one.</param>
/// <param name="classifyFailure">
///     The driver's classification of a failure, used only to tell a throttle from a fault. Omitting it reports
///     every failure as a fault, which is what a caller with no driver to hand can honestly say.
/// </param>
public sealed partial class ProviderTelemetryChatClient(
    IChatClient innerClient,
    ProviderCallTarget target,
    ModelPricing pricing,
    AiProviderMetrics metrics,
    ILogger? logger = null,
    Guid? clientId = null,
    string? logicalModelName = null,
    Func<Exception, ProviderFailureVerdict>? classifyFailure = null) : DelegatingChatClient(innerClient)
{
    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = this.StartActivity();
        var started = Stopwatch.GetTimestamp();

        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            this.RecordSuccess(activity, started, response.Usage);
            return response;
        }
        catch (Exception exception)
        {
            this.RecordFailure(activity, started, exception);
            throw;
        }
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var activity = this.StartActivity();
        var started = Stopwatch.GetTimestamp();
        var enumerator = base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        UsageDetails? usage = null;

        try
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    update = enumerator.Current;
                }
                catch (Exception exception)
                {
                    this.RecordFailure(activity, started, exception);
                    throw;
                }

                foreach (var content in update.Contents)
                {
                    if (content is UsageContent usageContent)
                    {
                        usage = usageContent.Details;
                    }
                }

                yield return update;
            }

            this.RecordSuccess(activity, started, usage);
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static double ElapsedSeconds(long started)
    {
        return Stopwatch.GetElapsedTime(started).TotalSeconds;
    }

    private Activity? StartActivity()
    {
        var activity = ActivitySource.StartActivity("ai.provider.chat", ActivityKind.Client);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("ai_provider", target.ProviderKind.ToString());
        activity.SetTag("ai_model", target.ModelId);
        if (target.ProfileLabel is { Length: > 0 } label)
        {
            activity.SetTag("ai_profile", label);
        }

        if (clientId is { } id && id != Guid.Empty)
        {
            activity.SetTag("client_id", id.ToString("D"));
        }

        if (logicalModelName is { Length: > 0 } role)
        {
            activity.SetTag("logical_model", role);
        }

        return activity;
    }

    private void RecordSuccess(Activity? activity, long started, UsageDetails? usageDetails)
    {
        var elapsed = ElapsedSeconds(started);
        var usage = AiTokenUsageExtractor.FromUsage(usageDetails, target.ProviderKind);
        var cost = AiCostCalculator.Calculate(usage, pricing);

        metrics.RecordCall(target.ProviderKind, target.ModelId, "ok", elapsed);
        this.RecordTokens(usage);
        if (cost.Usd is { } usd)
        {
            metrics.RecordCost(target.ProviderKind, target.ModelId, usd);
        }

        if (activity is not null)
        {
            activity.SetTag("ai_input_tokens", usage.InputTokens);
            activity.SetTag("ai_output_tokens", usage.OutputTokens);
            activity.SetTag("ai_cached_input_tokens", usage.CachedInputTokens);
            activity.SetTag("ai_cache_write_tokens", usage.CacheWriteTokens);
            activity.SetTag("ai_reasoning_tokens", usage.ReasoningTokens);
            activity.SetTag("ai_usage_measured", !usage.IsEstimated);
            if (cost.Usd is { } tagged)
            {
                activity.SetTag("ai_cost_usd", tagged);
            }

            activity.SetStatus(ActivityStatusCode.Ok);
        }

        if (logger is not null)
        {
            LogCallCompleted(
                logger,
                target.Describe(),
                usage.InputTokens,
                usage.OutputTokens,
                usage.CachedInputTokens,
                usage.CacheWriteTokens,
                usage.ReasoningTokens,
                cost.Usd,
                elapsed);
        }
    }

    private void RecordTokens(AiTokenUsage usage)
    {
        // Zero counts are skipped rather than recorded: a counter that never moves is readable as "this provider
        // reports nothing here", while a stream of explicit zeros is just noise.
        Record("input", usage.InputTokens);
        Record("output", usage.OutputTokens);
        Record("cached_input", usage.CachedInputTokens);
        Record("cache_write", usage.CacheWriteTokens);
        Record("reasoning", usage.ReasoningTokens);

        void Record(string kind, long count)
        {
            if (count > 0)
            {
                metrics.RecordTokens(target.ProviderKind, target.ModelId, kind, count);
            }
        }
    }

    private void RecordFailure(Activity? activity, long started, Exception exception)
    {
        // A cancellation is not a provider fault — a stopped job and a budget refusal both arrive this way — so
        // it is counted apart from errors rather than inflating a provider's failure rate.
        var cancelled = exception is OperationCanceledException;

        // A throttle gets its own outcome for the same reason. The provider answered that it has no quota left
        // for the moment, and the retry stage above waits and asks again. Keeping it apart from errors is what
        // lets a rate limit be told from an outage in the chart that used to show both the same way.
        var verdict = cancelled ? null : this.Classify(exception);
        var throttled = verdict?.IsThrottled == true;
        var faulted = !cancelled && !throttled;
        var outcome = cancelled ? "cancelled" : throttled ? "throttled" : "error";
        metrics.RecordCall(target.ProviderKind, target.ModelId, outcome, ElapsedSeconds(started));

        if (activity is not null)
        {
            if (faulted)
            {
                // Backends derive a provider's error rate from this tag, so a throttle or a stop the review
                // recovered from must not carry one.
                activity.SetTag("error.type", exception.GetType().FullName ?? exception.GetType().Name);
            }

            activity.SetStatus(faulted ? ActivityStatusCode.Error : ActivityStatusCode.Unset, exception.Message);
        }

        if (logger is null)
        {
            return;
        }

        if (faulted)
        {
            LogCallFailed(logger, target.Describe(), exception);
        }
        else if (verdict is { IsThrottled: true } throttle)
        {
            // No exception argument, so no stack trace: the retry stage owns the operator-facing line about the
            // throttle, and a second one carrying a trace reads as a fault that nothing actually suffered.
            LogCallThrottled(logger, target.Describe(), throttle.Reason);
        }
    }

    /// <summary>
    ///     Asks the driver what the failure was, treating a classifier that throws as having no opinion.
    ///     Recording a call is a side errand, and the exception the retry stage still has to classify must reach
    ///     it whatever happens here.
    /// </summary>
    /// <param name="exception">The failure the attempt threw.</param>
    private ProviderFailureVerdict? Classify(Exception exception)
    {
        if (classifyFailure is null)
        {
            return null;
        }

        try
        {
            return classifyFailure(exception);
        }
        catch (Exception)
        {
            return null;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "AI provider call to {Target} completed in {ElapsedSeconds:0.###}s: in={InputTokens} out={OutputTokens} "
                  + "cachedIn={CachedInputTokens} cacheWrite={CacheWriteTokens} reasoning={ReasoningTokens} costUsd={CostUsd}")]
    private static partial void LogCallCompleted(
        ILogger logger,
        string target,
        long inputTokens,
        long outputTokens,
        long cachedInputTokens,
        long cacheWriteTokens,
        long reasoningTokens,
        decimal? costUsd,
        double elapsedSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AI provider call to {Target} failed.")]
    private static partial void LogCallFailed(ILogger logger, string target, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "AI provider call to {Target} was throttled ({Reason}).")]
    private static partial void LogCallThrottled(ILogger logger, string target, string reason);
}
