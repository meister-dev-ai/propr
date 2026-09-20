// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Usage;

/// <summary>
///     Normalized token-usage counts read from a single provider response, carrying the full breakdown a
///     provider usage payload can expose: input and output plus the cache-read, cache-write, and reasoning
///     portions. A driver returns this shape, and a host maps it onto whatever its own accounting records use.
/// </summary>
/// <remarks>
///     <para>
///         <strong>The five counters are not five independent quantities, and a driver has to satisfy the
///         relationship between them.</strong> <see cref="InputTokens" /> is inclusive of both
///         <see cref="CachedInputTokens" /> and <see cref="CacheWriteTokens" />, and <see cref="OutputTokens" />
///         is inclusive of <see cref="ReasoningTokens" />. A driver whose vendor reports counts that exclude the
///         cache buckets adds them back in before returning this shape. Returning what the vendor sent is a
///         defect, not a variant.
///     </para>
///     <para>
///         What the host computes from the relationship is the input it bills at the full rate:
///         <see cref="NonCachedInputTokens" />, the input total less the two cache buckets, floored at zero. The
///         host charges that at the input rate and each cache bucket at its own. A driver that reports input
///         exclusive of its cache buckets therefore floors non-cached input at zero and bills the whole prompt at
///         nothing, which is silent under-billing: the counts look plausible, every counter is present, and the
///         spend is wrong.
///     </para>
///     <para>
///         <see cref="CountersAreInclusive" /> is the relationship in the form that can be checked, so a driver's
///         own build can assert it against a recorded vendor payload rather than counting which counters came
///         back populated. Presence passes a normalizing driver and an under-billing one alike.
///     </para>
/// </remarks>
/// <param name="InputTokens">Total prompt/input tokens the provider reported; already includes any cached-input and cache-write tokens.</param>
/// <param name="OutputTokens">Total completion/output tokens the provider reported; includes reasoning tokens.</param>
/// <param name="CachedInputTokens">Portion of <see cref="InputTokens" /> served from the provider prompt cache.</param>
/// <param name="CacheWriteTokens">
///     Portion of <see cref="InputTokens" /> written to the provider prompt cache (cache-creation); zero for
///     providers without a separate cache-write charge.
/// </param>
/// <param name="ReasoningTokens">Portion of <see cref="OutputTokens" /> spent on model reasoning.</param>
/// <param name="IsEstimated">
///     True when the response carried no usage payload, so the counts are placeholder zeros rather than measured
///     values. It survives the round trip through <see cref="UsageDetails" />, so a call the provider said
///     nothing about is not recorded as a call that cost nothing.
/// </param>
public sealed record ProviderTokenUsage(
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens = 0,
    long CacheWriteTokens = 0,
    long ReasoningTokens = 0,
    bool IsEstimated = false)
{
    // Held in fields so every counter is checked wherever it is set: a positional argument, an object
    // initializer and a `with` expression all reach the property, and a property with an accessor body cannot
    // carry a field initializer of its own. Every positional member is declared here, in the order of the
    // parameters, because a record that declares only some of them emits the rest first.
    private readonly long _inputTokens = Counted(InputTokens, nameof(InputTokens));
    private readonly long _outputTokens = Counted(OutputTokens, nameof(OutputTokens));
    private readonly long _cachedInputTokens = Counted(CachedInputTokens, nameof(CachedInputTokens));
    private readonly long _cacheWriteTokens = Counted(CacheWriteTokens, nameof(CacheWriteTokens));
    private readonly long _reasoningTokens = Counted(ReasoningTokens, nameof(ReasoningTokens));
    private readonly bool _isEstimated = IsEstimated;

    /// <summary>
    ///     The name cache-write tokens are carried under in <see cref="UsageDetails.AdditionalCounts" />.
    /// </summary>
    /// <remarks>
    ///     <see cref="UsageDetails" /> has a property for every counter in this shape except cache-write, so that
    ///     one bucket needs a name. The name is the host's, not a vendor's: a family maps its vendor's spelling
    ///     onto this one, and two families reporting cache-write are then read the same way.
    /// </remarks>
    public const string CacheWriteCountName = "meisterdev.cacheWriteTokens";

    /// <summary>
    ///     The name the estimated marker travels under in <see cref="UsageDetails.AdditionalCounts" />.
    /// </summary>
    /// <remarks>
    ///     A response that reported no usage becomes an all-zero payload on the way out, and a payload that is
    ///     present reads back as measured. Without a marker on it, a call the provider said nothing about is
    ///     recorded as a call that cost nothing, and those two are the difference between a quota that ran out
    ///     and a prompt that was free. Carried as a count because that is the only bag this shape has.
    /// </remarks>
    public const string EstimatedCountName = "meisterdev.usageIsEstimated";

    /// <summary>An all-zero usage flagged as estimated, returned when a response reports no usage.</summary>
    public static ProviderTokenUsage Missing { get; } = new(0, 0, IsEstimated: true);

    /// <summary>An all-zero measured usage.</summary>
    public static ProviderTokenUsage Zero { get; } = new(0, 0);

    /// <summary>Total prompt/input tokens the provider reported; already includes any cached and cache-write tokens.</summary>
    public long InputTokens
    {
        get => this._inputTokens;
        init => this._inputTokens = Counted(value, nameof(ProviderTokenUsage.InputTokens));
    }

    /// <summary>Total completion/output tokens the provider reported; includes reasoning tokens.</summary>
    public long OutputTokens
    {
        get => this._outputTokens;
        init => this._outputTokens = Counted(value, nameof(ProviderTokenUsage.OutputTokens));
    }

    /// <summary>Portion of <see cref="InputTokens" /> served from the provider prompt cache.</summary>
    public long CachedInputTokens
    {
        get => this._cachedInputTokens;
        init => this._cachedInputTokens = Counted(value, nameof(ProviderTokenUsage.CachedInputTokens));
    }

    /// <summary>Portion of <see cref="InputTokens" /> written to the provider prompt cache.</summary>
    public long CacheWriteTokens
    {
        get => this._cacheWriteTokens;
        init => this._cacheWriteTokens = Counted(value, nameof(ProviderTokenUsage.CacheWriteTokens));
    }

    /// <summary>Portion of <see cref="OutputTokens" /> spent on model reasoning.</summary>
    public long ReasoningTokens
    {
        get => this._reasoningTokens;
        init => this._reasoningTokens = Counted(value, nameof(ProviderTokenUsage.ReasoningTokens));
    }

    /// <summary>True when the response carried no usage payload, so the counts are placeholder zeros.</summary>
    public bool IsEstimated
    {
        get => this._isEstimated;
        init => this._isEstimated = value;
    }

    /// <summary>
    ///     Input tokens billed at the full input rate: <see cref="InputTokens" /> less the cached and cache-write
    ///     portions, floored at zero. This is the quantity the host prices against, so it is declared beside the
    ///     counters rather than derived separately by each reader.
    /// </summary>
    public long NonCachedInputTokens => Math.Max(0, this.InputTokens - this.CachedInputTokens - this.CacheWriteTokens);

    /// <summary>
    ///     Whether the counters satisfy the inclusion relationship: the input total covers both cache buckets and
    ///     the output total covers the reasoning portion. False means the driver returned a vendor's exclusive
    ///     counts without normalizing them, and the call would bill short.
    /// </summary>
    public bool CountersAreInclusive =>
        this.InputTokens >= this.CachedInputTokens + this.CacheWriteTokens
        && this.OutputTokens >= this.ReasoningTokens;

    /// <summary>
    ///     Reads the counters a usage payload already states in normalized terms. A driver whose vendor reports
    ///     inclusive counts returns this unchanged.
    /// </summary>
    /// <remarks>
    ///     Every counter but cache-write comes from a property of <see cref="UsageDetails" />, so this reads no
    ///     vendor field name. A driver whose vendor reports counts that exclude the cache buckets, or that names
    ///     a bucket its own way, maps them itself and does not call this.
    /// </remarks>
    /// <param name="usage">The usage payload, or <see langword="null" /> for a response that reported none.</param>
    public static ProviderTokenUsage FromUsageDetails(UsageDetails? usage)
    {
        if (usage is null)
        {
            return Missing;
        }

        return new ProviderTokenUsage(
            usage.InputTokenCount ?? 0,
            usage.OutputTokenCount ?? 0,
            usage.CachedInputTokenCount ?? 0,
            ReadCacheWrite(usage),
            usage.ReasoningTokenCount ?? 0,
            ReadEstimated(usage));
    }

    /// <summary>
    ///     Restates these counters as a usage payload, so the shape a driver returned is what every later reader
    ///     of the response sees.
    /// </summary>
    /// <remarks>
    ///     The total is the input and output counts added together. Both are inclusive of their buckets, so
    ///     adding the buckets again would count cached input twice.
    /// </remarks>
    public UsageDetails ToUsageDetails()
    {
        var usage = new UsageDetails
        {
            InputTokenCount = this.InputTokens,
            OutputTokenCount = this.OutputTokens,
            TotalTokenCount = this.InputTokens + this.OutputTokens,
            CachedInputTokenCount = this.CachedInputTokens,
            ReasoningTokenCount = this.ReasoningTokens,
        };

        var counts = new AdditionalPropertiesDictionary<long>();
        if (this.CacheWriteTokens > 0)
        {
            counts[CacheWriteCountName] = this.CacheWriteTokens;
        }

        // Carried across, because this payload is what the next reader takes the counts from. A response the
        // provider reported no usage for becomes an all-zero payload here, and one that is present reads back as
        // measured, so the call would be recorded as having cost nothing.
        if (this.IsEstimated)
        {
            counts[EstimatedCountName] = 1;
        }

        if (counts.Count > 0)
        {
            usage.AdditionalCounts = counts;
        }

        return usage;
    }

    /// <summary>
    ///     One counter, refused when it is negative.
    /// </summary>
    /// <remarks>
    ///     A token count is a quantity of work done, and the host prices against these five. A negative one
    ///     reaches the spend record as a credit against work that happened, and it also passes
    ///     <see cref="CountersAreInclusive" />, so the check that exists to catch a mis-normalized driver would
    ///     report it as sound.
    /// </remarks>
    /// <param name="value">The counter as the driver reported it.</param>
    /// <param name="parameter">The counter's name, for the refusal.</param>
    private static long Counted(long value, string parameter)
    {
        return value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(
                parameter,
                value,
                "A token count is a number of tokens, so it is not negative.");
    }

    private static long ReadCacheWrite(UsageDetails usage)
    {
        return usage.AdditionalCounts?.TryGetValue(CacheWriteCountName, out var cacheWrite) == true ? cacheWrite : 0;
    }

    private static bool ReadEstimated(UsageDetails usage)
    {
        return usage.AdditionalCounts?.TryGetValue(EstimatedCountName, out var estimated) == true && estimated != 0;
    }
}
