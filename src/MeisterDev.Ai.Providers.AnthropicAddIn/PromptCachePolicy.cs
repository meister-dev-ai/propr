// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.AnthropicAddIn;

/// <summary>
///     When a prompt is large enough to be worth asking Anthropic to cache.
/// </summary>
/// <remarks>
///     A marker is not free: Anthropic charges more for the request that writes the cache, and below a certain
///     size the write costs more than the reads save. Anthropic also ignores a marker under its own per-model
///     minimum, so this floor sits above the highest of them rather than tracking each. A request may carry only
///     four breakpoints, so one spent below the floor buys nothing and is one fewer for the prefixes that pay.
/// </remarks>
internal static class PromptCachePolicy
{
    /// <summary>
    ///     The smallest prompt worth marking, in characters. Characters rather than tokens because this only has
    ///     to clear a floor, and tokenising to answer it would cost more than the marker saves. Roughly a
    ///     thousand tokens of English, which clears the per-model minimums in use.
    /// </summary>
    public const int MinimumCacheableChars = 4096;
}
