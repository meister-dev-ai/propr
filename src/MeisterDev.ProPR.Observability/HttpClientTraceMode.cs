// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Observability;

/// <summary>How much of the outbound HTTP traffic is turned into trace spans.</summary>
public enum HttpClientTraceMode
{
    /// <summary>Emit no outbound HTTP spans at all; the metrics histogram remains the only view.</summary>
    Off = 0,

    /// <summary>
    ///     Emit spans only for foreground work: requests made outside a
    ///     <see cref="BackgroundActivityScope" /> and not aimed at a health or metrics endpoint.
    /// </summary>
    Foreground = 1,

    /// <summary>Emit a span for every outbound request.</summary>
    All = 2,
}
