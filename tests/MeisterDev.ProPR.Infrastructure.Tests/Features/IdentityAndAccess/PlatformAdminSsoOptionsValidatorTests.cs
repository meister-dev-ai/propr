// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.IdentityAndAccess;

public sealed class PlatformAdminSsoOptionsValidatorTests
{
    private const string Authority = "https://id.example.test/realms/staff";

    [Fact]
    public void Validate_WithoutAnySetting_Succeeds()
    {
        var result = new PlatformAdminSsoOptionsValidator(false).Validate(null, new PlatformAdminSsoOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_WithACompleteConfiguration_Succeeds()
    {
        var result = new PlatformAdminSsoOptionsValidator(false).Validate(null, Complete());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_WithSettingsButWithoutAuthority_FailsAndNamesTheSettings()
    {
        var options = Complete();
        options.Authority = null;

        var result = new PlatformAdminSsoOptionsValidator(false).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(PlatformAdminSsoOptions.ClientIdKey, result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains(PlatformAdminSsoOptions.AuthorityKey, result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PlatformAdminSsoOptions.ClientIdKey)]
    [InlineData(PlatformAdminSsoOptions.ClientSecretKey)]
    [InlineData(PlatformAdminSsoOptions.RequiredRoleKey)]
    public void Validate_WithoutARequiredSetting_FailsAndNamesIt(string missingKey)
    {
        var options = Complete();
        switch (missingKey)
        {
            case PlatformAdminSsoOptions.ClientIdKey:
                options.ClientId = null;
                break;
            case PlatformAdminSsoOptions.ClientSecretKey:
                options.ClientSecret = null;
                break;
            default:
                options.RequiredRole = null;
                break;
        }

        var result = new PlatformAdminSsoOptionsValidator(false).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(missingKey, result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://id.example.test/realms/staff")]
    [InlineData("/realms/staff")]
    [InlineData("https://user:secret@id.example.test/realms/staff")]
    [InlineData("https://id.example.test/realms/staff?realm=staff")]
    [InlineData("https://id.example.test/realms/staff#fragment")]
    public void Validate_OutsideDevelopment_RefusesAnAuthorityThatIsNotAPlainHttpsUrl(string authority)
    {
        var options = Complete();
        options.Authority = authority;

        var result = new PlatformAdminSsoOptionsValidator(false).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(PlatformAdminSsoOptions.AuthorityKey, result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_InDevelopment_AcceptsAnHttpAuthority()
    {
        var options = Complete();
        options.Authority = "http://localhost:8080/realms/staff";

        var result = new PlatformAdminSsoOptionsValidator(true).Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_AcceptsAMetadataUrlWithAQuery()
    {
        var options = Complete();
        options.MetadataUrl = $"{Authority}/.well-known/openid-configuration?appid=propr";

        var result = new PlatformAdminSsoOptionsValidator(false).Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_OutsideDevelopment_RefusesAnHttpMetadataUrl()
    {
        var options = Complete();
        options.MetadataUrl = "http://keycloak:8080/realms/staff/.well-known/openid-configuration";

        var result = new PlatformAdminSsoOptionsValidator(false).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(PlatformAdminSsoOptions.MetadataUrlKey, result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WithADisplayNameOverTwoLines_Fails()
    {
        var options = Complete();
        options.DisplayName = "Meister DEV\nstaff";

        var result = new PlatformAdminSsoOptionsValidator(false).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(PlatformAdminSsoOptions.DisplayNameKey, result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AddIdentityAndAccessModule_WithAnInvalidSetting_RefusesTheOptions()
    {
        using var provider = BuildProvider(
            new Dictionary<string, string?>
            {
                [PlatformAdminSsoOptions.AuthorityKey] = "http://id.example.test/realms/staff",
                [PlatformAdminSsoOptions.ClientIdKey] = "propr",
                [PlatformAdminSsoOptions.ClientSecretKey] = "secret",
                [PlatformAdminSsoOptions.RequiredRoleKey] = "operator",
            });

        var failure = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<PlatformAdminSsoOptions>>().Value);
        Assert.Contains(PlatformAdminSsoOptions.AuthorityKey, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddIdentityAndAccessModule_ReadsTheSettingsAndAppliesTheDefaults()
    {
        using var provider = BuildProvider(
            new Dictionary<string, string?>
            {
                [PlatformAdminSsoOptions.AuthorityKey] = $" {Authority} ",
                [PlatformAdminSsoOptions.ClientIdKey] = "propr",
                [PlatformAdminSsoOptions.ClientSecretKey] = "client-secret-value\n",
                [PlatformAdminSsoOptions.RequiredRoleKey] = "operator",
            });

        var options = provider.GetRequiredService<IOptions<PlatformAdminSsoOptions>>().Value;

        Assert.Equal(Authority, options.Authority);
        Assert.Equal("client-secret-value", options.ClientSecret);
        Assert.Equal(PlatformAdminSsoOptions.DefaultRoleClaim, options.EffectiveRoleClaim);
        Assert.Equal($"{Authority}/.well-known/openid-configuration", options.MetadataAddress);
        Assert.Equal("id.example.test", options.EffectiveDisplayName);
        Assert.DoesNotContain("client-secret-value", options.ToString(), StringComparison.Ordinal);
    }

    private static PlatformAdminSsoOptions Complete()
    {
        return new PlatformAdminSsoOptions
        {
            Authority = Authority,
            ClientId = "propr",
            ClientSecret = "secret",
            RequiredRole = "operator",
            DisplayName = "Meister DEV staff",
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
