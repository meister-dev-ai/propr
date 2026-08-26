// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     One quota's effective ceiling, as an enforcement decision reads it.
///     <para>
///         The ceiling comes with where it came from and the lifecycle stage it was resolved under, so a
///         refusal can name the number and say whether the license or the community value produced it. Every
///         caller that reads a ceiling from here reports the same limit for a dimension.
///     </para>
///     <para>
///         The values are fixed at the instant the resolution was built. A caller that holds one across a stage
///         boundary reads the ceiling that held when it was resolved.
///     </para>
/// </summary>
public sealed record LicenseLimitResolution
{
    /// <summary>
    ///     Keeps construction to the factory methods below. Without it the record carries an implicit public
    ///     constructor, and a caller outside this assembly could build a resolution whose ceiling names a count
    ///     it does not carry.
    ///     <para>
    ///         The members cannot carry <see langword="required" /> instead: a required member of a public type
    ///         may not have a setter less visible than the type, so it cannot be combined with the private
    ///         setters that keep the members out of reach of every caller.
    ///     </para>
    /// </summary>
    internal LicenseLimitResolution()
    {
    }

    /// <summary>The dimension the ceiling constrains.</summary>
    public LicenseLimitKey Key { get; private init; }

    /// <summary>What kind of ceiling applies.</summary>
    public LicenseLimitCeiling Ceiling { get; private init; }

    /// <summary>
    ///     The ceiling. Set exactly when <see cref="Ceiling" /> is <see cref="LicenseLimitCeiling.Count" />,
    ///     and null for an unlimited or an unmetered dimension.
    ///     <para>
    ///         The constructor is internal and every setter is private, so a resolution comes from the factory
    ///         methods below and no caller can build one whose count contradicts its ceiling. The setters are
    ///         private rather than internal because a record supports <c>with</c>, which copies an instance and
    ///         reassigns a member through its setter. An internal setter would let code in this assembly produce
    ///         the contradictory state that way, without calling the constructor.
    ///     </para>
    /// </summary>
    public long? Count { get; private init; }

    /// <summary>Which side of the licensing rules produced the ceiling.</summary>
    public LicenseLimitSource Source { get; private init; }

    /// <summary>The lifecycle stage the ceiling was resolved under.</summary>
    public LicenseStage Stage { get; private init; }

    /// <summary>A resolution that puts no ceiling on the dimension.</summary>
    /// <param name="key">The dimension.</param>
    /// <param name="source">Where the answer came from.</param>
    /// <param name="stage">The stage it was resolved under.</param>
    /// <returns>The resolution.</returns>
    public static LicenseLimitResolution Unlimited(
        LicenseLimitKey key,
        LicenseLimitSource source,
        LicenseStage stage) => new()
    {
        Key = key,
        Ceiling = LicenseLimitCeiling.Unlimited,
        Source = source,
        Stage = stage,
    };

    /// <summary>A resolution that holds the dimension to a count.</summary>
    /// <param name="key">The dimension.</param>
    /// <param name="count">The ceiling, which may be zero but not negative.</param>
    /// <param name="source">Where the answer came from.</param>
    /// <param name="stage">The stage it was resolved under.</param>
    /// <returns>The resolution.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is negative.</exception>
    public static LicenseLimitResolution Of(
        LicenseLimitKey key,
        long count,
        LicenseLimitSource source,
        LicenseStage stage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        return new LicenseLimitResolution
        {
            Key = key,
            Ceiling = LicenseLimitCeiling.Count,
            Count = count,
            Source = source,
            Stage = stage,
        };
    }

    /// <summary>A resolution for a dimension nothing counts, which therefore has no ceiling to enforce.</summary>
    /// <param name="key">The dimension.</param>
    /// <param name="source">Where the answer came from.</param>
    /// <param name="stage">The stage it was resolved under.</param>
    /// <returns>The resolution.</returns>
    public static LicenseLimitResolution Unmetered(
        LicenseLimitKey key,
        LicenseLimitSource source,
        LicenseStage stage) => new()
    {
        Key = key,
        Ceiling = LicenseLimitCeiling.Unmetered,
        Source = source,
        Stage = stage,
    };
}
