// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.Ai.Providers.Tests.Drivers;

/// <summary>
///     Covers the OpenAI-compatible profile: that an operator-supplied base URL is accepted for the long tail of
///     compatible endpoints, and that the endpoint is still held to the egress policy — an operator-set URL is
///     exactly the input that must not be able to reach an internal address.
/// </summary>
public sealed class OpenAiCompatibleProviderDriverTests
{
    // Endpoints an operator is expected to configure without any code change: vendor APIs, an aggregator, and
    // self-hosted servers. This is the long tail the profile exists for.
    [Theory]
    [InlineData("https://api.deepseek.com/v1")]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1")]
    [InlineData("https://api.moonshot.cn/v1")]
    [InlineData("https://api.minimax.chat/v1")]
    [InlineData("https://openrouter.ai/api/v1")]
    [InlineData("https://api.x.ai/v1")]
    [InlineData("https://api.groq.com/openai/v1")]
    [InlineData("https://api.together.xyz/v1")]
    public void PublicCompatibleEndpointWithAnApiKey_IsAccepted(string baseUrl)
    {
        var driver = Driver();

        Assert.Null(driver.ValidateProbeTarget(new AiProbeTarget(baseUrl, AiAuthMode.ApiKey, HasApiKey: true)));
    }

    // Unlike plain OpenAI, an Azure host is not rejected: an operator may front an Azure deployment with a
    // compatible gateway, and this profile describes "whatever is at this URL".
    [Fact]
    public void AzureHost_IsNotRejected()
    {
        Assert.Null(Driver().ValidateProbeTarget(new AiProbeTarget("https://contoso.openai.azure.com/", AiAuthMode.ApiKey, HasApiKey: true)));
    }

    [Fact]
    public void MissingApiKey_IsRejected()
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget("https://api.deepseek.com/v1", AiAuthMode.ApiKey, HasApiKey: false));

        Assert.NotNull(refusal);
        Assert.Contains("API key", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://api.deepseek.com/v1")]
    [InlineData("http://localhost:11434/v1")]
    public void NonHttps_IsRejectedWhenTheInsecureSchemeIsNotPermitted(string baseUrl)
    {
        var refusal = Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, AiAuthMode.ApiKey, HasApiKey: true));

        Assert.NotNull(refusal);
        Assert.Contains("https", refusal, StringComparison.OrdinalIgnoreCase);
    }

    // A self-hosted Ollama or vLLM server is the reason the private-egress opt-in exists; without it an
    // operator-supplied internal address must be refused. Note these are IP LITERALS — see the test below for
    // why a hostname behaves differently.
    [Theory]
    [InlineData("https://127.0.0.1:8000/v1")]
    [InlineData("https://10.0.0.5:8000/v1")]
    [InlineData("https://192.168.1.10:11434/v1")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://[::1]:8000/v1")]
    public void PrivateOrLoopbackLiteral_IsRejectedByDefaultAndPermittedByTheOptIn(string baseUrl)
    {
        var target = new AiProbeTarget(baseUrl, AiAuthMode.ApiKey, HasApiKey: true);

        var refusal = Driver().ValidateProbeTarget(target);
        Assert.NotNull(refusal);
        Assert.Contains("private", refusal, StringComparison.OrdinalIgnoreCase);

        Assert.Null(Driver(allowPrivateEgress: true).ValidateProbeTarget(target));
    }

    // Egress protection is two-layered, and this URL-shape check is only the first layer: it inspects the host
    // as written, so it can reject an IP literal but cannot know where a NAME resolves. "localhost" therefore
    // passes here and is stopped at connect time instead, by the guarded handler that checks the resolved
    // address. The operator still learns at probe time, because the probe itself egresses through that handler.
    [Theory]
    [InlineData("https://localhost:11434/v1")]
    [InlineData("https://ollama.internal/v1")]
    public void PrivateHostnameIsNotRejectedByTheUrlShapeCheck_ItIsStoppedAtConnectTime(string baseUrl)
    {
        Assert.Null(Driver().ValidateProbeTarget(new AiProbeTarget(baseUrl, AiAuthMode.ApiKey, HasApiKey: true)));
    }

    // The opt-in that permits an on-prem address deliberately does not also relax the scheme.
    [Fact]
    public void PrivateEgressOptIn_DoesNotRelaxTheSchemeRequirement()
    {
        var refusal = Driver(allowPrivateEgress: true).ValidateProbeTarget(new AiProbeTarget("http://localhost:11434/v1", AiAuthMode.ApiKey, HasApiKey: true));

        Assert.NotNull(refusal);
        Assert.Contains("https", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DriverIsRegisteredForItsOwnKind()
    {
        Assert.Equal(AiProviderKind.OpenAiCompatible, Driver().ProviderKind);
    }

    private static OpenAiCompatibleProviderDriver Driver(bool allowPrivateEgress = false, bool allowInsecureScheme = false)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("AiProviderAdmin");
        services.AddSingleton<OpenAiCompatibleRequestFactory>();
        services.AddSingleton<OpenAiCompatibleTransport>();
        var provider = services.BuildServiceProvider();

        return new OpenAiCompatibleProviderDriver(
            provider.GetRequiredService<OpenAiCompatibleTransport>(),
            provider.GetRequiredService<IHttpClientFactory>(),
            allowPrivateEgress,
            allowInsecureScheme);
    }
}
