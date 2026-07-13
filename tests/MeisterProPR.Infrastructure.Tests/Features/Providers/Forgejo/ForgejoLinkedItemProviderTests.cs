// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;

namespace MeisterProPR.Infrastructure.Tests.Features.Providers.Forgejo;

public sealed class ForgejoLinkedItemProviderTests
{
    [Fact]
    public void ExtractIssueReferences_ParsesNumbersFromTitleAndBody_Deduplicated()
    {
        var references = ForgejoLinkedItemProvider.ExtractIssueReferences(
            "Fixes #12 in the parser",
            "Also closes #7 and relates to #12.");

        // #12 appears in both title and body but is returned once; first-seen order is preserved.
        Assert.Equal(new[] { "12", "7" }, references);
    }

    [Fact]
    public void ExtractIssueReferences_ReturnsEmpty_WhenNoReferences()
    {
        var references = ForgejoLinkedItemProvider.ExtractIssueReferences("No refs here", "plain description");

        Assert.Empty(references);
    }

    [Fact]
    public void ExtractIssueReferences_HandlesNullTitleAndBody()
    {
        var references = ForgejoLinkedItemProvider.ExtractIssueReferences(null, null);

        Assert.Empty(references);
    }
}
