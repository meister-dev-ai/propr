// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.IdentityAndAccess;

public sealed class ProvisionedUserSsoOptionsValidatorTests
{
    private const string Authority = "https://id.example.test/realms/customers";

    [Fact]
    public void Validate_WithoutAnySetting_Succeeds()
    {
        var result = new ProvisionedUserSsoOptionsValidator(false).Validate(null, new ProvisionedUserSsoOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_WithACompleteConfiguration_Succeeds()
    {
        var result = new ProvisionedUserSsoOptionsValidator(false).Validate(null, Complete());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_WithSettingsButWithoutAuthority_FailsAndNamesTheSettings()
    {
        var options = Complete();
        options.Authority = null;

        var result = new ProvisionedUserSsoOptionsValidator(false).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(ProvisionedUserSsoOptions.ClientIdKey, result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains(ProvisionedUserSsoOptions.AuthorityKey, result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProvisionedUserSsoOptions.ClientIdKey)]
    [InlineData(ProvisionedUserSsoOptions.ClientSecretKey)]
    public void Validate_WithoutARequiredSetting_FailsAndNamesIt(string missingKey)
    {
        var options = Complete();
        if (missingKey == ProvisionedUserSsoOptions.ClientIdKey)
        {
            options.ClientId = null;
        }
        else
        {
            options.ClientSecret = null;
        }

        var result = new ProvisionedUserSsoOptionsValidator(false).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(missingKey, result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://id.example.test/realms/customers")]
    [InlineData("https://user:secret@id.example.test/realms/customers")]
    [InlineData("https://id.example.test/realms/customers?realm=customers")]
    public void Validate_OutsideDevelopment_RefusesAnAuthorityThatIsNotAPlainHttpsUrl(string authority)
    {
        var options = Complete();
        options.Authority = authority;

        var result = new ProvisionedUserSsoOptionsValidator(false).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(ProvisionedUserSsoOptions.AuthorityKey, result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_OutsideDevelopment_RefusesAnHttpMetadataUrl()
    {
        var options = Complete();
        options.MetadataUrl = "http://keycloak:8080/realms/customers/.well-known/openid-configuration";

        var result = new ProvisionedUserSsoOptionsValidator(false).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(ProvisionedUserSsoOptions.MetadataUrlKey, result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WithADisplayNameOverTwoLines_Fails()
    {
        var options = Complete();
        options.DisplayName = "Meister DEV\ncustomers";

        var result = new ProvisionedUserSsoOptionsValidator(false).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(ProvisionedUserSsoOptions.DisplayNameKey, result.FailureMessage, StringComparison.Ordinal);
    }

    private static ProvisionedUserSsoOptions Complete()
    {
        return new ProvisionedUserSsoOptions
        {
            Authority = Authority,
            ClientId = "propr-cell",
            ClientSecret = "cell-client-secret",
            DisplayName = "Meister DEV",
        };
    }
}
