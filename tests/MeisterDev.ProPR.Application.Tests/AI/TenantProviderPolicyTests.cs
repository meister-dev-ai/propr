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

    // Where the traffic goes is the half a provider family cannot answer: an OpenAI-compatible profile reached at
    // an operator-supplied base URL is constrained by its family not at all.
    [Fact]
    public void NoStatedHostPolicyPermitsEveryDestination()
    {
        Assert.False(TenantProviderPolicy.Unrestricted.RestrictsEndpoints);
        Assert.True(TenantProviderPolicy.Unrestricted.IsEndpointAllowed("https://anywhere.example/v1"));
    }

    [Fact]
    public void AStatedHostPolicyPermitsOnlyWhatItNames()
    {
        var policy = new TenantProviderPolicy([], ["opencode.ai"]);

        Assert.True(policy.RestrictsEndpoints);
        Assert.True(policy.IsEndpointAllowed("https://opencode.ai/zen/v1"));
        Assert.False(policy.IsEndpointAllowed("https://api.deepseek.com/v1"));
    }

    // A vendor whose customers each get their own name is permitted by the parent domain, or the policy would
    // have to be rewritten for every new resource.
    [Fact]
    public void ALeadingDotPermitsSubdomainsAndTheDomainItself()
    {
        var policy = new TenantProviderPolicy([], [".openai.azure.com"]);

        Assert.True(policy.IsEndpointAllowed("https://my-resource.openai.azure.com/"));
        Assert.True(policy.IsEndpointAllowed("https://openai.azure.com/"));
        Assert.False(policy.IsEndpointAllowed("https://notopenai.azure.com/"));
        Assert.False(policy.IsEndpointAllowed("https://openai.azure.com.evil.example/"));
    }

    [Fact]
    public void HostMatchingIgnoresCaseAndSurroundingNoise()
    {
        var policy = new TenantProviderPolicy([], ["  OpenCode.AI/  "]);

        Assert.True(policy.IsEndpointAllowed("https://OPENCODE.ai/zen/v1"));
    }

    // A policy that only constrains the URLs it can read is not a policy.
    [Fact]
    public void AnUnreadableBaseUrlIsRefusedRatherThanWavedThrough()
    {
        var policy = new TenantProviderPolicy([], ["opencode.ai"]);

        Assert.False(policy.IsEndpointAllowed("not a url"));
        Assert.False(policy.IsEndpointAllowed(null));
    }

    [Fact]
    public void TheEndpointRefusalNamesWhatIsPermitted()
    {
        var policy = new TenantProviderPolicy([], ["opencode.ai"]);

        var refusal = policy.DescribeEndpointRefusal("https://api.deepseek.com/v1");

        Assert.NotNull(refusal);
        Assert.Contains("api.deepseek.com", refusal, StringComparison.Ordinal);
        Assert.Contains("opencode.ai", refusal, StringComparison.Ordinal);
    }

    // The two restrictions are independent: a tenant can constrain families, hosts, both, or neither.
    [Fact]
    public void FamiliesAndHostsRestrictIndependently()
    {
        var hostsOnly = new TenantProviderPolicy([], ["opencode.ai"]);

        Assert.False(hostsOnly.IsRestricted);
        Assert.True(hostsOnly.IsAllowed(AiProviderKind.Anthropic));
        Assert.False(hostsOnly.IsEndpointAllowed("https://api.anthropic.com/"));
    }
}
