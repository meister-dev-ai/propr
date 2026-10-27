// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Features.IdentityAndAccess;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.IdentityAndAccess;

public sealed class PlatformAdminSsoStartupDiagnosticsTests
{
    [Fact]
    public async Task ReportAsync_WhenTheLicenseDoesNotGrantSso_WarnsThatTheProviderIsIgnored()
    {
        var logger = new ListLogger();

        await PlatformAdminSsoStartupDiagnostics.ReportAsync(Services(configured: true, licensed: false), logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(PlatformAdminSsoOptions.AuthorityKey, entry.Message, StringComparison.Ordinal);
        Assert.Contains(PremiumCapabilityKey.SsoAuthentication, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportAsync_WhenTheLicenseGrantsSso_ReportsTheProviderInUse()
    {
        var logger = new ListLogger();

        await PlatformAdminSsoStartupDiagnostics.ReportAsync(Services(configured: true, licensed: true), logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("https://id.example.test/realms/staff", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportAsync_WithoutAConfiguredProvider_WritesNothing()
    {
        var logger = new ListLogger();

        await PlatformAdminSsoStartupDiagnostics.ReportAsync(Services(configured: false, licensed: true), logger);

        Assert.Empty(logger.Entries);
    }

    private static ServiceProvider Services(bool configured, bool licensed)
    {
        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.IsEnabledAsync(PremiumCapabilityKey.SsoAuthentication, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(licensed));

        var services = new ServiceCollection();
        services.AddSingleton(licensing);
        services.AddSingleton(
            Options.Create(
                configured
                    ? new PlatformAdminSsoOptions
                    {
                        Authority = "https://id.example.test/realms/staff",
                        ClientId = "propr-platform",
                        ClientSecret = "platform-client-secret",
                        RequiredRole = "operator",
                    }
                    : new PlatformAdminSsoOptions()));
        return services.BuildServiceProvider();
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            this.Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
