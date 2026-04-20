// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.HealthChecks;
using MeisterProPR.Api.Telemetry;
using MeisterProPR.Api.Workers;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Clients.Support;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterProPR.Api.Tests.HealthChecks;

public sealed class WorkerHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_WithConfiguredRegistry_ReportsSeparateActivationAndReadinessSemantics()
    {
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.IsRegistered(Arg.Any<ScmProvider>()).Returns(true);
        registry.GetRegisteredCapabilities(Arg.Any<ScmProvider>()).Returns(["repositoryDiscovery"]);
        var providerActivationService = Substitute.For<IProviderActivationService>();
        var updatedAt = DateTimeOffset.Parse("2026-04-20T10:00:00Z");
        providerActivationService.ListAsync(Arg.Any<CancellationToken>())
            .Returns(
            [
                new ProviderActivationStatusDto(
                    ScmProvider.AzureDevOps,
                    true,
                    true,
                    ["repositoryDiscovery"],
                    ProviderConnectionReadinessLevel.WorkflowComplete,
                    "Azure DevOps is fully supported.",
                    updatedAt),
                new ProviderActivationStatusDto(
                    ScmProvider.GitHub,
                    false,
                    true,
                    ["repositoryDiscovery"],
                    ProviderConnectionReadinessLevel.OnboardingReady,
                    "GitHub remains onboarding ready when enabled.",
                    updatedAt),
                new ProviderActivationStatusDto(
                    ScmProvider.GitLab,
                    true,
                    true,
                    ["repositoryDiscovery"],
                    ProviderConnectionReadinessLevel.OnboardingReady,
                    "GitLab remains onboarding ready when enabled.",
                    updatedAt),
                new ProviderActivationStatusDto(
                    ScmProvider.Forgejo,
                    false,
                    true,
                    ["repositoryDiscovery"],
                    ProviderConnectionReadinessLevel.OnboardingReady,
                    "Forgejo remains onboarding ready when enabled.",
                    updatedAt),
            ]);

        var services = new ServiceCollection();
        services.AddSingleton(registry);
        services.AddSingleton(providerActivationService);
        services.AddSingleton<IProviderReadinessProfileCatalog>(new StaticProviderReadinessProfileCatalog());

        using var provider = services.BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DB_CONNECTION_STRING"] = "Host=localhost;Database=meisterpropr;Username=test;Password=test",
                })
            .Build();
        using var metrics = new ReviewJobMetrics(Substitute.For<IServiceScopeFactory>());
        var worker = new ReviewJobWorker(
            Substitute.For<IServiceScopeFactory>(),
            Options.Create(new WorkerOptions()),
            metrics,
            NullLogger<ReviewJobWorker>.Instance);

        var sut = new WorkerHealthCheck(worker, provider, configuration);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal("installationWideAdminPolicy", result.Data["providerActivationSemantics"]);
        Assert.Equal("leastReadyHostVariantSupportClaim", result.Data["providerReadinessSemantics"]);
        Assert.Equal(true, result.Data["provider.azureDevOps.enabled"]);
        Assert.Equal(true, result.Data["provider.azureDevOps.effectiveAvailable"]);
        Assert.Equal(false, result.Data["provider.github.enabled"]);
        Assert.Equal(false, result.Data["provider.github.effectiveAvailable"]);
        Assert.Equal("workflowComplete", result.Data["provider.azureDevOps.supportClaimReadiness"]);
        Assert.Equal("onboardingReady", result.Data["provider.github.supportClaimReadiness"]);
        Assert.Equal("onboardingReady", result.Data["provider.gitLab.supportClaimReadiness"]);
    }
}
