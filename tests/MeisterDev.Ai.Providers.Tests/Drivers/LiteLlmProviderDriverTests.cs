// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.Ai.Providers.Tests.Drivers;

/// <summary>
///     Covers the LiteLLM profile: an operator-hosted gateway, so its address is whatever the operator runs it at
///     and the host is not pinned to anything.
/// </summary>
public sealed class LiteLlmProviderDriverTests
{
    [Theory]
    [InlineData("https://gateway.example.com/v1")]
    [InlineData("https://litellm.internal.example.com/v1")]
    [InlineData("https://models.corp.example.org/openai/v1")]
    public void AnyPublicGatewayAddressIsAccepted(string baseUrl)
    {
        Assert.Null(Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, AiAuthMode.ApiKey, HasApiKey: true)));
    }

    // Unlike plain OpenAI, an Azure host behind the gateway is the gateway's business: LiteLLM routes onward, so
    // what it fronts says nothing about how ProPR should authenticate to it.
    [Fact]
    public void AnAzureHostIsNotRefused()
    {
        var target = new AiProbeTarget("https://contoso.openai.azure.com/", AiAuthMode.ApiKey, HasApiKey: true);

        Assert.Null(Driver().ValidateProbeTarget(target));
    }

    [Fact]
    public void MissingKeyIsRefused()
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget("https://gateway.example.com/v1", AiAuthMode.ApiKey, HasApiKey: false));

        Assert.NotNull(refusal);
        Assert.Contains("API key", refusal, StringComparison.OrdinalIgnoreCase);
    }

    // A gateway is the case most likely to be reached on a private network, so this is the driver where the opt-in
    // matters most - and where it must still be an opt-in rather than the default.
    [Theory]
    [InlineData("https://127.0.0.1:4000/v1")]
    [InlineData("https://10.0.0.5:4000/v1")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    public void APrivateAddressIsRefusedByDefaultAndPermittedByTheOptIn(string baseUrl)
    {
        var target = new AiProbeTarget(baseUrl, AiAuthMode.ApiKey, HasApiKey: true);

        Assert.NotNull(Driver().ValidateProbeTarget(target));
        Assert.Null(Driver(allowPrivateEgress: true).ValidateProbeTarget(target));
    }

    [Fact]
    public void ThePrivateEgressOptInDoesNotRelaxTheScheme()
    {
        var target = new AiProbeTarget("http://10.0.0.5:4000/v1", AiAuthMode.ApiKey, HasApiKey: true);

        var refusal = Driver(allowPrivateEgress: true).ValidateProbeTarget(target);

        Assert.NotNull(refusal);
        Assert.Contains("https", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDriverAnswersForItsOwnProviderKind()
    {
        Assert.Equal(AiProviderKind.LiteLlm, Driver().ProviderKind);
    }

    private static LiteLlmProviderDriver Driver(bool allowPrivateEgress = false, bool allowInsecureScheme = false)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("AiProviderAdmin");
        services.AddSingleton<OpenAiCompatibleRequestFactory>();
        services.AddSingleton<OpenAiCompatibleTransport>();
        var provider = services.BuildServiceProvider();

        return new LiteLlmProviderDriver(
            provider.GetRequiredService<OpenAiCompatibleTransport>(),
            provider.GetRequiredService<IHttpClientFactory>(),
            allowPrivateEgress,
            allowInsecureScheme);
    }
}
