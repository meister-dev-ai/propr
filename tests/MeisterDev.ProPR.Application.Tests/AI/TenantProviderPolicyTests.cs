// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.AI;

/// <summary>
///     Covers the rule every enforcement point asks, and the reading of the stored columns it is built from. How
///     an empty list is read is the load-bearing decision twice over: read it as "nothing permitted" and every
///     tenant that never stated a policy is locked out, read it as unrestricted after discarding entries no
///     loaded family claims and a tenant that stated a policy stops restricting anything.
/// </summary>
public sealed class TenantProviderPolicyTests
{
    private const string OpenAiKey = "meisterdev/openAi";
    private const string SupersededKey = "AcmeOpenAi";
    private const string AnthropicKey = "meisterdev/anthropic";
    private const string BedrockKey = "meisterdev/awsBedrock";

    // Identities the loaded families in these cases do not claim, which an allow-list entry looks like
    // once the add-in that declared it is gone.
    private static readonly string[] UnclaimedIdentities = ["Acme.Llm", "Contoso.Chat", "contoso/chat"];

    [Fact]
    public void NoStatedPolicyPermitsEveryProvider()
    {
        var policy = TenantProviderPolicy.Unrestricted;

        Assert.False(policy.IsRestricted);
        Assert.All(
            new[] { OpenAiKey, AnthropicKey, BedrockKey, "contoso/chat" },
            key => Assert.True(policy.IsAllowed(key)));
    }

    [Fact]
    public void AStatedPolicyPermitsOnlyWhatItNames()
    {
        var policy = new TenantProviderPolicy([OpenAiKey, AnthropicKey]);

        Assert.True(policy.IsRestricted);
        Assert.True(policy.IsAllowed(OpenAiKey));
        Assert.True(policy.IsAllowed(AnthropicKey));
        Assert.False(policy.IsAllowed(BedrockKey));
        Assert.False(policy.IsAllowed("contoso/chat"));
    }

    // Two spellings of one key differing only in case name one family wherever a key is compared, so the answer
    // cannot depend on which casing the tenant's entry happened to be stored in.
    [Fact]
    public void APermittedFamilyIsMatchedIgnoringCase()
    {
        var policy = new TenantProviderPolicy([OpenAiKey]);

        Assert.True(policy.IsAllowed("MeisterDev/OpenAI"));
    }

    // The refusal is what an operator reads on a rejected form, so it has to say what to choose instead rather
    // than only that the choice was wrong.
    [Fact]
    public void TheRefusalNamesWhatWasRefusedAndWhatIsPermitted()
    {
        var policy = new TenantProviderPolicy([OpenAiKey]);

        var refusal = policy.GetRefusalReason(BedrockKey);

        Assert.NotNull(refusal);
        Assert.Contains(BedrockKey, refusal, StringComparison.Ordinal);
        Assert.Contains(OpenAiKey, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void APermittedProviderHasNothingToRefuse()
    {
        var policy = new TenantProviderPolicy([OpenAiKey]);

        Assert.Null(policy.GetRefusalReason(OpenAiKey));
        Assert.Null(TenantProviderPolicy.Unrestricted.GetRefusalReason(BedrockKey));
    }

    [Fact]
    public void ARepeatedKindIsListedOnce()
    {
        var policy = new TenantProviderPolicy([OpenAiKey, OpenAiKey]);

        Assert.Equal([OpenAiKey], policy.AllowedKinds);
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
        Assert.True(hostsOnly.IsAllowed(AnthropicKey));
        Assert.False(hostsOnly.IsEndpointAllowed("https://api.anthropic.com/"));
    }

    // The stored reading is where the fail-open was: an entry no family claims used to be dropped, and a policy
    // that reduced to nothing then read as no policy at all.
    [Fact]
    public void AStoredEntryNoFamilyClaimsPermitsNothingAndLeavesTheRestRestricting()
    {
        var policy = TenantProviderPolicy.FromStored([OpenAiKey, "Acme.Llm"], [], Registry());

        Assert.True(policy.IsRestricted);
        Assert.True(policy.IsAllowed(OpenAiKey));
        Assert.False(policy.IsAllowed(AnthropicKey));
        Assert.Equal(["Acme.Llm"], policy.UnresolvedProviderEntries);
    }

    // An allow-list entry is an identity, and a family has more than one spelling for it: the key it declares
    // and the keys it supersedes. An entry is held as the claiming family's declared key, which every
    // connection of that family also resolves to, so the two sides of the comparison cannot drift apart.
    [Fact]
    public void AStoredEntryNamingAFamilyByItsDeclaredKeyResolvesToThatFamily()
    {
        var policy = TenantProviderPolicy.FromStored([OpenAiKey], [], Registry());

        Assert.True(policy.IsRestricted);
        Assert.True(policy.IsAllowed(OpenAiKey));
        Assert.False(policy.IsAllowed(AnthropicKey));
        Assert.Empty(policy.UnresolvedProviderEntries);
    }

    // The outcome a partly rewritten allow-list has to keep, whichever spelling each entry happens to hold. The
    // failure this guards against is silent: a tenant whose entries stopped resolving stops restricting nothing
    // and starts refusing everything, and neither is visible until a review fails.
    [Fact]
    public void ATenantWhoseEntriesAreSpelledTwoWaysStaysRestrictedAndPermitsBoth()
    {
        var policy = TenantProviderPolicy.FromStored([SupersededKey, AnthropicKey], [], Registry());

        Assert.True(policy.IsRestricted);
        Assert.True(policy.IsAllowed(OpenAiKey));
        Assert.True(policy.IsAllowed(AnthropicKey));
        Assert.False(policy.IsAllowed(BedrockKey));
        Assert.Empty(policy.UnresolvedProviderEntries);
    }

    // A spelling a family declares it supersedes names that family too, and that keeps an entry written
    // before the family had a key resolving to it. It is held as the key, so it permits the family's connections,
    // which carry the key.
    [Fact]
    public void AStoredEntryNamingASpellingAFamilySupersedesResolvesToThatFamily()
    {
        var policy = TenantProviderPolicy.FromStored([SupersededKey], [], Registry());

        Assert.True(policy.IsAllowed(OpenAiKey));
        Assert.Equal([OpenAiKey], policy.AllowedKinds);
        Assert.Empty(policy.UnresolvedProviderEntries);
    }

    [Fact]
    public void AStoredListNoFamilyClaimsAtAllRefusesEveryFamily()
    {
        var policy = TenantProviderPolicy.FromStored(["Acme.Llm", "Contoso.Chat"], [], Registry());

        Assert.True(policy.IsRestricted);
        Assert.All(
            new[] { OpenAiKey, AnthropicKey, BedrockKey }.Concat(UnclaimedIdentities),
            key => Assert.False(policy.IsAllowed(key)));
        Assert.Equal(["Acme.Llm", "Contoso.Chat"], policy.UnresolvedProviderEntries);
    }

    [Fact]
    public void ATenantThatStoredNothingIsUnrestricted()
    {
        var policy = TenantProviderPolicy.FromStored([], [], Registry());

        Assert.Same(TenantProviderPolicy.Unrestricted, policy);
        Assert.False(policy.IsRestricted);
        Assert.False(policy.RestrictsEndpoints);
        Assert.Empty(policy.UnresolvedProviderEntries);
    }

    // Both legs have to survive: an endpoint restriction alongside families that no longer resolve is a tenant
    // that constrained two things and is entitled to keep both.
    [Fact]
    public void AHostRestrictionAndAnUnclaimedFamilyRestrictBothLegs()
    {
        var policy = TenantProviderPolicy.FromStored(["Acme.Llm"], ["opencode.ai"], Registry());

        Assert.True(policy.IsRestricted);
        Assert.True(policy.RestrictsEndpoints);
        Assert.False(policy.IsAllowed(OpenAiKey));
        Assert.False(policy.IsEndpointAllowed("https://api.deepseek.com/v1"));
        Assert.True(policy.IsEndpointAllowed("https://opencode.ai/zen/v1"));
    }

    // A refusal that says nothing is permitted and stops there leaves the operator no way to find out why, which
    // would trade a silent fail-open for a silent fail-closed.
    [Fact]
    public void TheRefusalNamesTheEntryNoFamilyClaims()
    {
        var policy = TenantProviderPolicy.FromStored(["Acme.Llm"], [], Registry());

        var refusal = policy.GetRefusalReason(OpenAiKey);

        Assert.NotNull(refusal);
        Assert.Contains(OpenAiKey, refusal, StringComparison.Ordinal);
        Assert.Contains("Acme.Llm", refusal, StringComparison.Ordinal);
        Assert.Contains("none", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AStoredIdentityIsMatchedTheWayItIsRead()
    {
        var policy = TenantProviderPolicy.FromStored(["  MeisterDev/OpenAI  "], [], Registry());

        Assert.True(policy.IsAllowed(OpenAiKey));
        Assert.Empty(policy.UnresolvedProviderEntries);
    }

    // A blank entry is still something the tenant stored that no family claims. Treating it as absent would put
    // the fail-open back for a value written straight into the column.
    [Fact]
    public void ABlankStoredEntryStillRestricts()
    {
        var policy = TenantProviderPolicy.FromStored(["   "], [], Registry());

        Assert.True(policy.IsRestricted);
        Assert.False(policy.IsAllowed(OpenAiKey));
    }

    // The entries a policy refuses everything on account of are published as a read-only list. A caller that
    // could empty that list would turn a policy permitting nothing into one permitting everything, which is the
    // fail-open the entries are kept for.
    [Fact]
    public void TheEntriesNoFamilyClaimsCannotBeEmptiedByAReader()
    {
        var policy = TenantProviderPolicy.FromStored(["Acme.Llm"], [], Registry());

        Assert.Throws<NotSupportedException>(() => ((ICollection<string>)policy.UnresolvedProviderEntries).Clear());

        Assert.True(policy.IsRestricted);
        Assert.False(policy.IsAllowed(OpenAiKey));
        Assert.Equal(["Acme.Llm"], policy.UnresolvedProviderEntries);
    }

    // The permitted families are published the same way, and emptying that list would turn a policy that permits
    // one family into one that permits none while still reading as restricted.
    [Fact]
    public void ThePermittedFamiliesCannotBeEmptiedByAReader()
    {
        var policy = TenantProviderPolicy.FromStored([OpenAiKey], [], Registry());

        Assert.Throws<NotSupportedException>(() => ((ICollection<string>)policy.AllowedKinds).Clear());

        Assert.True(policy.IsAllowed(OpenAiKey));
    }

    // The second question the same list answers. A declared pattern can be a leading-dot suffix, which is not a
    // URL, so asking it as a base URL would refuse every family that declares one.
    [Fact]
    public void NoStatedHostPolicyCoversEveryDeclaredPattern()
    {
        Assert.True(TenantProviderPolicy.Unrestricted.IsPatternAllowed(".openai.azure.com"));
        Assert.True(TenantProviderPolicy.Unrestricted.IsPatternAllowed("api.openai.com"));
    }

    [Fact]
    public void AnEntryCoversThePatternItNames()
    {
        var policy = new TenantProviderPolicy([], ["api.openai.com", ".openai.azure.com"]);

        Assert.True(policy.IsPatternAllowed("api.openai.com"));
        Assert.True(policy.IsPatternAllowed(".openai.azure.com"));
        Assert.False(policy.IsPatternAllowed("api.anthropic.com"));
    }

    // The direction the whole check rests on: an entry has to name at least everything the pattern admits.
    [Fact]
    public void ASuffixEntryCoversAHostUnderItAndABareEntryCoversNoSuffix()
    {
        Assert.True(new TenantProviderPolicy([], [".example.com"]).IsPatternAllowed("api.example.com"));
        Assert.True(new TenantProviderPolicy([], [".example.com"]).IsPatternAllowed("example.com"));
        Assert.True(new TenantProviderPolicy([], [".example.com"]).IsPatternAllowed(".api.example.com"));
        Assert.False(new TenantProviderPolicy([], ["api.example.com"]).IsPatternAllowed(".example.com"));
        Assert.False(new TenantProviderPolicy([], [".example.com"]).IsPatternAllowed("notexample.com"));
    }

    [Fact]
    public void PatternMatchingIgnoresCaseOnBothSides()
    {
        Assert.True(new TenantProviderPolicy([], [".Example.COM"]).IsPatternAllowed("API.Example.com"));
    }

    // Every member of the set has to pass. Any-passes would let one permitted host carry the rest of a family's
    // reach in with it.
    [Fact]
    public void ATenantPermittingEveryMemberPermitsTheConnection()
    {
        var policy = new TenantProviderPolicy([], ["api.vendor.example", "auth.vendor.example"]);

        Assert.Null(policy.DescribeReachRefusal("https://api.vendor.example/v1", ["api.vendor.example", "auth.vendor.example"]));
    }

    [Fact]
    public void ATenantPermittingAllButOneDeclaredPatternRefusesItAndNamesThatPattern()
    {
        var policy = new TenantProviderPolicy([], ["api.vendor.example"]);

        var refusal = policy.DescribeReachRefusal("https://api.vendor.example/v1", ["api.vendor.example", "auth.vendor.example"]);

        Assert.NotNull(refusal);
        Assert.Contains("auth.vendor.example", refusal, StringComparison.Ordinal);
        Assert.Contains("permitted endpoint list", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ATenantPermittingEveryPatternButNotTheBaseUrlRefusesItAndNamesTheBaseUrl()
    {
        var policy = new TenantProviderPolicy([], ["api.vendor.example"]);

        var refusal = policy.DescribeReachRefusal("https://proxy.example.net/v1", ["api.vendor.example"]);

        Assert.NotNull(refusal);
        Assert.Contains("proxy.example.net", refusal, StringComparison.Ordinal);
    }

    // The case the declared patterns exist for: a family whose endpoint is fixed by its vendor carries no base
    // URL, and without them the restriction would have nothing to read for it.
    [Fact]
    public void AConnectionWithNoBaseUrlIsStillCheckedAgainstTheDeclaredPatterns()
    {
        var policy = new TenantProviderPolicy([], ["api.vendor.example"]);

        Assert.NotNull(policy.DescribeReachRefusal(null, ["auth.vendor.example"]));
        Assert.Null(policy.DescribeReachRefusal(null, ["api.vendor.example"]));
    }

    // A connection naming no destination, whose family names none either, is refused by a tenant that stated
    // where its traffic may go. Leaving it to another check would mean a restricted tenant permitting traffic
    // it can say nothing about, and before the union such a connection was refused outright. What the union
    // relaxes is the case of a family reaching a fixed vendor host, and that family names the host.
    [Fact]
    public void AConnectionNamingNoDestinationIsRefusedByARestrictedTenant()
    {
        var policy = new TenantProviderPolicy([], ["api.vendor.example"]);

        Assert.NotNull(policy.DescribeReachRefusal(null, []));
        Assert.NotNull(policy.DescribeReachRefusal(string.Empty, null));
        Assert.Null(TenantProviderPolicy.Unrestricted.DescribeReachRefusal(null, []));
    }

    [Fact]
    public void ATenantWithNoEndpointRestrictionPermitsEveryFamily()
    {
        Assert.Null(
            TenantProviderPolicy.Unrestricted.DescribeReachRefusal(
                "https://anywhere.example/v1",
                [".openai.azure.com", "auth.vendor.example"]));
    }

    // The host list is published the same way and lifts the endpoint leg if it is emptied.
    [Fact]
    public void ThePermittedHostsCannotBeEmptiedByAReader()
    {
        var policy = new TenantProviderPolicy([], ["api.openai.com"]);

        Assert.Throws<NotSupportedException>(() => ((ICollection<string>)policy.AllowedEndpointHosts).Clear());

        Assert.True(policy.RestrictsEndpoints);
        Assert.False(policy.IsEndpointAllowed("https://llm.example.com/v1"));
    }

    // A restricted tenant said where its clients may send traffic. A connection naming no endpoint, whose
    // family names no host either, cannot be measured against that list at all — and the relaxation this union
    // exists for is a family that reaches a fixed vendor host, which names it.
    [Fact]
    public void AConnectionThatNamesNoEndpointAndAFamilyThatNamesNoHostIsRefusedByARestrictedTenant()
    {
        var policy = TenantProviderPolicy.FromStored([], ["api.example.com"], Registry());

        var refusal = policy.DescribeReachRefusal(null, []);

        Assert.NotNull(refusal);
        Assert.Contains("names no endpoint", refusal, StringComparison.Ordinal);
    }

    // The same connection under a tenant that stated no endpoint restriction is not this check's business.
    [Fact]
    public void AConnectionThatNamesNoEndpointIsUnaffectedWhereTheTenantRestrictsNoEndpoint()
    {
        Assert.Null(TenantProviderPolicy.Unrestricted.DescribeReachRefusal(null, []));
    }

    // The case the union exists for still passes: the family names where it goes, and it is inside the list.
    [Fact]
    public void AFamilyThatNamesTheHostItReachesIsAllowedWithNoBaseUrl()
    {
        var policy = TenantProviderPolicy.FromStored([], [".googleapis.com"], Registry());

        Assert.Null(policy.DescribeReachRefusal(null, [".googleapis.com"]));
    }

    // Two families, declared the way a family loaded from a directory declares itself: a key of its own, and for
    // one of them the spelling it supersedes.
    private static IAiProviderDriverRegistry Registry()
    {
        return new AiProviderRegistry(
        [
            Driver(OpenAiKey, "OpenAI", [SupersededKey]),
            Driver(AnthropicKey, "Anthropic", []),
        ]);
    }

    private static IAiProviderDriver Driver(string key, string label, IReadOnlyList<string> supersededKeys)
    {
        var declaration = new ProviderDeclaration
        {
            Key = key,
            Label = label,
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            LegacyNames = ProviderLegacyNames.FromUnqualifiedNames(
                supersededKeys,
                [ProviderVocabulary.Compose(key, "ApiKey")],
                [ProviderDeclaredProtocolModes.Auto]),
            AuthModes = [new ProviderDeclaredAuthMode(ProviderVocabulary.Compose(key, "ApiKey"), [AiCredentialFieldSupport.ApiKey])],
            ProtocolModes = new ProviderDeclaredProtocolModes([ProviderDeclaredProtocolModes.Auto]),
            ConformanceInputs = new ProviderConformanceInputs(ProviderVocabulary.Compose(key, "ApiKey")),
        };

        // The three projections of the declaration are stubbed as well as the declaration itself: a substitute
        // does not run the contract's default implementations, and the registry refuses a driver that answers
        // none.
        var driver = Substitute.For<IAiProviderDriver>();
        driver.Declaration.Returns(declaration);
        driver.SupportedAuthModes.Returns(declaration.SupportedAuthModes);
        driver.SupportedProtocolModes.Returns(declaration.ProtocolModes.Supported);
        driver.CredentialFields.Returns(declaration.CredentialFields);

        return driver;
    }
}
