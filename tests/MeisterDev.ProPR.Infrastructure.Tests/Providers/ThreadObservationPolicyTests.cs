// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using System.Security.Cryptography;
using System.Text;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common.Compatibility;

namespace MeisterDev.ProPR.Infrastructure.Tests.Providers;

public sealed class ThreadObservationPolicyTests
{
    [Theory]
    [InlineData(ScmProvider.AzureDevOps)]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public void LocalPreparationOwnsCurrentThreadResolution(ScmProvider provider)
    {
        var registry = ScmLocalPolicyFactory.CreateRegistry();
        var policy = registry.GetCodeReviewPreparationPolicy(provider);
        Assert.Equal(ThreadResolutionIntent.ClaimsFix, policy.InterpretThreadResolution("Fixed"));
        Assert.Equal(ThreadResolutionIntent.Active, policy.InterpretThreadResolution(" Fixed "));
        Assert.Equal(ThreadResolutionIntent.AcceptedByHuman, policy.InterpretThreadResolution("byDESIGN"));
        Assert.Equal(ThreadResolutionIntent.Active, policy.InterpretThreadResolution("2"));
        Assert.False(registry.IsRegistered(provider));
    }

    [Fact]
    public void StoredStatusCompatibilityIsExplicitAndProviderless()
    {
        Assert.Equal(ThreadResolutionIntent.AcceptedByHuman, SavedThreadStatusCompatibilityDecoder.Decode("WontFix"));
        Assert.Equal(ThreadResolutionIntent.Active, SavedThreadStatusCompatibilityDecoder.Decode(" WontFix "));
    }

    [Fact]
    public void SavedRevisionObservationAliasesHaveANeutralOwner()
    {
        Assert.Null(ReviewRevisionObservationSequence.FromRevision(null));
        Assert.Equal(23, ReviewRevisionObservationSequence.FromRevision(new("head", "base", null, "23", "patch")));
        Assert.Equal(23, ReviewRevisionObservationSequence.FromRevision(new("head", "base", null, "00023", "patch")));
        Assert.Equal(Hash("patch"), ReviewRevisionObservationSequence.FromRevision(new("head", "base", null, "", "patch")));
        Assert.Equal(Hash("patch"), ReviewRevisionObservationSequence.FromRevision(new("head", "base", null, " ", "patch")));
        Assert.Equal(Hash("revision"), ReviewRevisionObservationSequence.FromRevision(new("head", "base", null, "revision", "patch")));
        Assert.Equal(Hash("patch"), ReviewRevisionObservationSequence.FromRevision(new("head", "base", null, null, "patch")));
        Assert.Equal(Hash("base::head::"), ReviewRevisionObservationSequence.FromRevision(new("head", "base", null, null, null)));
    }

    [Theory]
    [InlineData("WontFix", ThreadResolutionIntent.AcceptedByHuman)]
    [InlineData("byDESIGN", ThreadResolutionIntent.AcceptedByHuman)]
    [InlineData("Fixed", ThreadResolutionIntent.ClaimsFix)]
    [InlineData("CLOSED", ThreadResolutionIntent.ClaimsFix)]
    [InlineData(" Fixed ", ThreadResolutionIntent.Active)]
    [InlineData("2", ThreadResolutionIntent.Active)]
    [InlineData("Pending", ThreadResolutionIntent.Active)]
    [InlineData(null, ThreadResolutionIntent.Active)]
    public void SavedReadGrammarDoesNotTrimOrAcceptNumericTokens(string? status, ThreadResolutionIntent expected)
    {
        Assert.Equal(expected, ScmLocalPolicyFactory.CreateRegistry().CompatibilityCodec.DecodeStoredThreadResolution(status));
    }

    private static int Hash(string key)
    {
        var value = BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0) & int.MaxValue;
        return value == 0 ? 1 : value;
    }
}
