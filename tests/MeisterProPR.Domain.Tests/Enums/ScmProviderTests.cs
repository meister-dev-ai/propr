// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Tests.Enums;

public sealed class ScmProviderTests
{
    [Theory]
    [InlineData(ScmProvider.AzureDevOps)]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public void ScmProvider_AllSupportedFamilies_AreDefined(ScmProvider provider)
    {
        Assert.True(Enum.IsDefined(typeof(ScmProvider), provider));
    }

    [Theory]
    [InlineData(ScmAuthenticationKind.OAuthClientCredentials)]
    [InlineData(ScmAuthenticationKind.PersonalAccessToken)]
    [InlineData(ScmAuthenticationKind.AppInstallation)]
    public void ScmAuthenticationKind_AllSupportedModes_AreDefined(ScmAuthenticationKind authenticationKind)
    {
        Assert.True(Enum.IsDefined(typeof(ScmAuthenticationKind), authenticationKind));
    }

    [Theory]
    [InlineData(CodeReviewPlatformKind.PullRequest)]
    [InlineData(CodeReviewPlatformKind.MergeRequest)]
    public void CodeReviewPlatformKind_AllSupportedValues_AreDefined(CodeReviewPlatformKind platformKind)
    {
        Assert.True(Enum.IsDefined(typeof(CodeReviewPlatformKind), platformKind));
    }

    [Theory]
    [InlineData(CodeReviewState.Open)]
    [InlineData(CodeReviewState.Draft)]
    [InlineData(CodeReviewState.Merged)]
    [InlineData(CodeReviewState.Closed)]
    public void CodeReviewState_AllSupportedValues_AreDefined(CodeReviewState state)
    {
        Assert.True(Enum.IsDefined(typeof(CodeReviewState), state));
    }
}
