using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Interfaces;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.AzureDevOps;
using MeisterProPR.Infrastructure.Configuration;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.DependencyInjection;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // In-memory storage
        services.AddSingleton<IJobRepository, InMemoryJobRepository>();

        // Client authentication
        services.AddSingleton<IClientRegistry, EnvVarClientRegistry>();

        // ADO token validation (identity verification only).
        // Set ADO_SKIP_TOKEN_VALIDATION=true in user secrets to bypass the real
        // VSS endpoint during local development / testbed usage.
        if (configuration.GetValue<bool>("ADO_SKIP_TOKEN_VALIDATION"))
        {
            services.AddSingleton<IAdoTokenValidator, PassThroughAdoTokenValidator>();
        }
        else
        {
            services.AddHttpClient("AdoTokenValidator");
            services.AddSingleton<IAdoTokenValidator, AdoTokenValidator>();
        }

        // ADO operations
        // Set ADO_STUB_PR=true in user secrets to use a fake PR and skip ADO comment posting
        // during local development. The real AI endpoint is still called.
        if (configuration.GetValue<bool>("ADO_STUB_PR"))
        {
            services.AddScoped<IPullRequestFetcher, StubPullRequestFetcher>();
            services.AddScoped<IAdoCommentPoster, NoOpAdoCommentPoster>();
        }
        else
        {
            services.AddSingleton<VssConnectionFactory>(_ =>
                new VssConnectionFactory(ResolveCredential(configuration)));
            services.AddScoped<IPullRequestFetcher, AdoPullRequestFetcher>();
            services.AddScoped<IAdoCommentPoster, AdoCommentPoster>();
        }

        // AI review (provider-agnostic via IChatClient)
        var aiEndpoint = configuration["AI_ENDPOINT"]
                         ?? throw new InvalidOperationException("AI_ENDPOINT environment variable is not set.");
        var aiDeployment = configuration["AI_DEPLOYMENT"]
                           ?? throw new InvalidOperationException("AI_DEPLOYMENT environment variable is not set.");

        services.AddSingleton<IChatClient>(_ => CreateChatClient(aiEndpoint, aiDeployment, configuration["AI_API_KEY"]));

        services.AddSingleton<IAiReviewCore, AgentAiReviewCore>();

        return services;
    }

    /// <summary>
    ///     Resolves an Azure credential from configuration. Uses <see cref="ClientSecretCredential"/>
    ///     when AZURE_CLIENT_ID / AZURE_TENANT_ID / AZURE_CLIENT_SECRET are present in configuration
    ///     (e.g. user secrets), otherwise falls back to <see cref="DefaultAzureCredential"/> which
    ///     picks up Azure CLI login, managed identity, etc.
    /// </summary>
    private static Azure.Core.TokenCredential ResolveCredential(IConfiguration configuration)
    {
        var clientId = configuration["AZURE_CLIENT_ID"];
        var tenantId = configuration["AZURE_TENANT_ID"];
        var clientSecret = configuration["AZURE_CLIENT_SECRET"];

        if (!string.IsNullOrWhiteSpace(clientId) &&
            !string.IsNullOrWhiteSpace(tenantId) &&
            !string.IsNullOrWhiteSpace(clientSecret))
        {
            return new ClientSecretCredential(tenantId, clientId, clientSecret);
        }

        return new DefaultAzureCredential();
    }

    /// <summary>
    ///     Creates an <see cref="IChatClient" /> backed by the Azure OpenAI <b>Responses API</b>,
    ///     which supports reasoning models, tool use, and multi-turn state.
    ///     Both <c>*.openai.azure.com</c> and <c>*.services.ai.azure.com</c> (Azure AI Foundry)
    ///     are supported via <see cref="AzureOpenAIClient" />. For AI Foundry endpoints any
    ///     project path is stripped — <see cref="AzureOpenAIClient" /> constructs the correct
    ///     <c>/openai/responses</c> sub-path from the resource root automatically.
    /// </summary>
    private static IChatClient CreateChatClient(string endpoint, string deployment, string? apiKey)
    {
        var uri = new Uri(endpoint);

        // Azure AI Foundry portal URLs include a project path (.../api/projects/{project})
        // that is not part of the Azure OpenAI API surface — use only the resource root.
        if (uri.Host.EndsWith("services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            uri = new Uri($"{uri.Scheme}://{uri.Host}/");
        }

        var azureClient = string.IsNullOrWhiteSpace(apiKey)
            ? new AzureOpenAIClient(uri, new DefaultAzureCredential())
            : new AzureOpenAIClient(uri, new ApiKeyCredential(apiKey));

        // GetResponsesClient targets the Responses API endpoint instead of the
        // legacy Chat Completions endpoint, enabling reasoning and tool use.
        return azureClient.GetResponsesClient(deployment).AsIChatClient();
    }
}