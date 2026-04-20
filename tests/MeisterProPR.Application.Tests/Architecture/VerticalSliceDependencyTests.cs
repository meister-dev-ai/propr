// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Tests.Architecture;

public sealed class VerticalSliceDependencyTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    [Fact]
    public void ModuleRegistrationRoots_ExistForAllPlannedModules()
    {
        var moduleFiles = new[]
        {
            "src/MeisterProPR.Infrastructure/Features/Reviewing/ReviewingModuleServiceCollectionExtensions.cs",
            "src/MeisterProPR.Infrastructure/Features/Crawling/CrawlingModuleServiceCollectionExtensions.cs",
            "src/MeisterProPR.Infrastructure/Features/Clients/ClientsModuleServiceCollectionExtensions.cs",
            "src/MeisterProPR.Infrastructure/Features/IdentityAndAccess/IdentityAndAccessModuleServiceCollectionExtensions.cs",
            "src/MeisterProPR.Infrastructure/Features/Mentions/MentionsModuleServiceCollectionExtensions.cs",
            "src/MeisterProPR.Infrastructure/Features/PromptCustomization/PromptCustomizationModuleServiceCollectionExtensions.cs",
            "src/MeisterProPR.Infrastructure/Features/UsageReporting/UsageReportingModuleServiceCollectionExtensions.cs",
        };

        foreach (var relativePath in moduleFiles)
        {
            Assert.True(
                File.Exists(Path.Combine(RepoRoot, relativePath)),
                $"Expected module root file '{relativePath}' to exist.");
        }
    }

    [Fact]
    public void SharedInfrastructureSupport_ExcludesFeatureOwnedRegistrations()
    {
        var contents = ReadRepoFile("src/MeisterProPR.Infrastructure/DependencyInjection/InfrastructureServiceExtensions.cs");

        var forbiddenFeatureRegistrationTokens = new[]
        {
            "AddScoped<IJobRepository, JobRepository>()",
            "AddScoped<IClientRegistry, DbClientRegistry>()",
            "AddScoped<ICrawlConfigurationRepository, CrawlConfigurationRepository>()",
            "AddScoped<IMentionScanRepository, EfMentionScanRepository>()",
            "AddScoped<IPromptOverrideRepository, PromptOverrideRepository>()",
            "AddScoped<IUserRepository, AppUserRepository>()",
            "AddScoped<IClientTokenUsageRepository, ClientTokenUsageRepository>()",
            "AddSingleton<IProCursorTokenUsageRecorder, EfProCursorTokenUsageRecorder>()",
            "AddScoped<IPrCrawlService, PrCrawlService>()",
            "AddScoped<IMentionScanService, MentionScanService>()",
            "AddScoped<IMentionReplyService, MentionReplyService>()",
            "AddTransient<ReviewOrchestrationService>()",
        };

        foreach (var token in forbiddenFeatureRegistrationTokens)
        {
            Assert.DoesNotContain(token, contents, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ModuleRegistrationRoots_DoNotRegisterOtherModuleServices()
    {
        var moduleBoundaries = new[]
        {
            new ModuleBoundary(
                "src/MeisterProPR.Infrastructure/Features/Reviewing/ReviewingModuleServiceCollectionExtensions.cs",
                ["IJobRepository", "IProtocolRecorder", "ReviewOrchestrationService"],
                [
                    "IClientAdminService", "ICrawlConfigurationRepository", "IUserRepository", "IMentionScanRepository",
                    "IPromptOverrideRepository", "IClientTokenUsageRepository",
                ]),
            new ModuleBoundary(
                "src/MeisterProPR.Infrastructure/Features/Crawling/CrawlingModuleServiceCollectionExtensions.cs",
                ["ICrawlConfigurationRepository", "IReviewPrScanRepository", "IPrCrawlService"],
                [
                    "IClientAdminService", "IUserRepository", "IMentionScanRepository", "IPromptOverrideRepository",
                    "IClientTokenUsageRepository",
                ]),
            new ModuleBoundary(
                "src/MeisterProPR.Infrastructure/Features/Clients/ClientsModuleServiceCollectionExtensions.cs",
                ["IClientRegistry", "IClientAdminService", "IAiConnectionRepository"],
                [
                    "IJobRepository", "ICrawlConfigurationRepository", "IUserRepository", "IMentionScanRepository",
                    "IPromptOverrideRepository",
                ]),
            new ModuleBoundary(
                "src/MeisterProPR.Infrastructure/Features/IdentityAndAccess/IdentityAndAccessModuleServiceCollectionExtensions.cs",
                ["IUserRepository", "IRefreshTokenRepository", "IUserPatRepository", "IJwtTokenService"],
                [
                    "IJobRepository", "ICrawlConfigurationRepository", "IClientRegistry", "IMentionScanRepository",
                    "IPromptOverrideRepository",
                ]),
            new ModuleBoundary(
                "src/MeisterProPR.Infrastructure/Features/Mentions/MentionsModuleServiceCollectionExtensions.cs",
                ["IMentionReplyJobRepository", "IMentionScanRepository", "IMentionScanService"],
                [
                    "IJobRepository", "ICrawlConfigurationRepository", "IClientRegistry", "IUserRepository",
                    "IPromptOverrideRepository",
                ]),
            new ModuleBoundary(
                "src/MeisterProPR.Infrastructure/Features/PromptCustomization/PromptCustomizationModuleServiceCollectionExtensions.cs",
                ["IPromptOverrideRepository", "IPromptOverrideService"],
                [
                    "IJobRepository", "ICrawlConfigurationRepository", "IClientRegistry", "IUserRepository",
                    "IMentionScanRepository",
                ]),
            new ModuleBoundary(
                "src/MeisterProPR.Infrastructure/Features/UsageReporting/UsageReportingModuleServiceCollectionExtensions.cs",
                ["IClientTokenUsageRepository", "IProCursorTokenUsageRecorder", "IProCursorTokenUsageReadRepository"],
                [
                    "IJobRepository", "ICrawlConfigurationRepository", "IClientRegistry", "IUserRepository",
                    "IMentionScanRepository", "IPromptOverrideRepository",
                ]),
        };

        foreach (var boundary in moduleBoundaries)
        {
            var contents = ReadRepoFile(boundary.FilePath);

            foreach (var expectedToken in boundary.ExpectedTokens)
            {
                Assert.Contains(expectedToken, contents, StringComparison.Ordinal);
            }

            foreach (var forbiddenToken in boundary.ForbiddenTokens)
            {
                Assert.DoesNotContain(forbiddenToken, contents, StringComparison.Ordinal);
            }
        }
    }

    private static string ReadRepoFile(string relativePath)
    {
        return File.ReadAllText(Path.Combine(RepoRoot, relativePath));
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            var hasSolution = File.Exists(Path.Combine(current.FullName, "MeisterProPR.slnx"));
            var hasSourceTree = Directory.Exists(Path.Combine(current.FullName, "src"));
            var hasTestTree = Directory.Exists(Path.Combine(current.FullName, "tests"));

            if (hasSolution && hasSourceTree && hasTestTree)
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate the repository root.");
    }

    private sealed record ModuleBoundary(string FilePath, string[] ExpectedTokens, string[] ForbiddenTokens);
}
