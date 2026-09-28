// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Api.Validators;

/// <summary>
///     The range a USD budget cap must stay inside to survive persistence. The cap columns are
///     <c>numeric(18,6)</c>, so a larger magnitude makes the insert fail with a database overflow and a value
///     carrying more than six fractional digits is rounded on the way in, which changes the cap that is enforced.
///     Both are checked at the API boundary so the caller gets a 400 with a reason. The range starts at zero:
///     spend is never negative, so a negative cap would be reached by every review and stop all of them.
/// </summary>
internal static class MonetaryCapRange
{
    /// <summary>The largest value the cap columns hold.</summary>
    internal const decimal MaxUsd = 999_999_999_999.999999m;

    /// <summary>The number of fractional digits the cap columns keep.</summary>
    internal const int Scale = 6;

    /// <summary>
    ///     Returns whether the cap is absent or is a non-negative amount that can be stored without overflow
    ///     and without being rounded.
    /// </summary>
    internal static bool IsStorable(decimal? cap)
    {
        if (cap is not { } value)
        {
            return true;
        }

        return value is >= 0m and <= MaxUsd && decimal.Round(value, Scale) == value;
    }
}
