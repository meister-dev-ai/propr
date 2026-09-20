// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.AzureOpenAiAddIn.Tests;

/// <summary>
///     Covers the Azure OpenAI profile: the host is pinned to Azure's own AI hosts, and a managed identity is a
///     complete credential on its own.
/// </summary>
/// <remarks>
///     The host pin is this family's own rule and cannot be asserted by the shared conformance checks, which
///     know only what every family owes its caller. It is a control over the credential rather than over the
///     address: a profile configured for this family authenticates with a resource key or a Microsoft Entra
///     token, and one pointed elsewhere would send that credential to a host Microsoft does not control.
///     Refusing at configuration time also tells the operator what is wrong, where the connect-time address
///     check can only refuse the call once a review makes it.
/// </remarks>
public sealed class AzureOpenAiProviderDriverTests
{
    [Theory]
    [InlineData("https://contoso.openai.azure.com/")]
    [InlineData("https://contoso.services.ai.azure.com/")]
    [InlineData("https://contoso.cognitiveservices.azure.com/")]
    public void EachAzureAiHostShapeIsAccepted(string baseUrl)
    {
        Assert.Null(Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, AzureOpenAiProviderDriver.ApiKeyAuth, HasApiKey: true)));
    }

    // A resource name the product has never seen is accepted, which is why the declared hosts are suffixes: no
    // family can enumerate a tenant's resource names.
    [Fact]
    public void AResourceNameThisProductHasNeverSeenIsAccepted()
    {
        var target = new AiProbeTarget("https://a-brand-new-resource.openai.azure.com/", AzureOpenAiProviderDriver.ApiKeyAuth, HasApiKey: true);

        Assert.Null(Driver().ValidateProbeTarget(target));
    }

    // A vendor URL under this family would send an Azure credential to an endpoint that does not read it, so it
    // is refused here and pointed at the family that fits. The third case is the one a bare string comparison
    // gets wrong: a declared suffix in the middle of a host is not that host.
    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://gateway.example.com/v1")]
    [InlineData("https://contoso.openai.azure.com.evil.example/")]
    public void AHostThatIsNotAzuresIsRefused(string baseUrl)
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, AzureOpenAiProviderDriver.ApiKeyAuth, HasApiKey: true));

        Assert.NotNull(refusal);
        Assert.Contains("Azure AI host", refusal, StringComparison.Ordinal);
    }

    // The pin holds however the installation set its egress posture: an Azure AI host is public and always
    // served over https, so neither relaxation widens what this family accepts.
    [Fact]
    public void TheHostPinHoldsOnAnInstallationThatOptedIntoPrivateAddresses()
    {
        var target = new AiProbeTarget("https://10.0.0.5/", AzureOpenAiProviderDriver.ApiKeyAuth, HasApiKey: true)
        {
            AllowsPrivateAddress = true,
            AllowsInsecureScheme = true,
        };

        Assert.NotNull(Driver().ValidateProbeTarget(target));
    }

    // Managed identity is the point of this mode: there is no key to store, so requiring one would make the
    // keyless deployment unconfigurable.
    [Fact]
    public void AManagedIdentityIsAcceptedWithNoKeyStored()
    {
        var target = new AiProbeTarget("https://contoso.openai.azure.com/", AzureOpenAiProviderDriver.AzureIdentityAuth, HasApiKey: false);

        Assert.Null(Driver().ValidateProbeTarget(target));
    }

    [Fact]
    public void AKeyModeWithNoKeyIsRefusedAndNamesBothWaysIn()
    {
        var target = new AiProbeTarget("https://contoso.openai.azure.com/", AzureOpenAiProviderDriver.ApiKeyAuth, HasApiKey: false);

        var refusal = Driver().ValidateProbeTarget(target);

        Assert.NotNull(refusal);
        Assert.Contains("API key or Azure identity", refusal, StringComparison.Ordinal);
    }

    // An Azure AI host is always served over https, so a stored profile naming plain http would send the
    // credential in the clear on its first call.
    [Fact]
    public void PlainHttpIsRefusedEvenOnAnAzureHost()
    {
        var target = new AiProbeTarget("http://contoso.openai.azure.com/", AzureOpenAiProviderDriver.ApiKeyAuth, HasApiKey: true);

        var refusal = Driver().ValidateProbeTarget(target);

        Assert.NotNull(refusal);
        Assert.Contains("https", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void SomethingThatIsNotAUrlIsRefusedAsSuch()
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget("contoso.openai.azure.com", AzureOpenAiProviderDriver.ApiKeyAuth, HasApiKey: true));

        Assert.NotNull(refusal);
        Assert.Contains("absolute URL", refusal, StringComparison.Ordinal);
    }

    // The hosts this family declares it reaches are the hosts it enforces. A tenant's endpoint restriction and
    // the plugin inventory are both read against the declaration, so a declaration that disagreed with the rule
    // would show an operator a list the family does not keep to.
    [Fact]
    public void TheDeclaredHostsAreTheHostsTheFamilyAccepts()
    {
        var driver = Driver();

        Assert.All(
            driver.Declaration.ReachedHostPatterns,
            suffix => Assert.Null(
                driver.ValidateProbeTarget(new AiProbeTarget($"https://contoso{suffix}/", AzureOpenAiProviderDriver.ApiKeyAuth, HasApiKey: true))));
    }

    // A family reaches the network only through the client factory the host supplies. Where the host supplied
    // none, the client refuses instead of falling back to a transport of the library's own, which would work in
    // every functional test while silently losing the connect-time address check.
    [Fact]
    public async Task AClientBuiltWithNoHostTransportRefusesRatherThanReachingTheNetworkItself()
    {
        var endpoint = new ProviderEndpoint(
            "meisterdev/azureOpenAi",
            "https://contoso.openai.azure.com/",
            AzureOpenAiProviderDriver.ApiKeyAuth,
            "azure-resource-key");
        var model = new ProviderModelDescriptor(Guid.NewGuid(), "my-gpt-deployment", [AzureOpenAiProviderDriver.ChatCompletionsProtocol]);

        using var chat = Driver().CreateChatClient(endpoint, model, AzureOpenAiProviderDriver.ChatCompletionsProtocol);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chat.GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hello")]));
    }

    // What the runtime stages may do with a call of this family depends on the wire shape it is made on: the
    // Responses surface is what carries a provider-held session, a background response and a resumed
    // conversation, and the older completions surface carries none of them.
    [Theory]
    [InlineData(AzureOpenAiProviderDriver.ResponsesProtocol, true)]
    [InlineData(ProviderDeclaredProtocolModes.Auto, true)]
    [InlineData(AzureOpenAiProviderDriver.ChatCompletionsProtocol, false)]
    public void WhatACallCanDoFollowsTheWireShapeItIsMadeOn(string protocolMode, bool usesResponses)
    {
        var endpoint = new ProviderEndpoint(
            "meisterdev/azureOpenAi",
            "https://contoso.openai.azure.com/",
            AzureOpenAiProviderDriver.ApiKeyAuth,
            "azure-resource-key");
        var model = new ProviderModelDescriptor(Guid.NewGuid(), "gpt-5.4-mini", [protocolMode]);

        var capabilities = Driver().GetChatRuntimeCapabilities(endpoint, model, protocolMode);

        Assert.Equal(
            new ProviderRuntimeCapabilities(usesResponses, usesResponses, usesResponses, usesResponses, true, true),
            capabilities);
    }

    private static AzureOpenAiProviderDriver Driver()
    {
        return new AzureOpenAiProviderDriver();
    }
}
