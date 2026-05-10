// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.ProCursor.Infrastructure.Remote;
using MeisterProPR.ProCursor.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.ProCursor.Service.Tests.Startup;

public sealed class ProCursorRuntimeCompositionTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    [Fact]
    public void ProCursorServiceProgram_ComposesProCursorOwnedWorkersAndHealthChecks()
    {
        var contents = File.ReadAllText(Path.Combine(RepoRoot, "src/MeisterProPR.ProCursor.Service/Program.cs"));

        Assert.Contains("using MeisterProPR.ProCursor.Workers;", contents, StringComparison.Ordinal);
        Assert.Contains("using MeisterProPR.ProCursor.HealthChecks;", contents, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<ProCursorIndexWorker>()", contents, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<ProCursorTokenUsageRollupWorker>()", contents, StringComparison.Ordinal);
        Assert.Contains("AddCheck<ProCursorIndexWorkerHealthCheck>", contents, StringComparison.Ordinal);
        Assert.Contains("AddCheck<ProCursorTokenUsageRollupWorkerHealthCheck>", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("MeisterProPR.Api.Workers.ProCursorIndexWorker", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("MeisterProPR.Api.Workers.ProCursorTokenUsageRollupWorker", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("using MeisterProPR.Api.HealthChecks;", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void ProCursorServiceHost_ResolvesProCursorOwnedGatewayAndWorkers()
    {
        using var factory = new Support.ProCursorServiceFactory();
        using var scope = factory.Services.CreateScope();

        var gateway = scope.ServiceProvider.GetRequiredService<IProCursorGateway>();
        var runtimeCache = scope.ServiceProvider.GetRequiredService<IProCursorRuntimeConfigurationCache>();
        var indexWorker = scope.ServiceProvider.GetRequiredService<ProCursorIndexWorker>();
        var rollupWorker = scope.ServiceProvider.GetRequiredService<ProCursorTokenUsageRollupWorker>();

        Assert.NotNull(gateway);
        Assert.Equal("MeisterProPR.ProCursor.Infrastructure.Remote", runtimeCache.GetType().Namespace);
        Assert.Equal("MeisterProPR.ProCursor.Workers", indexWorker.GetType().Namespace);
        Assert.Equal("MeisterProPR.ProCursor.Workers", rollupWorker.GetType().Namespace);
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MeisterProPR.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate the repository root.");
    }
}
