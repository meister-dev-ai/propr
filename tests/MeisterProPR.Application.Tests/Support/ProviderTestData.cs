// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Tests.Support;

/// <summary>Shared provider-neutral fixture data for application-layer tests.</summary>
public static class ProviderTestData
{
    public static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid SecondaryClientId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static IReadOnlyList<string> ProviderFamilies { get; } =
    [
        "azureDevOps",
        "github",
        "gitLab",
        "forgejo",
    ];

    public static IReadOnlyList<string> ProviderReadinessLevels { get; } =
    [
        "configured",
        "degraded",
        "onboardingReady",
        "workflowComplete",
    ];

    public static IReadOnlyList<ProviderConnectionFixture> Connections { get; } =
    [
        new(
            "azureDevOps",
            "https://dev.azure.com",
            "oauthClientCredentials",
            "Meister Azure DevOps",
            "organization",
            "meister-propr"),
        new("github", "https://github.com", "personalAccessToken", "Acme GitHub", "organization", "acme"),
        new("gitLab", "https://gitlab.example.com", "personalAccessToken", "Acme GitLab", "group", "acme/platform"),
        new("forgejo", "https://codeberg.org", "personalAccessToken", "Acme Forgejo", "organization", "acme-labs"),
    ];

    public static ProviderConnectionFixture AzureDevOps => Connections[0];
    public static ProviderConnectionFixture GitHub => Connections[1];
    public static ProviderConnectionFixture GitLab => Connections[2];
    public static ProviderConnectionFixture Forgejo => Connections[3];

    public static ProviderReviewTargetFixture ReviewTarget(ProviderConnectionFixture connection, int number = 42)
    {
        var repositoryId = connection.ProviderFamily switch
        {
            "azureDevOps" => "repo-ado-1",
            "github" => "repo-gh-1",
            "gitLab" => "repo-gl-1",
            "forgejo" => "repo-fj-1",
            _ => "repo-generic-1",
        };

        var ownerOrNamespace = connection.ScopePath.Contains('/')
            ? connection.ScopePath.Split('/')[0]
            : connection.ScopePath;

        var projectPath = connection.ProviderFamily switch
        {
            "azureDevOps" => "Meister-ProPR",
            "github" => "propr",
            "gitLab" => "platform/propr",
            "forgejo" => "propr-mirror",
            _ => "propr",
        };

        return new ProviderReviewTargetFixture(
            connection.ProviderFamily,
            connection.HostBaseUrl,
            repositoryId,
            ownerOrNamespace,
            projectPath,
            number.ToString(),
            number,
            $"{connection.HostBaseUrl.TrimEnd('/')}/{connection.ScopePath}/pull/{number}",
            "refs/heads/feature/provider-neutral",
            "refs/heads/main");
    }

    public static ReviewRevisionFixture Revision(
        string headSha = "1111111111111111111111111111111111111111",
        string baseSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        string? providerRevisionId = "revision-1")
    {
        return new ReviewRevisionFixture(
            headSha,
            baseSha,
            "0000000000000000000000000000000000000000",
            providerRevisionId,
            $"{baseSha[..8]}..{headSha[..8]}");
    }

    public static ProviderReviewerFixture Reviewer(ProviderConnectionFixture connection)
    {
        return connection.ProviderFamily switch
        {
            "azureDevOps" => new ProviderReviewerFixture("ado-reviewer-1", "meister-bot", "Meister Bot", true),
            "github" => new ProviderReviewerFixture("github-reviewer-1", "meister-dev-bot", "Meister Dev Bot", true),
            "gitLab" => new ProviderReviewerFixture("gitlab-reviewer-1", "meister-reviewer", "Meister Reviewer", true),
            "forgejo" => new ProviderReviewerFixture("forgejo-reviewer-1", "meister-helper", "Meister Helper", true),
            _ => new ProviderReviewerFixture("provider-reviewer-1", "meister-reviewer", "Meister Reviewer", true),
        };
    }

    public static WebhookDeliveryFixture Webhook(ProviderConnectionFixture connection, string eventName)
    {
        return new WebhookDeliveryFixture(
            connection.ProviderFamily,
            connection.HostBaseUrl,
            $"delivery-{connection.ProviderFamily}-1",
            eventName,
            connection.ScopePath,
            ReviewTarget(connection),
            Revision());
    }

    public static ProviderReadinessFixture Readiness(
        ProviderConnectionFixture connection,
        string readinessLevel = "onboardingReady",
        string? hostVariant = null,
        params string[] missingCriteria)
    {
        return new ProviderReadinessFixture(
            readinessLevel,
            hostVariant ?? (connection.HostBaseUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase)
                            || connection.HostBaseUrl.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase)
                            || connection.HostBaseUrl.Contains("gitlab.com", StringComparison.OrdinalIgnoreCase)
                            || connection.HostBaseUrl.Contains("codeberg.org", StringComparison.OrdinalIgnoreCase)
                ? "hosted"
                : "selfHosted"),
            missingCriteria);
    }
}

public sealed record ProviderConnectionFixture(
    string ProviderFamily,
    string HostBaseUrl,
    string AuthenticationKind,
    string DisplayName,
    string ScopeType,
    string ScopePath);

public sealed record ProviderReviewTargetFixture(
    string ProviderFamily,
    string HostBaseUrl,
    string ExternalRepositoryId,
    string OwnerOrNamespace,
    string ProjectPath,
    string ExternalReviewId,
    int Number,
    string WebUrl,
    string SourceBranch,
    string TargetBranch);

public sealed record ReviewRevisionFixture(
    string HeadSha,
    string BaseSha,
    string StartSha,
    string? ProviderRevisionId,
    string PatchIdentity);

public sealed record ProviderReviewerFixture(
    string ExternalUserId,
    string Login,
    string DisplayName,
    bool IsBot);

public sealed record WebhookDeliveryFixture(
    string ProviderFamily,
    string HostBaseUrl,
    string DeliveryId,
    string EventName,
    string ScopePath,
    ProviderReviewTargetFixture Review,
    ReviewRevisionFixture Revision);

public sealed record ProviderReadinessFixture(
    string ReadinessLevel,
    string HostVariant,
    IReadOnlyList<string> MissingCriteria);
