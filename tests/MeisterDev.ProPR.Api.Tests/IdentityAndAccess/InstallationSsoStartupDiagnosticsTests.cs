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

public sealed class InstallationSsoStartupDiagnosticsTests
{
    private const string StaffAuthority = "https://id.example.test/realms/staff";
    private const string CustomerAuthority = "https://id.example.test/realms/customers";

    [Fact]
    public async Task ReportAsync_WhenTheLicenseDoesNotGrantSso_WarnsThatTheProviderForPlatformAdministratorsIsIgnored()
    {
        var logger = new ListLogger();

        await InstallationSsoStartupDiagnostics.ReportAsync(Services(platformAdmin: true, provisionedUser: false, licensed: false), logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(PlatformAdminSsoOptions.AuthorityKey, entry.Message, StringComparison.Ordinal);
        Assert.Contains(PremiumCapabilityKey.SsoAuthentication, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportAsync_WhenTheLicenseGrantsSso_ReportsTheProviderForPlatformAdministratorsInUse()
    {
        var logger = new ListLogger();

        await InstallationSsoStartupDiagnostics.ReportAsync(Services(platformAdmin: true, provisionedUser: false, licensed: true), logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(StaffAuthority, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportAsync_WhenTheLicenseDoesNotGrantSso_WarnsThatTheProviderForProvisionedUsersIsIgnored()
    {
        var logger = new ListLogger();

        await InstallationSsoStartupDiagnostics.ReportAsync(Services(platformAdmin: false, provisionedUser: true, licensed: false), logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(ProvisionedUserSsoOptions.AuthorityKey, entry.Message, StringComparison.Ordinal);
        Assert.Contains(PremiumCapabilityKey.SsoAuthentication, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportAsync_WithBothProvidersAndTheLicense_ReportsBothInUse()
    {
        var logger = new ListLogger();

        await InstallationSsoStartupDiagnostics.ReportAsync(Services(platformAdmin: true, provisionedUser: true, licensed: true), logger);

        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Information, entry.Level));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains(StaffAuthority, StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Message.Contains(CustomerAuthority, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReportAsync_WithoutAConfiguredProvider_WritesNothing()
    {
        var logger = new ListLogger();

        await InstallationSsoStartupDiagnostics.ReportAsync(Services(platformAdmin: false, provisionedUser: false, licensed: true), logger);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task ReportAsync_WithoutThePasswordSignInAndWithoutTheLicense_RefusesTheStart()
    {
        var logger = new ListLogger();
        var services = Services(platformAdmin: true, provisionedUser: true, licensed: false, localSignIn: false);

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => InstallationSsoStartupDiagnostics.ReportAsync(services, logger));

        Assert.Contains(InstallationSignInOptions.LocalSignInEnabledKey, failure.Message, StringComparison.Ordinal);
        Assert.Contains(PlatformAdminSsoOptions.AuthorityKey, failure.Message, StringComparison.Ordinal);
        Assert.Contains(PremiumCapabilityKey.SsoAuthentication, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportAsync_WithoutThePasswordSignInWhenTheLicenseCannotBeRead_RefusesTheStart()
    {
        var logger = new ListLogger();
        var services = Services(platformAdmin: true, provisionedUser: false, licensed: true, localSignIn: false, licenseReadable: false);

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => InstallationSsoStartupDiagnostics.ReportAsync(services, logger));

        Assert.Contains(InstallationSignInOptions.LocalSignInEnabledKey, failure.Message, StringComparison.Ordinal);
        Assert.Contains(PremiumCapabilityKey.SsoAuthentication, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportAsync_WithThePasswordSignInWhenTheLicenseCannotBeRead_OnlyWarns()
    {
        var logger = new ListLogger();

        await InstallationSsoStartupDiagnostics.ReportAsync(
            Services(platformAdmin: true, provisionedUser: false, licensed: true, licenseReadable: false),
            logger);

        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public async Task ReportAsync_WithoutThePasswordSignInAndWithTheLicense_ReportsTheProviderForPlatformAdministratorsInUse()
    {
        var logger = new ListLogger();

        await InstallationSsoStartupDiagnostics.ReportAsync(
            Services(platformAdmin: true, provisionedUser: false, licensed: true, localSignIn: false),
            logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(StaffAuthority, entry.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider Services(
        bool platformAdmin,
        bool provisionedUser,
        bool licensed,
        bool localSignIn = true,
        bool licenseReadable = true)
    {
        var licensing = Substitute.For<ILicensingCapabilityService>();
        if (licenseReadable)
        {
            licensing.IsEnabledAsync(PremiumCapabilityKey.SsoAuthentication, Arg.Any<CancellationToken>())
                .Returns(ValueTask.FromResult(licensed));
        }
        else
        {
            licensing.IsEnabledAsync(PremiumCapabilityKey.SsoAuthentication, Arg.Any<CancellationToken>())
                .Returns<ValueTask<bool>>(_ => throw new InvalidOperationException("The license store is unavailable."));
        }

        var services = new ServiceCollection();
        services.AddSingleton(licensing);
        services.AddSingleton(Options.Create(new InstallationSignInOptions { LocalSignInEnabled = localSignIn }));
        services.AddSingleton(
            Options.Create(
                platformAdmin
                    ? new PlatformAdminSsoOptions
                    {
                        Authority = StaffAuthority,
                        ClientId = "propr-platform",
                        ClientSecret = "platform-client-secret",
                        RequiredRole = "operator",
                    }
                    : new PlatformAdminSsoOptions()));
        services.AddSingleton(
            Options.Create(
                provisionedUser
                    ? new ProvisionedUserSsoOptions
                    {
                        Authority = CustomerAuthority,
                        ClientId = "propr-cell",
                        ClientSecret = "cell-client-secret",
                    }
                    : new ProvisionedUserSsoOptions()));
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
