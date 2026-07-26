// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Enums;

/// <summary>
///     How hard the caller wants a model to think, expressed in terms no provider owns.
/// </summary>
/// <remarks>
///     Deliberately coarse. Providers express this incompatibly — OpenAI as a named effort level, Anthropic as a
///     token budget, others not at all — and a caller that had to know which one it was talking to would defeat
///     the point of a shared runtime. Each driver maps these four values onto its own protocol.
/// </remarks>
public enum ProviderReasoningEffort
{
    /// <summary>No reasoning is requested; the provider keeps whatever default it has.</summary>
    None = 0,

    /// <summary>A short amount of reasoning.</summary>
    Low = 1,

    /// <summary>A moderate amount of reasoning.</summary>
    Medium = 2,

    /// <summary>As much reasoning as the model will usefully do.</summary>
    High = 3,
}
