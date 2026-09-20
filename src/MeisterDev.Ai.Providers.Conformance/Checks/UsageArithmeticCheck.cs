// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Conformance.Checks;

/// <summary>
///     A family's usage mapping returns counters that satisfy the relationship the host prices against.
/// </summary>
/// <remarks>
///     <para>
///         The five counters are not five independent quantities. The input total covers both cache buckets and
///         the output total covers the reasoning portion, and the host bills the input total less the two cache
///         buckets, floored at zero. A family whose vendor reports input exclusive of its cache buckets and
///         returns those counts unchanged therefore floors the billed input at zero and charges the whole prompt
///         at nothing. The counts look plausible, every counter is populated, and the spend is wrong.
///     </para>
///     <para>
///         The payload is replayed through the driver's own mapping rather than counted, because counting which
///         counters came back populated passes a family that normalized and one that did not alike. It is a
///         payload the family recorded from its own vendor, in the form that vendor's client library hands the
///         counts to the host, so what is measured is the mapping the host actually prices against and not a
///         second one written for the check.
///     </para>
/// </remarks>
internal sealed class UsageArithmeticCheck : DriverConformanceCheck
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public override string Name => "usage-arithmetic";

    /// <inheritdoc />
    protected override ConformanceResult Evaluate(ConformanceSubject subject)
    {
        var payload = subject.ResolvedInputs.RecordedUsagePayload;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return ConformanceResult.Fail(
                this.Name,
                "The family records no usage payload from its vendor, so its mapping is unmeasured and a call of "
                + "this family could be billed at anything.");
        }

        UsageDetails? reported;
        try
        {
            reported = JsonSerializer.Deserialize<UsageDetails>(payload, PayloadOptions);
        }
        catch (JsonException exception)
        {
            return ConformanceResult.Fail(
                this.Name,
                $"The recorded usage payload cannot be read as a usage payload: {exception.Message}");
        }

        if (reported is null)
        {
            return ConformanceResult.Fail(this.Name, "The recorded usage payload is empty.");
        }

        var usage = subject.Driver.ReadUsage(reported);
        if (usage is null)
        {
            return ConformanceResult.Fail(
                this.Name,
                "The family's mapping returned nothing for a usage payload its own vendor produced, so every call "
                + "of this family would be billed as zero.");
        }

        if (usage.InputTokens < usage.CachedInputTokens + usage.CacheWriteTokens)
        {
            return ConformanceResult.Fail(
                this.Name,
                $"The mapping returns {usage.InputTokens} input tokens against {usage.CachedInputTokens} cache-read "
                + $"and {usage.CacheWriteTokens} cache-write. The input total is inclusive of both, and the host "
                + "bills the remainder floored at zero, so these counts bill the non-cached prompt at nothing. A "
                + "vendor that reports input exclusive of its cache buckets is normalized before the counts are "
                + "returned.");
        }

        if (usage.OutputTokens < usage.ReasoningTokens)
        {
            return ConformanceResult.Fail(
                this.Name,
                $"The mapping returns {usage.OutputTokens} output tokens against {usage.ReasoningTokens} reasoning "
                + "tokens. The output total is inclusive of the reasoning portion.");
        }

        return usage.IsEstimated
            ? ConformanceResult.Fail(
                this.Name,
                "The mapping reports the counts as estimated for a payload its vendor measured, so every call of "
                + "this family is recorded as a placeholder rather than as spend.")
            : ConformanceResult.Pass(this.Name);
    }
}
