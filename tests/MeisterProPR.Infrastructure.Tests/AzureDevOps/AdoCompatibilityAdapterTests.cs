// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Azure.Core;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

public sealed class AdoCompatibilityAdapterTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    [Fact]
    public void AzureDevOpsProviderAdapters_LiveOutsideCompatibilityFolder()
    {
        Assert.False(
            Directory.Exists(
                Path.Combine(
                    RepoRoot,
                    "src/MeisterProPR.Infrastructure/Features/Providers/AzureDevOps/Compatibility")));
        Assert.True(
            File.Exists(
                Path.Combine(
                    RepoRoot,
                    "src/MeisterProPR.Infrastructure/Features/Providers/AzureDevOps/DependencyInjection/AzureDevOpsProviderServiceCollectionExtensions.cs")));
        Assert.False(
            File.Exists(
                Path.Combine(
                    RepoRoot,
                    "src/MeisterProPR.Infrastructure/Features/Providers/AzureDevOps/AzureDevOpsProviderAdapters.cs")));
        Assert.True(
            File.Exists(
                Path.Combine(
                    RepoRoot,
                    "src/MeisterProPR.Infrastructure/Features/Providers/AzureDevOps/Discovery/AdoRepositoryDiscoveryProvider.cs")));
        Assert.True(
            File.Exists(
                Path.Combine(
                    RepoRoot,
                    "src/MeisterProPR.Infrastructure/Features/Providers/AzureDevOps/Identity/AdoReviewerIdentityService.cs")));
        Assert.True(
            File.Exists(
                Path.Combine(
                    RepoRoot,
                    "src/MeisterProPR.Infrastructure/Features/Providers/AzureDevOps/Reviewing/AdoCodeReviewQueryService.cs")));
        Assert.True(
            File.Exists(
                Path.Combine(
                    RepoRoot,
                    "src/MeisterProPR.Infrastructure/Features/Providers/AzureDevOps/Reviewing/AdoCodeReviewPublicationService.cs")));
        Assert.True(
            File.Exists(
                Path.Combine(
                    RepoRoot,
                    "src/MeisterProPR.Infrastructure/Features/Providers/AzureDevOps/Reviewing/AdoReviewDiscoveryProvider.cs")));
        Assert.True(
            File.Exists(
                Path.Combine(
                    RepoRoot,
                    "src/MeisterProPR.Infrastructure/Features/Providers/AzureDevOps/Runtime/AdoWebhookIngressService.cs")));
        Assert.True(
            File.Exists(
                Path.Combine(
                    RepoRoot,
                    "src/MeisterProPR.Infrastructure/Features/Providers/AzureDevOps/Support/AdoProviderAdapterHelpers.cs")));
    }

    [Fact]
    public async Task GetReviewAsync_ProbesEnabledOrganizationScopesUntilReviewIsResolved()
    {
        var clientId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid().ToString("D");
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/org-one");
        var repository = new RepositoryRef(host, repositoryId, "project-1", "project-1");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var (connectionRepository, scopeRepository) = CreateProviderRepositories(
            clientId,
            CreateScope(clientId, "https://dev.azure.com/org-one"),
            CreateScope(clientId, "https://dev.azure.com/org-two"));
        var attemptedOrganizations = new List<string>();

        var firstClient = CreateGitClient("https://dev.azure.com/org-one");
        firstClient.GetPullRequestAsync(
                Arg.Any<string>(),
                repositoryId,
                review.Number,
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns<GitPullRequest>(_ => throw new InvalidOperationException("Scope mismatch."));

        var secondClient = CreateGitClient("https://dev.azure.com/org-two");
        secondClient.GetPullRequestAsync(
                Arg.Any<string>(),
                repositoryId,
                review.Number,
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    CreatePullRequest(
                        repositoryId,
                        review.Number,
                        "Provider-neutral Azure DevOps review",
                        "reviewer-guid",
                        "meister-bot")));
        secondClient.GetPullRequestIterationsAsync(
                Arg.Any<string>(),
                repositoryId,
                review.Number,
                Arg.Any<bool?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<GitPullRequestIteration>()));

        var sut = new AdoCodeReviewQueryService(
            connectionRepository,
            scopeRepository,
            CreateConnectionFactory())
        {
            GitClientResolver = (organizationUrl, _) =>
            {
                attemptedOrganizations.Add(organizationUrl);
                return Task.FromResult(
                    organizationUrl.EndsWith("org-one", StringComparison.OrdinalIgnoreCase)
                        ? firstClient
                        : secondClient);
            },
        };

        var result = await sut.GetReviewAsync(clientId, review, CancellationToken.None);

        Assert.Equal(
            ["https://dev.azure.com/org-one", "https://dev.azure.com/org-two"],
            attemptedOrganizations);
        Assert.NotNull(result);
        Assert.Equal(ScmProvider.AzureDevOps, result!.Provider);
        Assert.Equal(review.Number, result.CodeReview.Number);
        Assert.Equal(CodeReviewState.Open, result.ReviewState);
        Assert.Equal("Provider-neutral Azure DevOps review", result.Title);
        Assert.Equal("feature/providers", result.SourceBranch);
        Assert.Equal("main", result.TargetBranch);
        Assert.Null(result.ReviewRevision);
        Assert.NotNull(result.RequestedReviewerIdentity);
        Assert.Equal("meister-bot", result.RequestedReviewerIdentity!.Login);
    }

    [Fact]
    public async Task GetReviewAsync_UsesProviderBackedOrganizationScopes()
    {
        await using var db = CreateContext();
        var clientId = await SeedClientAsync(db);
        var connectionId = await SeedAzureConnectionAsync(db, clientId);
        await SeedAzureScopeAsync(db, clientId, connectionId, "https://dev.azure.com/org-one");
        await SeedAzureScopeAsync(db, clientId, connectionId, "https://dev.azure.com/org-two");

        var repositoryId = Guid.NewGuid().ToString("D");
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/org-one");
        var repository = new RepositoryRef(host, repositoryId, "project-1", "project-1");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var attemptedOrganizations = new List<string>();

        var firstClient = CreateGitClient("https://dev.azure.com/org-one");
        firstClient.GetPullRequestAsync(
                Arg.Any<string>(),
                repositoryId,
                review.Number,
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns<GitPullRequest>(_ => throw new InvalidOperationException("Scope mismatch."));

        var secondClient = CreateGitClient("https://dev.azure.com/org-two");
        secondClient.GetPullRequestAsync(
                Arg.Any<string>(),
                repositoryId,
                review.Number,
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<bool?>(),
                Arg.Any<bool?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    CreatePullRequest(
                        repositoryId,
                        review.Number,
                        "Provider-backed Azure DevOps review",
                        "reviewer-guid",
                        "meister-bot")));
        secondClient.GetPullRequestIterationsAsync(
                Arg.Any<string>(),
                repositoryId,
                review.Number,
                Arg.Any<bool?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<GitPullRequestIteration>()));

        var connectionRepository = CreateConnectionRepository(clientId, connectionId, "https://dev.azure.com");
        var sut = new AdoCodeReviewQueryService(
            connectionRepository,
            new ClientScmScopeRepository(db),
            CreateConnectionFactory())
        {
            GitClientResolver = (organizationUrl, _) =>
            {
                attemptedOrganizations.Add(organizationUrl);
                return Task.FromResult(
                    organizationUrl.EndsWith("org-one", StringComparison.OrdinalIgnoreCase)
                        ? firstClient
                        : secondClient);
            },
        };

        var result = await sut.GetReviewAsync(clientId, review, CancellationToken.None);

        Assert.Equal(
            ["https://dev.azure.com/org-one", "https://dev.azure.com/org-two"],
            attemptedOrganizations);
        Assert.NotNull(result);
        Assert.Equal("Provider-backed Azure DevOps review", result!.Title);
    }

    [Fact]
    public async Task PublishReviewAsync_UsesSharedScopeResolutionAndRevisionIteration()
    {
        var clientId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid().ToString("D");
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/org-one");
        var repository = new RepositoryRef(host, repositoryId, "project-1", "project-1");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "base-sha", "7", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "reviewer-guid", "meister-bot", "Meister Bot", true);
        var result = new ReviewResult("Looks solid.", []);
        var (connectionRepository, scopeRepository) = CreateProviderRepositories(
            clientId,
            CreateScope(clientId, "https://dev.azure.com/org-one"),
            CreateScope(clientId, "https://dev.azure.com/org-two"));
        var commentPoster = Substitute.For<IAdoCommentPoster>();

        commentPoster.PostAsync(
                "https://dev.azure.com/org-one",
                "project-1",
                repositoryId,
                review.Number,
                7,
                result,
                clientId,
                Arg.Is<IReadOnlyList<PrCommentThread>?>(threads => threads == null),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ReviewCommentPostingDiagnosticsDto>(new InvalidOperationException("Scope mismatch.")));
        commentPoster.PostAsync(
                "https://dev.azure.com/org-two",
                "project-1",
                repositoryId,
                review.Number,
                7,
                result,
                clientId,
                Arg.Is<IReadOnlyList<PrCommentThread>?>(threads => threads == null),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReviewCommentPostingDiagnosticsDto.Empty()));

        var sut = new AdoCodeReviewPublicationService(
            connectionRepository,
            scopeRepository,
            CreateConnectionFactory(),
            commentPoster);

        await sut.PublishReviewAsync(clientId, review, revision, result, reviewer, CancellationToken.None);

        await commentPoster.Received(1)
            .PostAsync(
                "https://dev.azure.com/org-two",
                "project-1",
                repositoryId,
                review.Number,
                7,
                result,
                clientId,
                Arg.Is<IReadOnlyList<PrCommentThread>?>(threads => threads == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListOpenReviewsAsync_FiltersToMatchingRepositoryAndReviewer()
    {
        var clientId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid().ToString("D");
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/org-one");
        var repository = new RepositoryRef(host, repositoryId, "project-1", "project-1");
        var reviewerId = Guid.NewGuid();
        var reviewer = new ReviewerIdentity(host, reviewerId.ToString("D"), "meister-bot", "Meister Bot", true);
        var (connectionRepository, scopeRepository) = CreateProviderRepositories(
            clientId,
            CreateScope(clientId, "https://dev.azure.com/org-one"));
        var gitClient = CreateGitClient("https://dev.azure.com/org-one");

        gitClient.GetPullRequestsByProjectAsync(
                Arg.Any<string>(),
                Arg.Any<GitPullRequestSearchCriteria>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<int?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    new List<GitPullRequest>
                    {
                        CreatePullRequest(repositoryId, 42, "Matching review", reviewer.ExternalUserId, reviewer.Login),
                        CreatePullRequest(
                            Guid.NewGuid().ToString("D"),
                            43,
                            "Different repository",
                            reviewer.ExternalUserId,
                            reviewer.Login),
                        CreatePullRequest(
                            repositoryId,
                            44,
                            "Different reviewer",
                            Guid.NewGuid().ToString("D"),
                            "other-reviewer"),
                    }));
        gitClient.GetPullRequestIterationsAsync(
                Arg.Any<string>(),
                repositoryId,
                42,
                Arg.Any<bool?>(),
                Arg.Any<object>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<GitPullRequestIteration>()));

        var sut = new AdoReviewDiscoveryProvider(
            connectionRepository,
            scopeRepository,
            CreateConnectionFactory())
        {
            GitClientResolver = (_, _) => Task.FromResult(gitClient),
        };

        var items = await sut.ListOpenReviewsAsync(clientId, repository, reviewer, CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal(42, item.CodeReview.Number);
        Assert.Equal("Matching review", item.Title);
        Assert.Equal(CodeReviewState.Open, item.ReviewState);
        Assert.Null(item.ReviewRevision);
        Assert.Equal("meister-bot", item.RequestedReviewerIdentity!.Login);
    }

    [Fact]
    public async Task ResolveCandidatesAsync_UsesProviderBackedOrganizationScopes()
    {
        await using var db = CreateContext();
        var clientId = await SeedClientAsync(db);
        var connectionId = await SeedAzureConnectionAsync(db, clientId);
        await SeedAzureScopeAsync(db, clientId, connectionId, "https://dev.azure.com/org-one");
        await SeedAzureScopeAsync(db, clientId, connectionId, "https://dev.azure.com/org-two");
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/org-one");
        var identityResolver = Substitute.For<IIdentityResolver>();

        identityResolver.ResolveAsync(
                "https://dev.azure.com/org-one",
                "meister",
                clientId,
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<ResolvedIdentity>>(
                [
                    new ResolvedIdentity(Guid.Parse("10000000-0000-0000-0000-000000000101"), "Meister Bot"),
                ]));
        identityResolver.ResolveAsync(
                "https://dev.azure.com/org-two",
                "meister",
                clientId,
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<ResolvedIdentity>>(
                [
                    new ResolvedIdentity(Guid.Parse("10000000-0000-0000-0000-000000000102"), "Meister Backup"),
                ]));

        var sut = new AdoReviewerIdentityService(
            CreateConnectionRepository(clientId, connectionId, "https://dev.azure.com"),
            new ClientScmScopeRepository(db),
            identityResolver);

        var result = await sut.ResolveCandidatesAsync(clientId, host, "meister", CancellationToken.None);

        Assert.Collection(
            result.OrderBy(identity => identity.DisplayName, StringComparer.OrdinalIgnoreCase),
            first =>
            {
                Assert.Equal("Meister Backup", first.DisplayName);
                Assert.Equal("Meister Backup", first.Login);
            },
            second =>
            {
                Assert.Equal("Meister Bot", second.DisplayName);
                Assert.Equal("Meister Bot", second.Login);
            });
    }

    [Fact]
    public async Task VerifyAsync_UsesWebhookConfigurationSecretFromSharedIngressContract()
    {
        var basicAuthVerifier = Substitute.For<IAdoWebhookBasicAuthVerifier>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        var sut = new AdoWebhookIngressService(basicAuthVerifier, new AdoWebhookPayloadParser(), clientRegistry);
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/org-one");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Basic valid",
        };

        basicAuthVerifier.IsAuthorized("Basic valid", "ciphertext").Returns(true);

        var result = await sut.VerifyAsync(Guid.NewGuid(), host, headers, "{}", "ciphertext", CancellationToken.None);

        Assert.True(result);
        basicAuthVerifier.Received(1).IsAuthorized("Basic valid", "ciphertext");
    }

    [Fact]
    public async Task ParseAsync_MapsReviewerAssignmentThroughSharedWebhookEnvelope()
    {
        var clientId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid().ToString("D");
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/org-one");
        var clientRegistry = Substitute.For<IClientRegistry>();
        var sut = new AdoWebhookIngressService(
            Substitute.For<IAdoWebhookBasicAuthVerifier>(),
            new AdoWebhookPayloadParser(),
            clientRegistry);

        clientRegistry.GetReviewerIdAsync(clientId, Arg.Any<CancellationToken>()).Returns(reviewerId);

        var payload = $$"""
                        {
                          "id": "delivery-42",
                          "eventType": "git.pullrequest.updated",
                          "resource": {
                            "pullRequestId": 42,
                            "status": "active",
                            "sourceRefName": "refs/heads/feature/providers",
                            "targetRefName": "refs/heads/main",
                            "repository": {
                              "id": "{{repositoryId}}",
                              "name": "MeisterProPR",
                              "project": {
                                "id": "project-1",
                                "name": "Project One"
                              }
                            },
                            "reviewers": [
                              {
                                "id": "{{reviewerId:D}}"
                              }
                            ]
                          }
                        }
                        """;

        var envelope = await sut.ParseAsync(
            clientId,
            host,
            new Dictionary<string, string>(),
            payload,
            CancellationToken.None);

        Assert.Equal("delivery-42", envelope.DeliveryId);
        Assert.Equal("reviewer_assignment", envelope.DeliveryKind);
        Assert.Equal("git.pullrequest.updated", envelope.EventName);
        Assert.Equal(repositoryId, envelope.Repository!.ExternalRepositoryId);
        Assert.Equal("project-1", envelope.Repository.OwnerOrNamespace);
        Assert.Equal("project-1", envelope.Repository.ProjectPath);
        Assert.Equal(42, envelope.Review!.Number);
        Assert.Equal("refs/heads/feature/providers", envelope.SourceBranch);
        Assert.Equal("refs/heads/main", envelope.TargetBranch);
    }

    private static VssConnectionFactory CreateConnectionFactory()
    {
        return new VssConnectionFactory(Substitute.For<TokenCredential>());
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"TestDb_AdoCompatibility_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new MeisterProPRDbContext(options);
    }

    private static async Task<Guid> SeedClientAsync(MeisterProPRDbContext db)
    {
        var id = Guid.NewGuid();
        db.Clients.Add(
            new ClientRecord
            {
                Id = id,
                DisplayName = "Test Client",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> SeedAzureConnectionAsync(MeisterProPRDbContext db, Guid clientId)
    {
        var connectionId = Guid.NewGuid();
        db.ClientScmConnections.Add(
            new ClientScmConnectionRecord
            {
                Id = connectionId,
                ClientId = clientId,
                Provider = ScmProvider.AzureDevOps,
                HostBaseUrl = "https://dev.azure.com",
                AuthenticationKind = ScmAuthenticationKind.OAuthClientCredentials,
                OAuthTenantId = "contoso.onmicrosoft.com",
                OAuthClientId = "11111111-1111-1111-1111-111111111111",
                DisplayName = "Azure DevOps",
                EncryptedSecretMaterial = "protected-secret",
                VerificationStatus = "verified",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();
        return connectionId;
    }

    private static async Task<Guid> SeedAzureScopeAsync(
        MeisterProPRDbContext db,
        Guid clientId,
        Guid connectionId,
        string organizationUrl)
    {
        var scopeId = Guid.NewGuid();
        db.ClientScmScopes.Add(
            new ClientScmScopeRecord
            {
                Id = scopeId,
                ClientId = clientId,
                ConnectionId = connectionId,
                ScopeType = "organization",
                ExternalScopeId = organizationUrl.Split('/').Last(),
                ScopePath = organizationUrl,
                DisplayName = organizationUrl,
                VerificationStatus = "verified",
                IsEnabled = true,
                LastVerifiedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();
        return scopeId;
    }

    private static (IClientScmConnectionRepository ConnectionRepository, IClientScmScopeRepository ScopeRepository)
        CreateProviderRepositories(
            Guid clientId,
            params ClientAdoOrganizationScopeDto[] scopes)
    {
        var connectionId = Guid.NewGuid();
        var connectionRepository = CreateConnectionRepository(clientId, connectionId, "https://dev.azure.com");
        var scopeRepository = Substitute.For<IClientScmScopeRepository>();
        var providerScopes = scopes
            .Select(scope => new ClientScmScopeDto(
                scope.Id,
                scope.ClientId,
                connectionId,
                "organization",
                scope.OrganizationUrl.Split('/').Last(),
                scope.OrganizationUrl,
                scope.DisplayName ?? scope.OrganizationUrl,
                MapVerificationStatus(scope.VerificationStatus),
                scope.IsEnabled,
                scope.LastVerifiedAt,
                scope.LastVerificationError,
                scope.CreatedAt,
                scope.UpdatedAt))
            .ToList()
            .AsReadOnly();

        scopeRepository.GetByConnectionIdAsync(clientId, connectionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ClientScmScopeDto>>(providerScopes));
        return (connectionRepository, scopeRepository);
    }

    private static IClientScmConnectionRepository CreateConnectionRepository(
        Guid clientId,
        Guid connectionId,
        string hostBaseUrl)
    {
        var repository = Substitute.For<IClientScmConnectionRepository>();
        var now = DateTimeOffset.UtcNow;
        var connection = new ClientScmConnectionDto(
            connectionId,
            clientId,
            ScmProvider.AzureDevOps,
            hostBaseUrl,
            ScmAuthenticationKind.OAuthClientCredentials,
            "Azure DevOps",
            true,
            "verified",
            now,
            null,
            null,
            now,
            now);

        repository.GetByClientIdAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ClientScmConnectionDto>>([connection]));
        return repository;
    }

    private static string MapVerificationStatus(AdoOrganizationVerificationStatus verificationStatus)
    {
        return verificationStatus switch
        {
            AdoOrganizationVerificationStatus.Verified => "verified",
            AdoOrganizationVerificationStatus.Unauthorized or AdoOrganizationVerificationStatus.Unreachable => "failed",
            AdoOrganizationVerificationStatus.Stale => "stale",
            _ => "unknown",
        };
    }

    private static ClientAdoOrganizationScopeDto CreateScope(
        Guid clientId,
        string organizationUrl,
        bool isEnabled = true)
    {
        return new ClientAdoOrganizationScopeDto(
            Guid.NewGuid(),
            clientId,
            organizationUrl,
            organizationUrl,
            isEnabled,
            AdoOrganizationVerificationStatus.Verified,
            DateTimeOffset.UtcNow,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    private static GitHttpClient CreateGitClient(string organizationUrl)
    {
        return Substitute.For<GitHttpClient>(new Uri(organizationUrl), new VssCredentials());
    }

    private static GitPullRequest CreatePullRequest(
        string repositoryId,
        int number,
        string title,
        string reviewerId,
        string reviewerLogin)
    {
        return new GitPullRequest
        {
            PullRequestId = number,
            Title = title,
            Url = $"https://dev.azure.com/org/project/_git/repo/pullrequest/{number}",
            Status = PullRequestStatus.Active,
            SourceRefName = "refs/heads/feature/providers",
            TargetRefName = "refs/heads/main",
            Repository = new GitRepository
            {
                Id = Guid.Parse(repositoryId),
            },
            Reviewers =
            [
                new IdentityRefWithVote
                {
                    Id = reviewerId,
                    UniqueName = reviewerLogin,
                    DisplayName = reviewerLogin,
                },
            ],
        };
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            var hasSolution = File.Exists(Path.Combine(current.FullName, "MeisterProPR.slnx"));
            var hasSourceTree = Directory.Exists(Path.Combine(current.FullName, "src"));

            if (hasSolution && hasSourceTree)
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate the repository root.");
    }
}
