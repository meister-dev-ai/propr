// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class AiChatClientFactoryTests
{
    [Fact]
    public void CreateClient_AzureProviderWithNonAzureHost_Throws()
    {
        var factory = CreateFactory();

        Assert.Throws<ArgumentException>(() =>
            factory.CreateClient("https://internal.corp.example/", "api-key", AiProviderKind.AzureOpenAi));
    }

    [Fact]
    public void CreateClient_AzureProviderWithAzureHost_Succeeds()
    {
        var factory = CreateFactory();

        var client = factory.CreateClient("https://myorg.openai.azure.com/", "api-key", AiProviderKind.AzureOpenAi);

        Assert.NotNull(client);
    }

    [Fact]
    public void CreateClient_OpenAiProvider_Succeeds()
    {
        var factory = CreateFactory();

        var client = factory.CreateClient("https://api.openai.com/v1/", "api-key", AiProviderKind.OpenAi);

        Assert.NotNull(client);
    }

    private static AiChatClientFactory CreateFactory()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        var httpClientFactory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        return new AiChatClientFactory(NullLogger<AiChatClientFactory>.Instance, httpClientFactory);
    }
}
