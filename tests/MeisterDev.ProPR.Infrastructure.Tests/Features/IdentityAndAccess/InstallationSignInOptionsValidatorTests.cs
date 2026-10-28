// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.IdentityAndAccess;

public sealed class InstallationSignInOptionsValidatorTests
{
    [Fact]
    public void AddIdentityAndAccessModule_WithoutTheSettings_KeepsBothSignIns()
    {
        using var provider = BuildProvider([]);

        var options = provider.GetRequiredService<IOptions<InstallationSignInOptions>>().Value;

        Assert.True(options.LocalSignInEnabled);
        Assert.True(options.TenantSsoEnabled);
    }

    [Theory]
    [InlineData("false")]
    [InlineData(" False ")]
    public void AddIdentityAndAccessModule_WithFalse_TurnsTheProvidersOfTenantsOff(string value)
    {
        using var provider = BuildProvider(new Dictionary<string, string?> { [InstallationSignInOptions.TenantSsoEnabledKey] = value });

        var options = provider.GetRequiredService<IOptions<InstallationSignInOptions>>().Value;

        Assert.False(options.TenantSsoEnabled);
        Assert.True(options.LocalSignInEnabled);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("0")]
    public void AddIdentityAndAccessModule_WithAValueOtherThanTrueOrFalseForTheProvidersOfTenants_RefusesTheSettings(string value)
    {
        using var provider = BuildProvider(new Dictionary<string, string?> { [InstallationSignInOptions.TenantSsoEnabledKey] = value });

        var failure = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<InstallationSignInOptions>>().Value);

        Assert.Contains(InstallationSignInOptions.TenantSsoEnabledKey, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData(" FALSE ")]
    public void AddIdentityAndAccessModule_WithFalseAndTheProviderForPlatformAdministrators_TurnsThePasswordSignInOff(string value)
    {
        var settings = WithPlatformAdminProvider();
        settings[InstallationSignInOptions.LocalSignInEnabledKey] = value;
        using var provider = BuildProvider(settings);

        var options = provider.GetRequiredService<IOptions<InstallationSignInOptions>>().Value;

        Assert.False(options.LocalSignInEnabled);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("yes")]
    public void AddIdentityAndAccessModule_WithAValueOtherThanTrueOrFalse_RefusesTheSettings(string value)
    {
        using var provider = BuildProvider(new Dictionary<string, string?> { [InstallationSignInOptions.LocalSignInEnabledKey] = value });

        var failure = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<InstallationSignInOptions>>().Value);

        Assert.Contains(InstallationSignInOptions.LocalSignInEnabledKey, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddIdentityAndAccessModule_WithoutThePasswordSignInAndWithoutTheProviderForPlatformAdministrators_RefusesTheSettings()
    {
        using var provider = BuildProvider(new Dictionary<string, string?> { [InstallationSignInOptions.LocalSignInEnabledKey] = "false" });

        var failure = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<InstallationSignInOptions>>().Value);

        Assert.Contains(InstallationSignInOptions.LocalSignInEnabledKey, failure.Message, StringComparison.Ordinal);
        Assert.Contains(PlatformAdminSsoOptions.AuthorityKey, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddIdentityAndAccessModule_WithThePasswordSignInAndWithoutAProvider_AcceptsTheSettings()
    {
        using var provider = BuildProvider(new Dictionary<string, string?> { [InstallationSignInOptions.LocalSignInEnabledKey] = "true" });

        var options = provider.GetRequiredService<IOptions<InstallationSignInOptions>>().Value;

        Assert.True(options.LocalSignInEnabled);
    }

    private static Dictionary<string, string?> WithPlatformAdminProvider()
    {
        return new Dictionary<string, string?>
        {
            [PlatformAdminSsoOptions.AuthorityKey] = "https://id.example.test/realms/staff",
            [PlatformAdminSsoOptions.ClientIdKey] = "propr",
            [PlatformAdminSsoOptions.ClientSecretKey] = "secret",
            [PlatformAdminSsoOptions.RequiredRoleKey] = "operator",
        };
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIdentityAndAccessModule(configuration);
        return services.BuildServiceProvider();
    }
}
