// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
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
using Microsoft.Extensions.Hosting;

namespace MeisterProPR.Api.Tests.Startup;

public sealed class ModuleRegistrationTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    [Fact]
    public void Program_UsesModuleEntryPoints_ForFeatureRegistration()
    {
        var contents = File.ReadAllText(Path.Combine(RepoRoot, "src/MeisterProPR.Api/Program.cs"));

        Assert.Contains("AddInfrastructureSupport(builder.Configuration, builder.Environment)", contents, StringComparison.Ordinal);
        Assert.Contains("AddReviewingModule(builder.Configuration, builder.Environment)", contents, StringComparison.Ordinal);
        Assert.Contains("AddCrawlingModule(builder.Configuration, builder.Environment)", contents, StringComparison.Ordinal);
        Assert.Contains("AddClientsModule(builder.Configuration, builder.Environment)", contents, StringComparison.Ordinal);
        Assert.Contains("AddIdentityAndAccessModule(builder.Configuration, builder.Environment)", contents, StringComparison.Ordinal);
        Assert.Contains("AddMentionsModule(builder.Configuration, builder.Environment)", contents, StringComparison.Ordinal);
        Assert.Contains("AddPromptCustomizationModule(builder.Configuration, builder.Environment)", contents, StringComparison.Ordinal);
        Assert.Contains("AddUsageReportingModule(builder.Configuration, builder.Environment)", contents, StringComparison.Ordinal);
        Assert.Contains("AddProCursorModule(builder.Configuration, builder.Environment)", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("AddInfrastructure(builder.Configuration)", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void InfrastructureSupport_RegistersOnlySharedSupportServices()
    {
        var services = new ServiceCollection();

        services.AddInfrastructureSupport(CreateConfiguration(dbMode: false));

        Assert.NotNull(FindService<IAdoTokenValidator>(services));
        Assert.NotNull(FindService<IAiChatClientFactory>(services));
        Assert.NotNull(FindService<IAiEmbeddingGeneratorFactory>(services));
        Assert.Null(FindService<IJobRepository>(services));
        Assert.Null(FindService<ICrawlConfigurationRepository>(services));
        Assert.Null(FindService<IClientRegistry>(services));
        Assert.Null(FindService<IUserRepository>(services));
        Assert.Null(FindService<IMentionScanRepository>(services));
        Assert.Null(FindService<IPromptOverrideRepository>(services));
        Assert.Null(FindService<IClientTokenUsageRepository>(services));
        Assert.Null(FindService<IProCursorTokenUsageRecorder>(services));
    }

    [Fact]
    public void ComposedModules_RegisterFeatureOwnedServices()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(dbMode: true);

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
        Assert.NotNull(FindService<IClientRegistry>(services));
        Assert.NotNull(FindService<IUserRepository>(services));
        Assert.NotNull(FindService<IMentionScanRepository>(services));
        Assert.NotNull(FindService<IPromptOverrideRepository>(services));
        Assert.NotNull(FindService<IClientTokenUsageRepository>(services));
        Assert.NotNull(FindService<IProCursorTokenUsageRecorder>(services));
        Assert.NotNull(FindService<IMentionAnswerService>(services));
        Assert.NotNull(FindService<IPrCrawlService>(services));
        Assert.NotNull(FindService<IFileByFileReviewOrchestrator>(services));
    }

    [Fact]
    public void ComposedModules_TestingEnvironment_SuppressesDbRegistrationsUntilExplicitlyEnabled()
    {
        var services = new ServiceCollection();
        var configuration = CreateConfiguration(dbMode: true);
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

        Assert.Null(FindService<IJobRepository>(services));
        Assert.Null(FindService<ICrawlConfigurationRepository>(services));
        Assert.Null(FindService<IClientRegistry>(services));
        Assert.Null(FindService<IUserRepository>(services));
        Assert.Null(FindService<IMentionScanRepository>(services));
        Assert.Null(FindService<IPromptOverrideRepository>(services));
        Assert.Null(FindService<IClientTokenUsageRepository>(services));
        Assert.Null(FindService<IProCursorTokenUsageRecorder>(services));
        Assert.Null(FindService<IProCursorKnowledgeSourceRepository>(services));
        Assert.NotNull(FindService<IMentionAnswerService>(services));
        Assert.NotNull(FindService<IPrCrawlService>(services));
        Assert.NotNull(FindService<IFileByFileReviewOrchestrator>(services));
        Assert.NotNull(FindService<IProCursorGateway>(services));
    }

    private static IConfiguration CreateConfiguration(bool dbMode, bool testEnableDbMode = false)
    {
        var values = new Dictionary<string, string?>
        {
            ["ADO_SKIP_TOKEN_VALIDATION"] = "true",
            ["ADO_STUB_PR"] = "true",
            ["MEISTER_JWT_SECRET"] = "test-module-registration-jwt-secret-32!",
            ["DB_CONNECTION_STRING"] = dbMode ? "Host=localhost;Database=meister;Username=test;Password=test" : null,
            ["TEST_ENABLE_DB_MODE"] = testEnableDbMode ? "true" : null,
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = nameof(ModuleRegistrationTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(AppContext.BaseDirectory);
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
}
