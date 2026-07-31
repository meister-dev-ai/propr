// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Features.Reviewing.ThreadMemory;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.ThreadMemory;

/// <summary>
///     The keyword prompts render from their template files. A missing or misnamed template only fails at the
///     first extraction, on a path whose failures are swallowed, so it would surface as memories that quietly
///     never get keywords.
/// </summary>
public sealed class ThreadMemoryPromptTests
{
    [Fact]
    public void KeywordsSystem_StatesTheCapTheSanitizerEnforces()
    {
        var rendered = ThreadMemoryPrompts.KeywordsSystem(new ThreadMemoryPrompts.KeywordsSystemModel(8));

        // Asking for more than the sanitizer keeps would spend tokens on keywords that are then dropped.
        Assert.Contains("At most 8", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void KeywordsUser_IncludesTheChangeOnlyWhenThereIsOne(bool hasExcerpt)
    {
        var rendered = ThreadMemoryPrompts.KeywordsUser(
            new ThreadMemoryPrompts.KeywordsUserModel(
                "Agreed to guard the call site.",
                hasExcerpt,
                hasExcerpt ? "@@ -1 +1 @@" : string.Empty));

        Assert.Contains("Agreed to guard the call site.", rendered, StringComparison.Ordinal);
        Assert.Equal(hasExcerpt, rendered.Contains("Change:", StringComparison.Ordinal));
    }
}
