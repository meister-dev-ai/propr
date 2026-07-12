// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI.OpenAiCompatible;
using MeisterProPR.Infrastructure.AI.Providers.AzureOpenAi;
using MeisterProPR.Infrastructure.AI.Providers.LiteLlm;
using MeisterProPR.Infrastructure.AI.Providers.OpenAi;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class ProviderRuntimeCapabilityTests
{
    [Theory]
    [InlineData(AiProtocolMode.Responses, true, true, true, true, true, true)]
    [InlineData(AiProtocolMode.Auto, true, true, true, true, true, true)]
    [InlineData(AiProtocolMode.ChatCompletions, false, false, false, false, true, true)]
    public void AzureOpenAiProviderDriver_GetChatRuntimeCapabilities_TracksManagedSessionEligibility(
        AiProtocolMode protocolMode,
        bool supportsProviderManagedSessions,
        bool supportsManagedRemoteConversation,
        bool supportsBackgroundResponses,
        bool prefersResponsesApi,
        bool supportsPromptCaching,
        bool supportsPromptCacheRouting)
    {
        var driver = new AzureOpenAiProviderDriver();
        var model = AiConnectionTestFactory.CreateChatModel("gpt-5.4-mini");
        var binding = AiConnectionTestFactory.CreateBinding(AiPurpose.ReviewDefault, model, protocolMode);
        var connection = AiConnectionTestFactory.CreateConnection(Guid.NewGuid(), [model], [binding]);

        var capabilities = driver.GetChatRuntimeCapabilities(connection, model, binding);

        Assert.Equal(
            new AgentReviewRuntimeCapabilities(
                supportsProviderManagedSessions,
                supportsManagedRemoteConversation,
                supportsBackgroundResponses,
                prefersResponsesApi,
                supportsPromptCaching,
                supportsPromptCacheRouting),
            capabilities);
    }

    [Theory]
    [InlineData(AiProtocolMode.Responses, true, true, true, true)]
    [InlineData(AiProtocolMode.Auto, true, true, true, true)]
    [InlineData(AiProtocolMode.ChatCompletions, false, false, false, false)]
    public void OpenAiProviderDriver_GetChatRuntimeCapabilities_TracksManagedSessionEligibility(
        AiProtocolMode protocolMode,
        bool supportsProviderManagedSessions,
        bool supportsManagedRemoteConversation,
        bool supportsBackgroundResponses,
        bool prefersResponsesApi)
    {
        var driver = new OpenAiProviderDriver(
            CreateTransport(
                HttpStatusCode.OK, """
                                   {"data":[{"id":"gpt-5.4-mini"}]}
                                   """),
            CreateHttpClientFactory());
        var model = AiConnectionTestFactory.CreateChatModel("gpt-5.4-mini");
        var binding = AiConnectionTestFactory.CreateBinding(AiPurpose.ReviewDefault, model, protocolMode);
        var connection = AiConnectionTestFactory.CreateConnection(
                Guid.NewGuid(),
                [model with { SupportedProtocolModes = protocolMode == AiProtocolMode.Auto ? [AiProtocolMode.Responses] : model.SupportedProtocolModes }],
                [binding]) with
            {
                ProviderKind = AiProviderKind.OpenAi,
            };

        var capabilities = driver.GetChatRuntimeCapabilities(connection, model, binding);

        Assert.Equal(
            new AgentReviewRuntimeCapabilities(
                supportsProviderManagedSessions,
                supportsManagedRemoteConversation,
                supportsBackgroundResponses,
                prefersResponsesApi),
            capabilities);
    }

    [Fact]
    public void LiteLlmProviderDriver_GetChatRuntimeCapabilities_RemainsExplicitlyNonManaged()
    {
        var driver = new LiteLlmProviderDriver(
            CreateTransport(
                HttpStatusCode.OK, """
                                   {"data":[{"id":"gpt-4o-mini"}]}
                                   """),
            CreateHttpClientFactory());
        var model = AiConnectionTestFactory.CreateChatModel("gpt-4o-mini");
        var binding = AiConnectionTestFactory.CreateBinding(AiPurpose.ReviewDefault, model, AiProtocolMode.Responses);
        var connection = AiConnectionTestFactory.CreateConnection(Guid.NewGuid(), [model], [binding]) with
        {
            ProviderKind = AiProviderKind.LiteLlm,
        };

        var capabilities = driver.GetChatRuntimeCapabilities(connection, model, binding);

        Assert.Equal(new AgentReviewRuntimeCapabilities(false, false, false, false), capabilities);
    }

    private static IHttpClientFactory CreateHttpClientFactory()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    private static OpenAiCompatibleTransport CreateTransport(HttpStatusCode statusCode, string payload)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("AiProviderAdmin")
            .ConfigurePrimaryHttpMessageHandler(() => new StubHttpMessageHandler(statusCode, payload));
        var provider = services.BuildServiceProvider();
        return new OpenAiCompatibleTransport(
            provider.GetRequiredService<IHttpClientFactory>(),
            new OpenAiCompatibleRequestFactory());
    }

    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode, string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(
                new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(payload),
                });
        }
    }
}
