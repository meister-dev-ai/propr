// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Tests.Drivers;

/// <summary>
///     Covers the Azure OpenAI profile: the host is pinned to Azure's own AI hosts, and a managed identity is a
///     complete credential on its own.
/// </summary>
/// <remarks>
///     This driver checks the scheme itself rather than through the shared egress policy, because the Azure SDK
///     brings its own transport and so never passes through the connect-time egress guard the other providers are
///     held to. That makes these assertions the only thing standing between a stored profile and a plaintext call,
///     which is why they are pinned here rather than left to the shared conformance suite.
/// </remarks>
public sealed class AzureOpenAiProviderDriverTests
{
    [Theory]
    [InlineData("https://contoso.openai.azure.com/")]
    [InlineData("https://contoso.services.ai.azure.com/")]
    [InlineData("https://contoso.cognitiveservices.azure.com/")]
    public void EachAzureAiHostShapeIsAccepted(string baseUrl)
    {
        Assert.Null(Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, AiAuthMode.ApiKey, HasApiKey: true)));
    }

    // A vendor URL under this provider kind would authenticate the Azure way against an endpoint that does not
    // speak it, so it is refused here and pointed at the provider kind that fits.
    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://gateway.example.com/v1")]
    [InlineData("https://contoso.openai.azure.com.evil.example/")]
    public void AHostThatIsNotAzuresIsRefused(string baseUrl)
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, AiAuthMode.ApiKey, HasApiKey: true));

        Assert.NotNull(refusal);
        Assert.Contains("Azure AI host", refusal, StringComparison.Ordinal);
    }

    // Managed identity is the point of this mode: there is no key to store, so requiring one would make the
    // keyless deployment unconfigurable.
    [Fact]
    public void AManagedIdentityIsAcceptedWithNoKeyStored()
    {
        var target = new AiProbeTarget("https://contoso.openai.azure.com/", AiAuthMode.AzureIdentity, HasApiKey: false);

        Assert.Null(Driver().ValidateProbeTarget(target));
    }

    [Fact]
    public void AKeyModeWithNoKeyIsRefusedAndNamesBothWaysIn()
    {
        var target = new AiProbeTarget("https://contoso.openai.azure.com/", AiAuthMode.ApiKey, HasApiKey: false);

        var refusal = Driver().ValidateProbeTarget(target);

        Assert.NotNull(refusal);
        Assert.Contains("API key or Azure identity", refusal, StringComparison.Ordinal);
    }

    // The SDK's transport bypasses the connect-time egress guard, so plain http has to be refused right here or
    // the credential goes out in the clear.
    [Fact]
    public void PlainHttpIsRefusedEvenOnAnAzureHost()
    {
        var target = new AiProbeTarget("http://contoso.openai.azure.com/", AiAuthMode.ApiKey, HasApiKey: true);

        var refusal = Driver().ValidateProbeTarget(target);

        Assert.NotNull(refusal);
        Assert.Contains("https", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void SomethingThatIsNotAUrlIsRefusedAsSuch()
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget("contoso.openai.azure.com", AiAuthMode.ApiKey, HasApiKey: true));

        Assert.NotNull(refusal);
        Assert.Contains("absolute URL", refusal, StringComparison.Ordinal);
    }

    private static AzureOpenAiProviderDriver Driver()
    {
        return new AzureOpenAiProviderDriver();
    }
}
