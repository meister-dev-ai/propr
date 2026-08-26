// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     The installation-wide ceiling on reviews executing at once, as a claim site reads it from a resolved
///     license limit.
///     <para>
///         Both claim sites read the ceiling here, so they enforce and report the same number, and a refusal
///         names where that number came from.
///     </para>
/// </summary>
public sealed record ConcurrentReviewCeiling
{
    private ConcurrentReviewCeiling(int cap, LicenseLimitSource source)
    {
        this.Cap = cap;
        this.Source = source;
    }

    /// <summary>How many reviews may execute at once across the installation. May be zero.</summary>
    public int Cap { get; }

    /// <summary>Which side of the licensing rules produced the cap.</summary>
    public LicenseLimitSource Source { get; }

    /// <summary>
    ///     The ceiling a resolved concurrent-review limit puts on claiming, or null when the limit does not cap
    ///     claiming.
    ///     <para>
    ///         An unlimited ceiling returns null so the caller takes the uncapped claim. Counting under an
    ///         unlimited ceiling would serialize every claim installation-wide without enforcing anything.
    ///         <see cref="LicenseLimitCeiling.Unmetered" /> is not an answer this key has, because
    ///         concurrent reviews resolve to a count or to unlimited in
    ///         <c>LicenseLimitResolver</c>, so it takes the same path as unlimited.
    ///     </para>
    /// </summary>
    /// <param name="resolution">The resolved limit for concurrent reviews.</param>
    /// <returns>The ceiling, or null when the limit does not cap claiming.</returns>
    public static ConcurrentReviewCeiling? From(LicenseLimitResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        if (resolution.Ceiling != LicenseLimitCeiling.Count || resolution.Count is not { } count)
        {
            return null;
        }

        // A stated limit is a long; the claim takes the cap as an int. Clamping keeps a number larger than an
        // int can hold from wrapping into a small or negative cap.
        return new ConcurrentReviewCeiling((int)Math.Min(count, int.MaxValue), resolution.Source);
    }

    /// <summary>
    ///     Operator-readable detail for a refusal made at this ceiling. Names the cap and, when the store
    ///     reported one, the count it observed.
    /// </summary>
    /// <param name="processingCount">
    ///     How many reviews were observed as executing, or null when no count was reported.
    /// </param>
    /// <returns>The detail text.</returns>
    public string Describe(int? processingCount)
    {
        var state = processingCount is { } observed
            ? string.Format(
                CultureInfo.InvariantCulture,
                "This installation is running {0} of {1} concurrent reviews.",
                observed,
                this.Cap)
            : string.Format(
                CultureInfo.InvariantCulture,
                "This installation is at its limit of {0} concurrent reviews.",
                this.Cap);

        // The community source covers three causes: no license, a license that leaves the limit out, and a
        // capability the licensed value depends on being unavailable. The wording holds for all three, and it
        // does not report a licensed installation as having no license.
        var origin = this.Source == LicenseLimitSource.License
            ? "The limit comes from the license in force."
            : "This installation is not entitled to a higher limit.";

        return state + " " + origin;
    }
}
