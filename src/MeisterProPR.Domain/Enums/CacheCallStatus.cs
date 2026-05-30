// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Cache outcome recorded for one AI call.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CacheCallStatus
{
    NotApplicable = 0,
    Hit = 1,
    Miss = 2,
    Unobservable = 3,
    Ineligible = 4,
    Expired = 5,
    RoutingOverflow = 6,
    Unsupported = 7,
}
