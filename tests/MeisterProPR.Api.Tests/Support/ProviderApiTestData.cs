// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Nodes;

namespace MeisterProPR.Api.Tests.Support;

/// <summary>Provider-neutral request payload fixtures for API and integration tests.</summary>
public static class ProviderApiTestData
{
    public static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static JsonObject CreateConnectionRequest(
        string providerFamily = "github",
        string hostBaseUrl = "https://github.com",
        string authenticationKind = "personalAccessToken",
        string displayName = "Acme GitHub")
    {
        return new JsonObject
        {
            ["providerFamily"] = providerFamily,
            ["hostBaseUrl"] = hostBaseUrl,
            ["authenticationKind"] = authenticationKind,
            ["displayName"] = displayName,
            ["secretMaterial"] = "provider-secret-token",
        };
    }

    public static JsonObject CreateScopeRequest(
        string scopeType = "organization",
        string externalScopeId = "acme",
        string scopePath = "acme",
        string displayName = "Acme")
    {
        return new JsonObject
        {
            ["scopeType"] = scopeType,
            ["externalScopeId"] = externalScopeId,
            ["scopePath"] = scopePath,
            ["displayName"] = displayName,
            ["isEnabled"] = true,
        };
    }

    public static JsonObject CreateReviewerIdentityRequest(
        string externalUserId = "github-reviewer-1",
        string login = "meister-dev-bot",
        string displayName = "Meister Dev Bot",
        bool isBot = true)
    {
        return new JsonObject
        {
            ["externalUserId"] = externalUserId,
            ["login"] = login,
            ["displayName"] = displayName,
            ["isBot"] = isBot,
        };
    }

    public static JsonObject CreateProviderOperationalStatusResponse(
        string providerFamily = "github",
        string readinessLevel = "onboardingReady",
        string statusReason =
            "Connection is verified for onboarding, but workflow-complete readiness criteria are still missing.")
    {
        return new JsonObject
        {
            ["connections"] = new JsonArray
            {
                new JsonObject
                {
                    ["connectionId"] = Guid.NewGuid().ToString("D"),
                    ["providerFamily"] = providerFamily,
                    ["displayName"] = "Fixture Provider Connection",
                    ["hostBaseUrl"] = "https://github.com",
                    ["hostVariant"] = "hosted",
                    ["isActive"] = true,
                    ["verificationStatus"] = "verified",
                    ["readinessLevel"] = readinessLevel,
                    ["readinessReason"] = statusReason,
                    ["missingReadinessCriteria"] = new JsonArray("Configured reviewer identity is required for workflow-complete readiness."),
                    ["health"] = "degraded",
                    ["lastCheckedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["failureCategory"] = null,
                    ["statusReason"] = statusReason,
                },
            },
            ["providerFamilies"] = new JsonArray(),
        };
    }

    public static JsonObject CreateReviewSubmissionRequest(
        string providerFamily = "github",
        string hostBaseUrl = "https://github.com",
        string repositoryId = "repo-gh-1",
        string reviewId = "42",
        string headSha = "1111111111111111111111111111111111111111",
        string baseSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
    {
        return new JsonObject
        {
            ["providerFamily"] = providerFamily,
            ["hostBaseUrl"] = hostBaseUrl,
            ["repositoryReference"] = new JsonObject
            {
                ["externalRepositoryId"] = repositoryId,
                ["ownerOrNamespace"] = "acme",
                ["projectPath"] = "acme/propr",
                ["displayName"] = "propr",
            },
            ["codeReviewReference"] = new JsonObject
            {
                ["externalReviewId"] = reviewId,
                ["number"] = 42,
                ["webUrl"] = $"https://github.com/acme/propr/pull/{reviewId}",
                ["sourceBranch"] = "refs/heads/feature/provider-neutral",
                ["targetBranch"] = "refs/heads/main",
            },
            ["reviewRevision"] = new JsonObject
            {
                ["headSha"] = headSha,
                ["baseSha"] = baseSha,
                ["startSha"] = "0000000000000000000000000000000000000000",
                ["providerRevisionId"] = "revision-1",
                ["patchIdentity"] = $"{baseSha[..8]}..{headSha[..8]}",
            },
            ["requestedReviewerIdentity"] = CreateReviewerIdentityRequest(),
        };
    }
}
