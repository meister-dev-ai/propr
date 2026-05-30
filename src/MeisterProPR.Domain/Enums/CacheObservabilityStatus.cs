// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Provider/model cache observability roll-up for one protocol pass.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CacheObservabilityStatus
{
    Unknown = 0,
    Observable = 1,
    Unobservable = 2,
    Unsupported = 3,
}
