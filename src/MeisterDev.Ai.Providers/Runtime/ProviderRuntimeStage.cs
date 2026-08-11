// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Runtime;

/// <summary>
///     The stages a model call passes through, ordered outermost first. The order is fixed rather than left to
///     whoever registers a decorator, because it changes behaviour rather than style:
///     <list type="bullet">
///         <item>
///             <description>
///                 <see cref="Retry" /> outermost means every attempt traverses the stages below it, so a metering
///                 stage counts each attempt exactly once. A retryable failure carries no usage payload, so failed
///                 attempts contribute nothing.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="Pacing" /> inside retry, because each attempt that gets past the gate is a real
///                 request against the quota, so the attempt budget stays with retry. It sits outside
///                 <see cref="Observability" /> so that time spent waiting on a throttled connection is not
///                 counted as provider latency.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="Observability" /> inside retry sees each attempt separately, and captures a
///                 <see cref="Budget" /> refusal within the attempt that provoked it.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="Normalization" /> innermost shapes the request and response actually put on the
///                 wire, so it applies to retried attempts too.
///             </description>
///         </item>
///     </list>
/// </summary>
public enum ProviderRuntimeStage
{
    /// <summary>Retries transient provider failures. Outermost, so each attempt passes through every later stage.</summary>
    Retry = 0,

    /// <summary>Holds an attempt back while the connection it is bound for is known to be out of quota.</summary>
    Pacing = 1,

    /// <summary>Records spans, logs, and usage for a single attempt.</summary>
    Observability = 2,

    /// <summary>Meters and gates spend. Host-supplied; the library has no notion of cost or entitlement.</summary>
    Budget = 3,

    /// <summary>Adapts requests and responses for a specific model's quirks. Innermost, closest to the wire.</summary>
    Normalization = 4,
}
