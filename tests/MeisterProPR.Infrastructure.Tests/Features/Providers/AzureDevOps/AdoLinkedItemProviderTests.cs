// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

namespace MeisterProPR.Infrastructure.Tests.Features.Providers.AzureDevOps;

public sealed class AdoLinkedItemProviderTests
{
    [Theory]
    [InlineData("https://dev.azure.com/org/_apis/wit/workItems/123", "123")]
    [InlineData("https://dev.azure.com/org/_apis/wit/workItems/123/", "123")]
    [InlineData("https://dev.azure.com/org/_apis/wit/workItems/123/updates", "123")]
    public void ExtractRelationTargetKey_ReturnsLeadingNumericId(string url, string expected)
    {
        Assert.Equal(expected, AdoLinkedItemProvider.ExtractRelationTargetKey(url));
    }

    [Theory]
    [InlineData("vstfs:///Git/PullRequestId/1%2fabc")] // artifact link, not a work item
    [InlineData("https://dev.azure.com/org/_apis/wit/attachments/456")] // different wit resource
    [InlineData("https://example.com/some/other/path")]
    [InlineData("")]
    [InlineData(null)]
    public void ExtractRelationTargetKey_ReturnsNull_ForNonWorkItemOrEmpty(string? url)
    {
        Assert.Null(AdoLinkedItemProvider.ExtractRelationTargetKey(url));
    }
}
