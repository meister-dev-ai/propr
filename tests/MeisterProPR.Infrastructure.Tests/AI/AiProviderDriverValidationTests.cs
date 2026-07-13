// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI.OpenAiCompatible;
using MeisterProPR.Infrastructure.AI.Providers.AzureOpenAi;
using MeisterProPR.Infrastructure.AI.Providers.LiteLlm;
using MeisterProPR.Infrastructure.AI.Providers.OpenAi;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Provider-specific probe-target validation now lives behind each <c>IAiProviderDriver</c> (moved out of the
///     controller). These tests exercise that seam directly.
/// </summary>
public sealed class AiProviderDriverValidationTests
{
    [Fact]
    public void Azure_NonAzureHost_Rejected()
    {
        var error = new AzureOpenAiProviderDriver().ValidateProbeTarget(new AiProbeTarget("https://internal.corp.example/", AiAuthMode.ApiKey, true));

        Assert.Contains("Azure AI host", error);
    }

    [Fact]
    public void Azure_AzureHostWithApiKey_Accepted()
    {
        var error = new AzureOpenAiProviderDriver().ValidateProbeTarget(new AiProbeTarget("https://x.openai.azure.com/", AiAuthMode.ApiKey, true));

        Assert.Null(error);
    }

    [Fact]
    public void Azure_AzureIdentityWithoutKey_Accepted()
    {
        var error = new AzureOpenAiProviderDriver().ValidateProbeTarget(new AiProbeTarget("https://x.openai.azure.com/", AiAuthMode.AzureIdentity, false));

        Assert.Null(error);
    }

    [Fact]
    public void OpenAi_AzureHost_RejectedWithProviderKindHint()
    {
        var error = CreateOpenAiDriver(isDevelopment: false).ValidateProbeTarget(new AiProbeTarget("https://x.openai.azure.com/", AiAuthMode.ApiKey, true));

        Assert.Contains("azureOpenAi", error);
    }

    [Fact]
    public void OpenAi_HttpOutsideDevelopment_RejectsScheme()
    {
        var error = CreateOpenAiDriver(isDevelopment: false).ValidateProbeTarget(new AiProbeTarget("http://api.example.com/v1", AiAuthMode.ApiKey, true));

        Assert.Contains("https", error);
    }

    [Fact]
    public void OpenAi_LiteralMetadataIp_Rejected()
    {
        var error = CreateOpenAiDriver(isDevelopment: false).ValidateProbeTarget(new AiProbeTarget("https://169.254.169.254/", AiAuthMode.ApiKey, true));

        Assert.Contains("private", error);
    }

    [Fact]
    public void OpenAi_PublicHttpsWithKey_Accepted()
    {
        var error = CreateOpenAiDriver(isDevelopment: false).ValidateProbeTarget(new AiProbeTarget("https://api.openai.com/v1", AiAuthMode.ApiKey, true));

        Assert.Null(error);
    }

    [Fact]
    public void OpenAi_MissingApiKey_Rejected()
    {
        var error = CreateOpenAiDriver(isDevelopment: false).ValidateProbeTarget(new AiProbeTarget("https://api.openai.com/v1", AiAuthMode.ApiKey, false));

        Assert.Contains("API key", error);
    }

    [Fact]
    public void OpenAi_HttpInDevelopment_Allowed()
    {
        var error = CreateOpenAiDriver(isDevelopment: true).ValidateProbeTarget(new AiProbeTarget("http://localhost:1234/v1", AiAuthMode.ApiKey, true));

        Assert.Null(error);
    }

    [Fact]
    public void LiteLlm_AzureHost_Allowed()
    {
        // LiteLLM is a generic OpenAI-compatible proxy, so an Azure host is not rejected (unlike plain OpenAI).
        var error = CreateLiteLlmDriver(isDevelopment: false).ValidateProbeTarget(new AiProbeTarget("https://x.openai.azure.com/", AiAuthMode.ApiKey, true));

        Assert.Null(error);
    }

    private static OpenAiProviderDriver CreateOpenAiDriver(bool isDevelopment)
    {
        return new OpenAiProviderDriver(CreateTransport(), CreateHttpClientFactory(), allowPrivateEgress: isDevelopment);
    }

    private static LiteLlmProviderDriver CreateLiteLlmDriver(bool isDevelopment)
    {
        return new LiteLlmProviderDriver(CreateTransport(), CreateHttpClientFactory(), allowPrivateEgress: isDevelopment);
    }

    private static OpenAiCompatibleTransport CreateTransport()
    {
        return new OpenAiCompatibleTransport(CreateHttpClientFactory(), new OpenAiCompatibleRequestFactory());
    }

    private static IHttpClientFactory CreateHttpClientFactory()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }
}
