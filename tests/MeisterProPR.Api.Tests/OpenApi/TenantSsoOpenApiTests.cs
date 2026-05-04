// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Api.Tests.OpenApi;

public sealed class TenantSsoOpenApiTests
{
    [Fact]
    public async Task OpenApi_ContainsTenantAdministrationAndTenantAuthPaths()
    {
        var openApiPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "openapi.json"));
        var content = await File.ReadAllTextAsync(openApiPath);

        Assert.Contains("/api/admin/tenants", content, StringComparison.Ordinal);
        Assert.Contains("/api/admin/tenants/{tenantId}/sso-providers", content, StringComparison.Ordinal);
        Assert.Contains("/api/admin/tenants/{tenantId}/memberships", content, StringComparison.Ordinal);
        Assert.DoesNotContain("UpsertTenantMembershipRequest", content, StringComparison.Ordinal);
        Assert.Contains("/auth/tenants/{tenantSlug}/providers", content, StringComparison.Ordinal);
        Assert.Contains("/auth/tenants/{tenantSlug}/local-login", content, StringComparison.Ordinal);
        Assert.Contains("/auth/external/challenge/{tenantSlug}/{providerId}", content, StringComparison.Ordinal);
        Assert.Contains("/auth/external/callback/{tenantSlug}", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenApi_DocumentsExternalCallbackStateAndAuthorizationCodeParameters()
    {
        var openApiPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "openapi.json"));
        var content = await File.ReadAllTextAsync(openApiPath);

        Assert.Contains("/auth/external/callback/{tenantSlug}", content, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"state\"", content, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"code\"", content, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"error\"", content, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"error_description\"", content, StringComparison.Ordinal);
    }
}
