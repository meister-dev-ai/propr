// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Stable-prefix eligibility recorded for one cache-sensitive AI call.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PrefixEligibilityStatus
{
    NotApplicable = 0,
    Eligible = 1,
    IneligibleTooShort = 2,
    IneligiblePrefixUnstable = 3,
}
