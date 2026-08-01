// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.AI;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public sealed class ReviewPromptsTemplateBackedLegacyPromptTests
{
    [Fact]
    public void BuildUserMessage_WithChangedFiles_UsesTemplateBackedDefault()
    {
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "Test PR",
            "PR description",
            "feature/x",
            "main",
            [new ChangedFile("src/Foo.cs", ChangeType.Edit, "class Foo {}", "@@ -1 +1 @@\n-class FooOld {}\n+class Foo {}")],
            PrStatus.Active,
            []);

        var message = ReviewPrompts.BuildUserMessage(pr);

        Assert.Contains("Pull Request: Test PR", message, StringComparison.Ordinal);
        Assert.Contains("======================================= FULL CONTENT =======================================", message, StringComparison.Ordinal);
        Assert.Contains("======================================= DIFF =======================================", message, StringComparison.Ordinal);
    }
}
