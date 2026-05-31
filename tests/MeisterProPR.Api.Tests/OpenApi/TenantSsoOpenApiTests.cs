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

        Assert.Contains("/admin/tenants", content, StringComparison.Ordinal);
        Assert.Contains("/admin/tenants/{tenantId}/sso-providers", content, StringComparison.Ordinal);
        Assert.Contains("/admin/tenants/{tenantId}/memberships", content, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/admin/tenants", content, StringComparison.Ordinal);
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

    [Fact]
    public async Task OpenApi_DocumentsProtocolTokenOptimizationDiagnostics()
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

        Assert.Contains("\"CacheCallStatus\"", content, StringComparison.Ordinal);
        Assert.Contains("\"CacheObservabilityStatus\"", content, StringComparison.Ordinal);
        Assert.Contains("\"PrefixEligibilityStatus\"", content, StringComparison.Ordinal);
        Assert.Contains("\"ProtocolToolEvidenceDto\"", content, StringComparison.Ordinal);
        Assert.Contains("\"cachedInputTokens\"", content, StringComparison.Ordinal);
        Assert.Contains("\"cacheStatus\"", content, StringComparison.Ordinal);
        Assert.Contains("\"toolEvidence\"", content, StringComparison.Ordinal);
        Assert.Contains("\"finalizationAttemptKind\"", content, StringComparison.Ordinal);
        Assert.Contains("\"totalCachedInputTokens\"", content, StringComparison.Ordinal);
        Assert.Contains("\"cacheObservability\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenApi_DocumentsClientReviewProfileEndpoints()
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

        Assert.Contains("/admin/review-profiles", content, StringComparison.Ordinal);
        Assert.Contains("/admin/clients/{clientId}/review-profile", content, StringComparison.Ordinal);
        Assert.Contains("\"ReviewProfileCatalogResponse\"", content, StringComparison.Ordinal);
        Assert.Contains("\"ClientReviewProfileResponse\"", content, StringComparison.Ordinal);
    }
}
