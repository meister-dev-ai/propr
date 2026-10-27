// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Options;

namespace MeisterDev.ProPR.Application.Tests.Options;

public sealed class PlatformAdminSsoOptionsTests
{
    /// <summary>
    ///     The metadata URL may carry a query, and a query or a path can hold credential material, so the rendering of
    ///     the settings keeps only scheme, host and port of both addresses.
    /// </summary>
    [Fact]
    public void ToString_RendersTheAddressesWithoutPathQueryAndTheClientSecret()
    {
        var options = new PlatformAdminSsoOptions
        {
            Authority = "https://id.example.test/realms/staff",
            MetadataUrl = "https://keycloak.internal.test/realms/staff/.well-known/openid-configuration?key=query-secret",
            ClientId = "propr-platform",
            ClientSecret = "client-secret-value",
            RequiredRole = "operator",
        };

        var rendered = options.ToString();

        Assert.Contains("https://id.example.test", rendered, StringComparison.Ordinal);
        Assert.Contains("https://keycloak.internal.test", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("/realms/staff", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("client-secret-value", rendered, StringComparison.Ordinal);
    }
}
