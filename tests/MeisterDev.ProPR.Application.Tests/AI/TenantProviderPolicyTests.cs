// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;

namespace MeisterDev.ProPR.Application.Tests.AI;

/// <summary>
///     Covers the one rule both enforcement points ask. The reading of an empty list is the load-bearing decision:
///     get it backwards and every tenant that has never stated a policy is locked out of every provider.
/// </summary>
public sealed class TenantProviderPolicyTests
{
    [Fact]
    public void NoStatedPolicyPermitsEveryProvider()
    {
        var policy = TenantProviderPolicy.Unrestricted;

        Assert.False(policy.IsRestricted);
        Assert.All(
            Enum.GetValues<AiProviderKind>(),
            kind => Assert.True(policy.IsAllowed(kind)));
    }

    [Fact]
    public void AStatedPolicyPermitsOnlyWhatItNames()
    {
        var policy = new TenantProviderPolicy([AiProviderKind.AzureOpenAi, AiProviderKind.LiteLlm]);

        Assert.True(policy.IsRestricted);
        Assert.True(policy.IsAllowed(AiProviderKind.AzureOpenAi));
        Assert.True(policy.IsAllowed(AiProviderKind.LiteLlm));
        Assert.False(policy.IsAllowed(AiProviderKind.OpenAiCompatible));
        Assert.False(policy.IsAllowed(AiProviderKind.OpenAi));
    }

    // The refusal is what an operator reads on a rejected form, so it has to say what to choose instead rather
    // than only that the choice was wrong.
    [Fact]
    public void TheRefusalNamesWhatWasRefusedAndWhatIsPermitted()
    {
        var policy = new TenantProviderPolicy([AiProviderKind.AzureOpenAi]);

        var refusal = policy.DescribeRefusal(AiProviderKind.OpenAiCompatible);

        Assert.NotNull(refusal);
        Assert.Contains("OpenAiCompatible", refusal, StringComparison.Ordinal);
        Assert.Contains("AzureOpenAi", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void APermittedProviderHasNothingToRefuse()
    {
        var policy = new TenantProviderPolicy([AiProviderKind.AzureOpenAi]);

        Assert.Null(policy.DescribeRefusal(AiProviderKind.AzureOpenAi));
        Assert.Null(TenantProviderPolicy.Unrestricted.DescribeRefusal(AiProviderKind.OpenAiCompatible));
    }

    [Fact]
    public void ARepeatedKindIsListedOnce()
    {
        var policy = new TenantProviderPolicy([AiProviderKind.OpenAi, AiProviderKind.OpenAi]);

        Assert.Equal([AiProviderKind.OpenAi], policy.AllowedKinds);
    }
}
