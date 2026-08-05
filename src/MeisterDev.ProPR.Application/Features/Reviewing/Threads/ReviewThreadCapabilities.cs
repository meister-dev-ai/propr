// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Threads;

/// <summary>
///     The advertised provider capabilities thread work depends on. Named once so the trigger and the pass
///     ask the registry the same question, and so a provider that cannot support one degrades identically on
///     both sides.
/// </summary>
public static class ReviewThreadCapabilities
{
    /// <summary>Writing a thread's status, without which no thread pass runs at all.</summary>
    public const string Status = "reviewThreadStatus";

    /// <summary>Posting into an existing thread, without which threads resolve but nothing is written back.</summary>
    public const string Reply = "reviewThreadReply";

    /// <summary>Whether the advertised set contains the named capability.</summary>
    /// <param name="capabilities">The provider's advertised capability set.</param>
    /// <param name="capability">The capability to look for.</param>
    /// <returns><c>true</c> when the provider advertises it.</returns>
    public static bool Advertises(IReadOnlyCollection<string>? capabilities, string capability)
    {
        return capabilities is not null
               && capabilities.Any(candidate => string.Equals(candidate, capability, StringComparison.Ordinal));
    }
}
