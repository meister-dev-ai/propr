// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure;

/// <summary>Truncation limits applied when persisting protocol event text content.</summary>
internal static class ProtocolLimits
{
    /// <summary>Maximum number of characters stored for input text samples and output summaries.</summary>
    public const int TextSampleMaxLength = 50_000;

    /// <summary>
    ///     Maximum number of characters stored for tool result excerpts when the review loop is on
    ///     iteration 4 or later (depth &gt; 3). Caps expensive deep-loop protocol storage to
    ///     reduce both DB footprint and retransmission overhead.
    /// </summary>
    public const int ToolResultExcerptMaxLength = 1_000;
}
