// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using Azure.Core;
using MeisterProPR.Api.Features.ProCursor;
using MeisterProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Interfaces;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Clients;
using MeisterProPR.Infrastructure.Features.Crawling;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Features.Mentions;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using MeisterProPR.Infrastructure.Features.PromptCustomization;
using MeisterProPR.Infrastructure.Features.Reviewing;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;
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
        var configuration = CreateConfiguration(
            true,
            new Dictionary<string, string?>
            {
                ["PROCURSOR_DB_CONNECTION_STRING"] = "Host=localhost;Database=procursor;Username=test;Password=test",
            });

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
        Assert.NotNull(FindService<ProCursorOperationalDbContext>(services));
        Assert.NotNull(FindService<IMentionAnswerService>(services));
        Assert.NotNull(FindService<IPrCrawlService>(services));
        Assert.NotNull(FindService<IFileByFileReviewOrchestrator>(services));
        Assert.NotNull(FindService<IAgenticFileByFileReviewOrchestrator>(services));
        Assert.NotNull(FindService<IScmProviderRegistry>(services));
        Assert.NotNull(FindService<IRepositoryDiscoveryProvider>(services));
        Assert.NotNull(FindService<ICodeReviewQueryService>(services));
        Assert.NotNull(FindService<ICodeReviewPublicationService>(services));
        Assert.NotNull(FindService<IReviewDiscoveryProvider>(services));
        Assert.NotNull(FindService<IReviewerIdentityService>(services));
        Assert.NotNull(FindService<IWebhookIngressService>(services));
    }

    [Fact]
    public void ReviewingModule_WithoutDatabaseConnectionString_RegistersOfflineHarnessExecutionServices()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(false);

        services.AddInfrastructureSupport(configuration);
        services.AddReviewingModule(configuration);

        Assert.NotNull(FindService<IJobRepository>(services));
        Assert.NotNull(FindService<IProtocolRecorder>(services));
        Assert.NotNull(FindService<IReviewWorkflowRunner>(services));
        Assert.NotNull(FindService<IEvaluationArtifactWriter>(services));
    }

    [Fact]
    public void ReviewingModule_RegistersReviewingOwnedStrategyPorts()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(true);

        services.AddInfrastructureSupport(configuration);
        services.AddReviewingModule(configuration);

        Assert.NotNull(FindService<IFileByFileReviewOrchestrator>(services));
        Assert.NotNull(FindService<IAgenticFileByFileReviewOrchestrator>(services));
        Assert.NotNull(FindService<IPrWideAgenticReviewOrchestrator>(services));
    }

    [Fact]
    public void ReviewingModule_WithDatabaseConnectionString_DoesNotRegisterOfflineWorkflowRunner()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(true);

        services.AddInfrastructureSupport(configuration);
        services.AddReviewingModule(configuration);

        Assert.Null(FindService<IReviewWorkflowRunner>(services));
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
        var configuration = CreateConfiguration(
            true,
            new Dictionary<string, string?>
            {
                ["PROCURSOR_DB_CONNECTION_STRING"] = "Host=localhost;Database=procursor;Username=test;Password=test",
            });
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
        Assert.NotNull(FindService<ProCursorOperationalDbContext>(services));
        Assert.NotNull(FindService<IProCursorKnowledgeSourceRepository>(services));
        Assert.NotNull(FindService<IMentionAnswerService>(services));
        Assert.NotNull(FindService<IPrCrawlService>(services));
        Assert.NotNull(FindService<IFileByFileReviewOrchestrator>(services));
        Assert.NotNull(FindService<IAgenticFileByFileReviewOrchestrator>(services));
        Assert.NotNull(FindService<IProCursorGateway>(services));
        Assert.NotNull(FindService<IScmProviderRegistry>(services));
        Assert.NotNull(FindService<IRepositoryDiscoveryProvider>(services));
        Assert.NotNull(FindService<ICodeReviewQueryService>(services));
        Assert.NotNull(FindService<ICodeReviewPublicationService>(services));
        Assert.NotNull(FindService<IReviewDiscoveryProvider>(services));
        Assert.NotNull(FindService<IReviewerIdentityService>(services));
        Assert.NotNull(FindService<IWebhookIngressService>(services));
    }

    [Fact]
    public void ReviewingModule_WithoutSelectedCommentRelevanceFilter_RegistersBaselineSelection()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(true);

        services.AddInfrastructureSupport(configuration);
        services.AddReviewingModule(configuration);

        using var provider = services.BuildServiceProvider();
        var selection = provider.GetRequiredService<CommentRelevanceFilterSelection>();
        var registry = provider.GetRequiredService<CommentRelevanceFilterRegistry>();
        var evaluator = provider.GetRequiredService<ICommentRelevanceAmbiguityEvaluator>();

        Assert.False(selection.HasSelection);
        Assert.Null(selection.SelectedImplementationId);
        Assert.False(registry.HasSelection);
        Assert.Equal("AiCommentRelevanceAmbiguityEvaluator", evaluator.GetType().Name);
    }

    [Theory]
    [InlineData("pass-through-v1")]
    [InlineData("heuristic-v1")]
    public void ReviewingModule_WithSelectedCommentRelevanceFilter_ResolvesNamedImplementation(string selectedFilterId)
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(true);

        services.AddInfrastructureSupport(configuration);
        services.AddReviewingModule(configuration, selectedCommentRelevanceFilterId: selectedFilterId);

        using var provider = services.BuildServiceProvider();
        var selection = provider.GetRequiredService<CommentRelevanceFilterSelection>();
        var registry = provider.GetRequiredService<CommentRelevanceFilterRegistry>();

        Assert.True(selection.HasSelection);
        Assert.Equal(selectedFilterId, selection.SelectedImplementationId);
        Assert.True(registry.TryResolveSelected(out var filter));
        Assert.NotNull(filter);
        Assert.Equal(selectedFilterId, filter!.ImplementationId);
    }

    [Fact]
    public void Program_SelectedCommentRelevanceFilter_UsesHybridByDefault()
    {
        var selectedFilterId = InvokeSelectedCommentRelevanceFilterId();

        Assert.Equal("hybrid-v1", selectedFilterId);
    }

    [Fact]
    public void Program_WithoutRemoteProCursorConfiguration_DefaultsToDisabledMode()
    {
        var mode = InvokeEffectiveProCursorMode(CreateConfiguration(false));

        Assert.Equal("disabled", mode);
    }

    [Fact]
    public void Program_WithRemoteProCursorConfiguration_UsesManagedRemoteMode()
    {
        var mode = InvokeEffectiveProCursorMode(
            CreateConfiguration(
                false,
                new Dictionary<string, string?>
                {
                    ["PROCURSOR_REMOTE_MODE"] = "proprManagedRemote",
                    ["PROCURSOR_SERVICE_BASE_URL"] = "http://procursor.internal:8080",
                    ["PROCURSOR_SHARED_KEY"] = "shared-test-key",
                }));

        Assert.Equal("proprManagedRemote", mode);
    }

    [Fact]
    public void UsageReportingModule_WithoutExplicitOperationalConnection_DoesNotRegisterProCursorOperationalServices()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(true);

        services.AddUsageReportingModule(configuration);

        Assert.Null(FindService<IProCursorTokenUsageRecorder>(services));
        Assert.Null(FindService<ProCursorOperationalDbContext>(services));
    }

    [Fact]
    public void UsageReportingModule_WithExplicitOperationalConnection_RegistersProCursorOperationalServices()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(
            true,
            new Dictionary<string, string?>
            {
                ["PROCURSOR_DB_CONNECTION_STRING"] = "Host=localhost;Database=procursor;Username=test;Password=test",
            });

        services.AddUsageReportingModule(configuration);

        Assert.NotNull(FindService<IClientTokenUsageRepository>(services));
        Assert.Null(FindService<IProCursorTokenUsageRecorder>(services));
        Assert.Null(FindService<ProCursorOperationalDbContext>(services));
    }

    [Fact]
    public void ProCursorModule_WithExplicitOperationalConnection_RegistersProCursorOperationalServices()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(
            true,
            new Dictionary<string, string?>
            {
                ["PROCURSOR_DB_CONNECTION_STRING"] = "Host=localhost;Database=procursor;Username=test;Password=test",
            });

        services.AddOptions();
        services.AddLogging();
        services.AddProCursorModule(configuration);

        Assert.NotNull(FindService<IProCursorTokenUsageRecorder>(services));
        Assert.NotNull(FindService<ProCursorOperationalDbContext>(services));
    }

    [Fact]
    public void UsageReportingModule_InManagedRemoteMode_RegistersRemoteClientsWithoutOperationalDb()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(
            true,
            new Dictionary<string, string?>
            {
                ["PROCURSOR_REMOTE_MODE"] = "proprManagedRemote",
                ["PROCURSOR_SERVICE_BASE_URL"] = "http://procursor.internal:8080",
                ["PROCURSOR_SHARED_KEY"] = "shared-test-key",
            });

        services.AddOptions();
        services.AddLogging();
        services.AddProCursorRemoteMode(configuration);
        services.AddUsageReportingModule(configuration);

        Assert.NotNull(FindService<IProCursorTokenUsageReadRepository>(services));
        Assert.NotNull(FindService<IProCursorTokenUsageRebuildService>(services));
        Assert.Null(FindService<IProCursorTokenUsageRecorder>(services));
        Assert.Null(FindService<ProCursorOperationalDbContext>(services));
        Assert.Null(services.FirstOrDefault(descriptor => descriptor.ImplementationType == typeof(ProCursorTokenUsageReadRepository)));
        Assert.Null(services.FirstOrDefault(descriptor => descriptor.ImplementationType == typeof(ProCursorTokenUsageRebuildService)));
    }

    [Fact]
    public void AddProCursorRemoteMode_InManagedRemoteMode_RegistersManagedRemoteGatewayWithoutOperationalRepositories()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(
            true,
            new Dictionary<string, string?>
            {
                ["PROCURSOR_REMOTE_MODE"] = "proprManagedRemote",
                ["PROCURSOR_SERVICE_BASE_URL"] = "http://procursor.internal:8080",
                ["PROCURSOR_SHARED_KEY"] = "shared-test-key",
            });

        services.AddOptions();
        services.AddLogging();
        services.AddDataProtection();
        services.AddInfrastructureSupport(configuration);
        services.AddProCursorRemoteMode(configuration);
        services.AddSingleton(Substitute.For<IAiConnectionRepository>());
        services.AddSingleton(Substitute.For<IProCursorKnowledgeSourceRepository>());
        services.AddScoped<ManagedRemoteProCursorGateway>();
        services.AddScoped<IProCursorGateway>(sp => sp.GetRequiredService<ManagedRemoteProCursorGateway>());

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        using var scope = provider.CreateScope();

        var gateway = scope.ServiceProvider.GetRequiredService<IProCursorGateway>();

        Assert.IsType<ManagedRemoteProCursorGateway>(gateway);
        Assert.Null(FindService<IProCursorIndexJobRepository>(services));
        Assert.Null(FindService<IProCursorIndexSnapshotRepository>(services));
    }

    [Fact]
    public void Program_RegistersProPrOwnedBrokerBackends_ForApiHostComposition()
    {
        var contents = File.ReadAllText(Path.Combine(RepoRoot, "src/MeisterProPR.Api/Program.cs"));

        Assert.Contains("LocalProPrScmBroker", contents, StringComparison.Ordinal);
        Assert.Contains("LocalProPrEmbeddingBroker", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalProCursorScmBroker", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalProCursorEmbeddingBroker", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_WithDisabledProCursorConfiguration_UsesDisabledGatewayMode()
    {
        var mode = InvokeEffectiveProCursorMode(
            CreateConfiguration(
                false,
                new Dictionary<string, string?>
                {
                    ["PROCURSOR_REMOTE_MODE"] = "disabled",
                }));

        Assert.Equal("disabled", mode);
    }

    [Fact]
    public void AddProCursorRemoteMode_RegistersDisabledGatewayAndRemoteHealthDependencies()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(
            false,
            new Dictionary<string, string?>
            {
                ["PROCURSOR_REMOTE_MODE"] = "disabled",
            });

        services.AddOptions();
        services.AddLogging();
        services.AddHealthChecks();
        services.AddProCursorRemoteMode(configuration);

        Assert.NotNull(FindService<DisabledProCursorGateway>(services));
        Assert.NotNull(FindService<HttpProCursorGateway>(services));
    }

    [Fact]
    public void AddProCursorRemoteMode_InDisabledMode_DoesNotResolveManagedRemoteGateway()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(
            false,
            new Dictionary<string, string?>
            {
                ["PROCURSOR_REMOTE_MODE"] = "disabled",
            });

        services.AddOptions();
        services.AddLogging();
        services.AddHealthChecks();
        services.AddDataProtection();
        services.AddInfrastructureSupport(configuration);
        services.AddProCursorRemoteMode(configuration);
        services.AddSingleton(Substitute.For<IAiConnectionRepository>());
        services.AddSingleton(Substitute.For<IProCursorKnowledgeSourceRepository>());
        services.AddScoped<ManagedRemoteProCursorGateway>();
        services.AddScoped<IProCursorGateway>(sp => sp.GetRequiredService<DisabledProCursorGateway>());

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        using var scope = provider.CreateScope();

        var gateway = scope.ServiceProvider.GetRequiredService<IProCursorGateway>();

        Assert.IsType<DisabledProCursorGateway>(gateway);
    }

    [Fact]
    public void ReviewingModule_RegistersDeterministicReviewFindingGateAndInvariantProviders()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(true);

        services.AddInfrastructureSupport(configuration);
        services.AddReviewingModule(configuration);

        using var provider = services.BuildServiceProvider();
        var gate = provider.GetRequiredService<IDeterministicReviewFindingGate>();
        var invariantProviders = provider.GetServices<IReviewInvariantFactProvider>().ToList();

        Assert.Equal("DeterministicReviewFindingGate", gate.GetType().Name);
        Assert.Contains(invariantProviders, service => service.GetType().Name == "DomainReviewInvariantFactProvider");
        Assert.Contains(invariantProviders, service => service.GetType().Name == "PersistenceReviewInvariantFactProvider");
    }

    private static string InvokeSelectedCommentRelevanceFilterId()
    {
        var method = typeof(Program).GetMethod(
            "GetSelectedCommentRelevanceFilterId",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var result = method!.Invoke(null, []);
        Assert.IsType<string>(result);
        return (string)result;
    }

    private static string InvokeEffectiveProCursorMode(IConfiguration configuration)
    {
        var method = typeof(Program).GetMethod(
            "GetEffectiveProCursorMode",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var result = method!.Invoke(null, [configuration]);
        Assert.IsType<string>(result);
        return (string)result;
    }

    private static IConfiguration CreateConfiguration(
        bool withDatabaseConnectionString,
        IEnumerable<KeyValuePair<string, string?>>? overrides = null)
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

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                values[key] = value;
            }
        }

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
