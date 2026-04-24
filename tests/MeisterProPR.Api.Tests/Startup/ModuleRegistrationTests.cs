// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Azure.Core;
using MeisterProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Interfaces;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Clients;
using MeisterProPR.Infrastructure.Features.Crawling;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Features.Mentions;
using MeisterProPR.Infrastructure.Features.PromptCustomization;
using MeisterProPR.Infrastructure.Features.Reviewing;
using MeisterProPR.Infrastructure.Features.UsageReporting;
using MeisterProPR.ProCursor.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Startup;

public sealed class ModuleRegistrationTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    [Fact]
    public void Program_UsesModuleEntryPoints_ForFeatureRegistration()
    {
        var contents = File.ReadAllText(Path.Combine(RepoRoot, "src/MeisterProPR.Api/Program.cs"));

        Assert.Contains(
            "AddInfrastructureSupport(builder.Configuration, builder.Environment)",
            contents,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddReviewingModule(builder.Configuration, builder.Environment)",
            contents,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddCrawlingModule(builder.Configuration, builder.Environment)",
            contents,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddClientsModule(builder.Configuration, builder.Environment)",
            contents,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddIdentityAndAccessModule(builder.Configuration, builder.Environment)",
            contents,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddMentionsModule(builder.Configuration, builder.Environment)",
            contents,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddPromptCustomizationModule(builder.Configuration, builder.Environment)",
            contents,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddUsageReportingModule(builder.Configuration, builder.Environment)",
            contents,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddProCursorModule(builder.Configuration, builder.Environment)",
            contents,
            StringComparison.Ordinal);
        Assert.DoesNotContain("AddInfrastructure(builder.Configuration)", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void AzureDevOpsLegacyWebhookCompatibilitySurfaces_AreRemovedFromSourceTree()
    {
        Assert.False(
            File.Exists(
                Path.Combine(
                    RepoRoot,
                    "src/MeisterProPR.Api/Features/Crawling/Webhooks/Controllers/AdoWebhookReceiverController.cs")));
        Assert.False(
            Directory.Exists(
                Path.Combine(
                    RepoRoot,
                    "src/MeisterProPR.Application/Features/Crawling/Webhooks/Commands/HandleAdoWebhookDelivery")));
        Assert.False(File.Exists(Path.Combine(RepoRoot, "src/MeisterProPR.Application/Interfaces/IAdoCommentPoster.cs")));
        Assert.False(File.Exists(Path.Combine(RepoRoot, "src/MeisterProPR.Application/Interfaces/IAdoDiscoveryService.cs")));
        Assert.False(File.Exists(Path.Combine(RepoRoot, "src/MeisterProPR.Application/Interfaces/IAdoReviewerManager.cs")));
        Assert.False(File.Exists(Path.Combine(RepoRoot, "src/MeisterProPR.Application/Interfaces/IAdoThreadClient.cs")));
        Assert.False(File.Exists(Path.Combine(RepoRoot, "src/MeisterProPR.Application/Interfaces/IAdoThreadReplier.cs")));
        Assert.False(File.Exists(Path.Combine(RepoRoot, "src/MeisterProPR.Application/Interfaces/IAdoTokenValidator.cs")));
        Assert.False(
            File.Exists(
                Path.Combine(
                    RepoRoot,
                    "src/MeisterProPR.Application/Features/Crawling/Webhooks/Ports/IAdoWebhookBasicAuthVerifier.cs")));
        Assert.False(
            File.Exists(
                Path.Combine(
                    RepoRoot,
                    "src/MeisterProPR.Application/Features/Crawling/Webhooks/Ports/IAdoWebhookPayloadParser.cs")));
    }

    [Fact]
    public void ModuleRegistration_UsesFirstClassAzureDevOpsProviderExtension()
    {
        var infrastructureSupport = File.ReadAllText(
            Path.Combine(
                RepoRoot,
                "src/MeisterProPR.Infrastructure/DependencyInjection/InfrastructureServiceExtensions.cs"));
        var clientsModule = File.ReadAllText(
            Path.Combine(
                RepoRoot,
                "src/MeisterProPR.Infrastructure/Features/Clients/ClientsModuleServiceCollectionExtensions.cs"));
        var reviewingModule = File.ReadAllText(
            Path.Combine(
                RepoRoot,
                "src/MeisterProPR.Infrastructure/Features/Reviewing/ReviewingModuleServiceCollectionExtensions.cs"));
        var crawlingModule = File.ReadAllText(
            Path.Combine(
                RepoRoot,
                "src/MeisterProPR.Infrastructure/Features/Crawling/CrawlingModuleServiceCollectionExtensions.cs"));
        var proCursorModule = File.ReadAllText(
            Path.Combine(
                RepoRoot,
                "src/MeisterProPR.ProCursor/Infrastructure/DependencyInjection/ProCursorServiceCollectionExtensions.cs"));

        Assert.Contains("AddAzureDevOpsProviderAdapters()", clientsModule, StringComparison.Ordinal);
        Assert.Contains("AddAzureDevOpsProviderAdapters()", reviewingModule, StringComparison.Ordinal);
        Assert.Contains("AddAzureDevOpsProviderAdapters()", crawlingModule, StringComparison.Ordinal);
        Assert.Contains("AddAzureDevOpsInfrastructureServices(", infrastructureSupport, StringComparison.Ordinal);
        Assert.Contains("AddAzureDevOpsReviewingServices(", reviewingModule, StringComparison.Ordinal);
        Assert.Contains("AddAzureDevOpsCrawlingServices(", crawlingModule, StringComparison.Ordinal);
        Assert.Contains("AddAzureDevOpsProCursorServices(", proCursorModule, StringComparison.Ordinal);
        Assert.DoesNotContain("AdoCompatibility", clientsModule, StringComparison.Ordinal);
        Assert.DoesNotContain("AdoCompatibility", reviewingModule, StringComparison.Ordinal);
        Assert.DoesNotContain("AdoCompatibility", crawlingModule, StringComparison.Ordinal);
        Assert.DoesNotContain("IAdoTokenValidator", infrastructureSupport, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AddScoped<IAdoDiscoveryService, AdoDiscoveryService>",
            crawlingModule,
            StringComparison.Ordinal);
        Assert.DoesNotContain("IAdoReviewerManager", infrastructureSupport, StringComparison.Ordinal);
        Assert.DoesNotContain("IAdoThreadClient", infrastructureSupport, StringComparison.Ordinal);
        Assert.DoesNotContain("IAdoThreadReplier", infrastructureSupport, StringComparison.Ordinal);
        Assert.DoesNotContain("AddScoped<IReviewContextToolsFactory>(sp =>", reviewingModule, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AddScoped<IProCursorMaterializer, AdoRepositoryMaterializer>",
            proCursorModule,
            StringComparison.Ordinal);
        Assert.DoesNotContain("HandleAdoWebhookDeliveryHandler", crawlingModule, StringComparison.Ordinal);
    }

    [Fact]
    public void InfrastructureSupport_RegistersOnlySharedSupportServices()
    {
        var services = new ServiceCollection();

        services.AddInfrastructureSupport(CreateConfiguration(false));

        Assert.DoesNotContain(
            services,
            descriptor => string.Equals(descriptor.ServiceType.Name, "IAdoTokenValidator", StringComparison.Ordinal));
        Assert.NotNull(FindService<IAiChatClientFactory>(services));
        Assert.Null(FindService<IJobRepository>(services));
        Assert.Null(FindService<ICrawlConfigurationRepository>(services));
        Assert.Null(FindService<IWebhookConfigurationRepository>(services));
        Assert.Null(FindService<IWebhookDeliveryLogRepository>(services));
        Assert.Null(FindService<IClientRegistry>(services));
        Assert.Null(FindService<IUserRepository>(services));
        Assert.Null(FindService<IMentionScanRepository>(services));
        Assert.Null(FindService<IPromptOverrideRepository>(services));
        Assert.Null(FindService<IClientTokenUsageRepository>(services));
        Assert.Null(FindService<IProCursorTokenUsageRecorder>(services));
        Assert.Null(FindService<IWebhookSecretGenerator>(services));
    }

    [Fact]
    public void ComposedModules_RegisterFeatureOwnedServices()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(true);

        services.AddInfrastructureSupport(configuration);
        services.AddReviewingModule(configuration);
        services.AddCrawlingModule(configuration);
        services.AddClientsModule(configuration);
        services.AddIdentityAndAccessModule(configuration);
        services.AddMentionsModule(configuration);
        services.AddPromptCustomizationModule(configuration);
        services.AddUsageReportingModule(configuration);
        services.AddProCursorModule(configuration);

        Assert.NotNull(FindService<IJobRepository>(services));
        Assert.NotNull(FindService<ICrawlConfigurationRepository>(services));
        Assert.NotNull(FindService<IWebhookConfigurationRepository>(services));
        Assert.NotNull(FindService<IWebhookDeliveryLogRepository>(services));
        Assert.NotNull(FindService<IWebhookSecretGenerator>(services));
        Assert.NotNull(FindService<IClientRegistry>(services));
        Assert.NotNull(FindService<IUserRepository>(services));
        Assert.NotNull(FindService<IMentionScanRepository>(services));
        Assert.NotNull(FindService<IPromptOverrideRepository>(services));
        Assert.NotNull(FindService<IClientTokenUsageRepository>(services));
        Assert.NotNull(FindService<IProCursorTokenUsageRecorder>(services));
        Assert.NotNull(FindService<IMentionAnswerService>(services));
        Assert.NotNull(FindService<IPrCrawlService>(services));
        Assert.NotNull(FindService<IFileByFileReviewOrchestrator>(services));
        Assert.NotNull(FindService<IScmProviderRegistry>(services));
        Assert.NotNull(FindService<IRepositoryDiscoveryProvider>(services));
        Assert.NotNull(FindService<ICodeReviewQueryService>(services));
        Assert.NotNull(FindService<ICodeReviewPublicationService>(services));
        Assert.NotNull(FindService<IReviewDiscoveryProvider>(services));
        Assert.NotNull(FindService<IReviewerIdentityService>(services));
        Assert.NotNull(FindService<IWebhookIngressService>(services));
    }

    [Fact]
    public void ComposedModules_RegisterAzureDevOpsAsCompleteProviderInRegistry()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(true);

        services.AddDataProtection();
        services.AddSingleton(new VssConnectionFactory(Substitute.For<TokenCredential>()));
        services.AddInfrastructureSupport(configuration);
        services.AddReviewingModule(configuration);
        services.AddCrawlingModule(configuration);
        services.AddClientsModule(configuration);
        services.AddIdentityAndAccessModule(configuration);
        services.AddMentionsModule(configuration);
        services.AddPromptCustomizationModule(configuration);
        services.AddUsageReportingModule(configuration);
        services.AddProCursorModule(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IScmProviderRegistry>();

        Assert.True(registry.IsRegistered(ScmProvider.AzureDevOps));
    }

    [Fact]
    public void ComposedModules_TestingEnvironment_WithDatabaseConnectionString_StillRegistersDbServices()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(true);
        var environment = new TestHostEnvironment("Testing");

        services.AddInfrastructureSupport(configuration, environment);
        services.AddReviewingModule(configuration, environment);
        services.AddCrawlingModule(configuration, environment);
        services.AddClientsModule(configuration, environment);
        services.AddIdentityAndAccessModule(configuration, environment);
        services.AddMentionsModule(configuration, environment);
        services.AddPromptCustomizationModule(configuration, environment);
        services.AddUsageReportingModule(configuration, environment);
        services.AddProCursorModule(configuration, environment);

        Assert.NotNull(FindService<IJobRepository>(services));
        Assert.NotNull(FindService<ICrawlConfigurationRepository>(services));
        Assert.NotNull(FindService<IWebhookConfigurationRepository>(services));
        Assert.NotNull(FindService<IWebhookDeliveryLogRepository>(services));
        Assert.NotNull(FindService<IWebhookSecretGenerator>(services));
        Assert.NotNull(FindService<IClientRegistry>(services));
        Assert.NotNull(FindService<IUserRepository>(services));
        Assert.NotNull(FindService<IMentionScanRepository>(services));
        Assert.NotNull(FindService<IPromptOverrideRepository>(services));
        Assert.NotNull(FindService<IClientTokenUsageRepository>(services));
        Assert.NotNull(FindService<IProCursorTokenUsageRecorder>(services));
        Assert.NotNull(FindService<IProCursorKnowledgeSourceRepository>(services));
        Assert.NotNull(FindService<IMentionAnswerService>(services));
        Assert.NotNull(FindService<IPrCrawlService>(services));
        Assert.NotNull(FindService<IFileByFileReviewOrchestrator>(services));
        Assert.NotNull(FindService<IProCursorGateway>(services));
        Assert.NotNull(FindService<IScmProviderRegistry>(services));
        Assert.NotNull(FindService<IRepositoryDiscoveryProvider>(services));
        Assert.NotNull(FindService<ICodeReviewQueryService>(services));
        Assert.NotNull(FindService<ICodeReviewPublicationService>(services));
        Assert.NotNull(FindService<IReviewDiscoveryProvider>(services));
        Assert.NotNull(FindService<IReviewerIdentityService>(services));
        Assert.NotNull(FindService<IWebhookIngressService>(services));
    }

    private static IConfiguration CreateConfiguration(bool withDatabaseConnectionString)
    {
        var values = new Dictionary<string, string?>
        {
            ["ADO_SKIP_TOKEN_VALIDATION"] = "true",
            ["ADO_STUB_PR"] = "true",
            ["MEISTER_JWT_SECRET"] = "test-module-registration-jwt-secret-32!",
            ["DB_CONNECTION_STRING"] = withDatabaseConnectionString
                ? "Host=localhost;Database=meister;Username=test;Password=test"
                : null,
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static ServiceDescriptor? FindService<TService>(IServiceCollection services)
    {
        return services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(TService));
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            var hasSolution = File.Exists(Path.Combine(current.FullName, "MeisterProPR.slnx"));
            var hasSourceTree = Directory.Exists(Path.Combine(current.FullName, "src"));

            if (hasSolution && hasSourceTree)
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate the repository root.");
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = nameof(ModuleRegistrationTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; }
            = new PhysicalFileProvider(AppContext.BaseDirectory);
    }
}
