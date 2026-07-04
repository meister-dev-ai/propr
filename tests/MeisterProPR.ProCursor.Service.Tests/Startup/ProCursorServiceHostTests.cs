// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.ProCursor.Contracts.ProCursor;
using MeisterProPR.ProCursor.Options;
using MeisterProPR.ProCursor.Service.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MeisterProPR.ProCursor.Service.Tests.Startup;

public sealed class ProCursorServiceHostTests
{
    private const string ExpectedStartupValidationMessage =
        "PROCURSOR_PROPR_BASE_URL, PROCURSOR_SHARED_KEY, and PROCURSOR_DB_CONNECTION_STRING are required";

    [Fact]
    public void CreateClient_WithoutSharedKey_ThrowsStartupValidationError()
    {
        using var factory = new InvalidHostFactory();

        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            using var _ = factory.Server;
        });

        Assert.Contains(ExpectedStartupValidationMessage, FlattenExceptionMessages(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void CreateClient_WithoutProPrBaseUrl_ThrowsStartupValidationError()
    {
        using var factory = new MissingBrokerUrlFactory();

        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            using var _ = factory.Server;
        });

        Assert.Contains(ExpectedStartupValidationMessage, FlattenExceptionMessages(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void CreateClient_WithoutOperationalDbConnection_ThrowsStartupValidationError()
    {
        using var factory = new MissingOperationalDbFactory();

        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            using var _ = factory.Server;
        });

        Assert.Contains(ExpectedStartupValidationMessage, FlattenExceptionMessages(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void CreateClient_WithManagedRemoteConfiguration_BuildsHost()
    {
        using var factory = new ProCursorServiceFactory();

        using var _ = factory.Server;

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProCursorOperationalDbContext>();
        Assert.NotNull(db.Model.FindEntityType(typeof(ProCursorIndexJob)));
    }

    [Fact]
    public void CreateClient_WithManagedRemoteConfiguration_ResolvesExpectedHostOptions()
    {
        using var factory = new ProCursorServiceFactory();

        using var _ = factory.Server;

        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<ProCursorHostOptions>>().Value;

        Assert.Equal("http://propr.internal:8080", options.ProPrBaseUrl);
        Assert.Equal(ProCursorServiceFactory.SharedKey, options.SharedKey);
        Assert.True(options.RequestTimeoutSeconds > 0);
        Assert.True(options.RuntimeConfigurationTtlSeconds > 0);
    }

    [Fact]
    public void CreateClient_WithManagedRemoteConfiguration_ResolvesProCursorOwnedTokenUsageOptions()
    {
        using var factory = new ProCursorServiceFactory();

        using var _ = factory.Server;

        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<ProCursorTokenUsageOptions>>();

        Assert.NotNull(options.Value);
        Assert.Equal("MeisterProPR.ProCursor.Contracts.ProCursor", typeof(ProCursorTokenUsageOptions).Namespace);
    }

    [Fact]
    public void Program_UsesMigrationBasedOperationalSchemaInitialization()
    {
        var programContents = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "src/MeisterProPR.ProCursor.Service/Program.cs"));

        Assert.Contains("Database.MigrateAsync", programContents, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureOperationalSchemaAsync", programContents, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTablesAsync", programContents, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureCreatedAsync", programContents, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateClient_WithManagedRemoteConfiguration_WithoutAdoStub_BuildsHost()
    {
        using var factory = new NonStubManagedRemoteFactory();

        using var _ = factory.Server;

        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<ProCursorHostOptions>>().Value;

        Assert.Equal("http://propr.internal:8080", options.ProPrBaseUrl);
    }

    [Fact]
    public void ProCursorHostOptions_ExposesNoLegacyHostModeSelector()
    {
        Assert.Null(typeof(ProCursorHostOptions).GetProperty("HostMode"));
        Assert.Null(typeof(ProCursorHostOptions).GetProperty("IsManagedRemoteMode"));
        Assert.Null(typeof(ProCursorHostOptions).GetField("ProprManagedRemoteMode"));
    }

    private static string FlattenExceptionMessages(Exception exception)
    {
        var messages = new List<string>();
        var current = exception;
        while (current is not null)
        {
            messages.Add(current.ToString());
            current = current.InnerException;
        }

        return string.Join(Environment.NewLine, messages);
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

    private sealed class InvalidHostFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("PROCURSOR_PROPR_BASE_URL", "http://propr.internal:8080");
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("ADO_STUB_PR", "true");
            builder.UseSetting("MEISTER_JWT_SECRET", "test-procursor-invalid-host-jwt-secret!");
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            try
            {
                return base.CreateHost(builder);
            }
            catch (Exception ex)
            {
                throw ex.InnerException ?? ex;
            }
        }
    }

    private sealed class MissingBrokerUrlFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("PROCURSOR_SHARED_KEY", "test-shared-key");
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("ADO_STUB_PR", "true");
            builder.UseSetting("MEISTER_JWT_SECRET", "test-procursor-missing-broker-url-jwt-secret!");
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            try
            {
                return base.CreateHost(builder);
            }
            catch (Exception ex)
            {
                throw ex.InnerException ?? ex;
            }
        }
    }

    private sealed class MissingOperationalDbFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("PROCURSOR_PROPR_BASE_URL", "http://propr.internal:8080");
            builder.UseSetting("PROCURSOR_SHARED_KEY", "test-shared-key");
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("ADO_STUB_PR", "true");
            builder.UseSetting("MEISTER_JWT_SECRET", "test-procursor-missing-operational-db-jwt-secret!");
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            try
            {
                return base.CreateHost(builder);
            }
            catch (Exception ex)
            {
                throw ex.InnerException ?? ex;
            }
        }
    }

    private sealed class NonStubManagedRemoteFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("PROCURSOR_PROPR_BASE_URL", "http://propr.internal:8080");
            builder.UseSetting("PROCURSOR_SHARED_KEY", "test-shared-key");
            builder.UseSetting("PROCURSOR_DB_CONNECTION_STRING", "Host=localhost;Database=procursor_test;Username=test;Password=test");
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("MEISTER_JWT_SECRET", "test-procursor-nonstub-jwt-secret!");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();
                ProCursorServiceFactory.ConfigureInMemoryOperationalDb(services);
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            try
            {
                return base.CreateHost(builder);
            }
            catch (Exception ex)
            {
                throw ex.InnerException ?? ex;
            }
        }
    }
}
