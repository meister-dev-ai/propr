// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Options;

namespace MeisterDev.ProPR.Application.Tests.Options;

public sealed class ProvisionedUserSsoOptionsTests
{
    /// <summary>
    ///     Providers differ in whether their issuer ends with a slash. ProPR records the identities of provisioned users
    ///     under the authority without one, so both ways of writing the authority name the same users.
    /// </summary>
    [Theory]
    [InlineData("https://id.example.test/realms/customers")]
    [InlineData("https://id.example.test/realms/customers/")]
    public void IdentityIssuer_IsTheAuthorityWithoutATrailingSlash(string authority)
    {
        var options = new ProvisionedUserSsoOptions { Authority = authority };

        Assert.Equal("https://id.example.test/realms/customers", options.IdentityIssuer);
    }

    [Fact]
    public void IdentityIssuer_WithoutAuthority_IsNull()
    {
        Assert.Null(new ProvisionedUserSsoOptions().IdentityIssuer);
    }

    [Fact]
    public void ToString_RendersTheAddressesWithoutPathQueryAndTheClientSecret()
    {
        var options = new ProvisionedUserSsoOptions
        {
            Authority = "https://id.example.test/realms/customers",
            MetadataUrl = "https://keycloak.internal.test/realms/customers/.well-known/openid-configuration?key=query-secret",
            ClientId = "propr-cell",
            ClientSecret = "client-secret-value",
        };

        var rendered = options.ToString();

        Assert.Contains("https://id.example.test", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("/realms/customers", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("client-secret-value", rendered, StringComparison.Ordinal);
    }
}
