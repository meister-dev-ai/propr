// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Azure.Core;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Infrastructure.Tests.AzureDevOps;

public sealed class AdoReviewDiscoveryProviderTests
{
    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized)]
    [InlineData(System.Net.HttpStatusCode.Forbidden)]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests)]
    public async Task ExplicitContext_NormalizesNestedSdkPreparationFailures(System.Net.HttpStatusCode status)
    {
        var connection = Guid.NewGuid();
        var sut = this.CreateProvider(this.SelectedConnection(connection));
        var native = (VssServiceResponseException)Activator.CreateInstance(
            typeof(VssServiceResponseException), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [status, "Private detail"], null)!;
        var wrapped = new InvalidOperationException("Outer read failed.", new InvalidOperationException("Inner read failed.", native));
        sut.GitClientResolver = (_, _) => Task.FromException<GitHttpClient>(wrapped);
        var operation = () => sut.ListOpenReviewsAsync(this._clientId, this.Repository(), null, context: new(connection, "https://dev.azure.com/acme"));
        if (status == System.Net.HttpStatusCode.TooManyRequests)
        {
            await Assert.ThrowsAsync<MeisterDev.ProPR.Infrastructure.Features.Providers.Common.ProviderThrottledException>(operation);
            return;
        }

        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(operation);
        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain("Private detail", error.Message, StringComparison.Ordinal);
        Assert.Equal(
            status == System.Net.HttpStatusCode.Unauthorized,
            MeisterDev.ProPR.Infrastructure.Features.Providers.Common.ProviderReadFailures.IsConnectionDenied(error));
    }

    [Fact]
    public async Task ExplicitContext_PreservesPreliminaryConnectionWideForbidden()
    {
        var connection = Guid.NewGuid();
        var sut = this.CreateProvider(this.SelectedConnection(connection));
        sut.GitClientResolver = (_, _) => Task.FromException<GitHttpClient>(
            MeisterDev.ProPR.Infrastructure.Features.Providers.Common.ProviderReadFailures.Denial(System.Net.HttpStatusCode.Forbidden, true));
        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => sut.ListOpenReviewsAsync(
            this._clientId, this.Repository(), null, context: new(connection, "https://dev.azure.com/acme")));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, error.StatusCode);
        Assert.True(MeisterDev.ProPR.Infrastructure.Features.Providers.Common.ProviderReadFailures.IsConnectionDenied(error));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized)]
    [InlineData(System.Net.HttpStatusCode.Forbidden)]
    public async Task ExplicitContext_SyntheticProviderDenialPreservesTypedStatus(System.Net.HttpStatusCode status)
    {
        var connection = Guid.NewGuid();
        var sut = this.CreateProvider(this.SelectedConnection(connection));
        using var git = SyntheticAdoReadClient.Create(status, false);
        sut.GitClientResolver = (_, _) => Task.FromResult(git);
        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => sut.ListOpenReviewsAsync(
            this._clientId, this.Repository(), null, context: new(connection, "https://dev.azure.com/acme")));
        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain("Private detail", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListOpenReviewsAsync_UsesBrowserUrlInsteadOfApiUrl()
    {
        this.StubRepositoryQuery();
        var row = this.PullRequest();
        row.Url = "https://dev.azure.com/acme/_apis/git/pullRequests/42";
        this._git.GetPullRequestsAsync(
            "project", this._repositoryId, Arg.Any<GitPullRequestSearchCriteria>(),
            Arg.Any<int?>(), Arg.Any<int?>(), 100, Arg.Any<object>(), Arg.Any<CancellationToken>()).Returns([row]);
        var connectionId = Guid.NewGuid();
        var result = Assert.Single(
            await this.CreateProvider(this.SelectedConnection(connectionId)).ListOpenReviewsAsync(
                this._clientId, this.Repository(), null, context: new(connectionId, "https://dev.azure.com/acme")));
        Assert.Equal($"https://dev.azure.com/acme/project/_git/{this._repositoryId}/pullrequest/42", result.WebUrl);
    }

    private readonly Guid _clientId = Guid.NewGuid();
    private readonly string _repositoryId = Guid.NewGuid().ToString("D");
    private readonly GitHttpClient _git = Substitute.For<GitHttpClient>(new Uri("https://dev.azure.com/acme"), new VssCredentials());

    [Fact]
    public async Task ListOpenReviewsAsync_QueriesRepositoryBeforeApplyingTheLimit()
    {
        this.StubRepositoryQuery();
        var sut = this.CreateProvider();

        var result = await sut.ListOpenReviewsAsync(this._clientId, this.Repository(), null);

        Assert.Equal(42, Assert.Single(result).CodeReview.Number);
        await this._git.Received(1).GetPullRequestsAsync(
            "project", this._repositoryId,
            Arg.Is<GitPullRequestSearchCriteria>(criteria => criteria.Status == PullRequestStatus.Active),
            null, null, 100, null, Arg.Any<CancellationToken>());
        await this._git.DidNotReceiveWithAnyArgs().GetPullRequestsByProjectAsync(default(string)!, default!, default, default, default, default!, default);
    }

    [Fact]
    public async Task ListOpenReviewsAsync_RetainsRowWhenRevisionEnrichmentFails()
    {
        this.StubRepositoryQuery();
        this._git.GetPullRequestsByProjectAsync(
                Arg.Any<string>(), Arg.Any<GitPullRequestSearchCriteria>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns([this.PullRequest()]);
        this._git.GetPullRequestIterationsAsync("project", this._repositoryId, 42, false, null, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Provider detail"));

        var connectionId = Guid.NewGuid();
        var result = await this.CreateProvider(this.SelectedConnection(connectionId)).ListOpenReviewsAsync(
            this._clientId,
            this.Repository(), null, context: new(connectionId, "https://dev.azure.com/acme"));

        var row = Assert.Single(result);
        Assert.Equal(42, row.CodeReview.Number);
        Assert.Null(row.ReviewRevision);
    }

    [Fact]
    public async Task ExplicitContext_UsesSelectedConnectionAndExactOrganization()
    {
        this.StubRepositoryQuery();
        var connectionId = Guid.NewGuid();
        var connections = this.SelectedConnection(connectionId);
        var organizations = new List<string>();
        var sut = this.CreateProvider(connections);
        sut.GitClientResolver = (organization, _) =>
        {
            organizations.Add(organization);
            return Task.FromResult(this._git);
        };

        Assert.Single(
            await sut.ListOpenReviewsAsync(
                this._clientId, this.Repository(), null,
                context: new(connectionId, "https://dev.azure.com/acme")));

        Assert.Equal(["https://dev.azure.com/acme"], organizations);
        await connections.Received(1).GetOperationalConnectionByIdAsync(this._clientId, connectionId, Arg.Any<CancellationToken>());
        await connections.DidNotReceiveWithAnyArgs().GetOperationalConnectionAsync(default, default!, default);
    }

    [Fact]
    public async Task ExplicitContext_QueryFailureThrowsFixedError()
    {
        var connectionId = Guid.NewGuid();
        this._git.GetPullRequestsAsync(
                "project", this._repositoryId, Arg.Any<GitPullRequestSearchCriteria>(),
                Arg.Any<int?>(), Arg.Any<int?>(), 100, Arg.Any<object>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Private provider detail"));
        this._git.GetPullRequestsByProjectAsync(
                Arg.Any<string>(), Arg.Any<GitPullRequestSearchCriteria>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Private provider detail"));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => this.CreateProvider(this.SelectedConnection(connectionId))
            .ListOpenReviewsAsync(this._clientId, this.Repository(), null, context: new(connectionId, "https://dev.azure.com/acme")));

        Assert.Equal("The provider could not list open pull requests.", failure.Message);
    }

    [Fact]
    public async Task ExplicitContext_SuccessfulEmptyQueryReturnsEmpty()
    {
        var connectionId = Guid.NewGuid();
        this._git.GetPullRequestsAsync(
            "project", this._repositoryId, Arg.Any<GitPullRequestSearchCriteria>(),
            Arg.Any<int?>(), Arg.Any<int?>(), 100, Arg.Any<object>(), Arg.Any<CancellationToken>()).Returns([]);
        Assert.Empty(
            await this.CreateProvider(this.SelectedConnection(connectionId))
                .ListOpenReviewsAsync(this._clientId, this.Repository(), null, context: new(connectionId, "https://dev.azure.com/acme")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitContext_MalformedRowThrowsFixedError(bool includeValidRow)
    {
        this.StubRepositoryQuery();
        var connectionId = Guid.NewGuid();
        var malformed = this.PullRequest();
        malformed.PullRequestId = 0;
        var rows = new List<GitPullRequest>();
        if (includeValidRow)
        {
            rows.Add(this.PullRequest());
        }

        rows.Add(malformed);
        this._git.GetPullRequestsAsync(
                "project", this._repositoryId, Arg.Any<GitPullRequestSearchCriteria>(),
                Arg.Any<int?>(), Arg.Any<int?>(), 100, Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(rows);
        this._git.GetPullRequestIterationsAsync("project", this._repositoryId, 0, false, null, Arg.Any<CancellationToken>())
            .Returns([]);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => this.CreateProvider(this.SelectedConnection(connectionId))
            .ListOpenReviewsAsync(this._clientId, this.Repository(), null, context: new(connectionId, "https://dev.azure.com/acme")));

        Assert.Equal("The provider could not list open pull requests.", failure.Message);
        if (includeValidRow)
        {
            await this._git.Received(1).GetPullRequestIterationsAsync("project", this._repositoryId, 42, false, null, Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task ExplicitContext_MissingCredentialDoesNotUseResolver()
    {
        var sut = this.CreateProvider();
        var called = false;
        sut.GitClientResolver = (_, _) =>
        {
            called = true;
            return Task.FromResult(this._git);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ListOpenReviewsAsync(
            this._clientId, this.Repository(),
            null, context: new(Guid.NewGuid(), "https://dev.azure.com/acme")));

        Assert.False(called);
    }

    [Fact]
    public async Task ExplicitContext_PropagatesQueryCancellation()
    {
        var connectionId = Guid.NewGuid();
        this._git.GetPullRequestsAsync(
                "project", this._repositoryId, Arg.Any<GitPullRequestSearchCriteria>(),
                Arg.Any<int?>(), Arg.Any<int?>(), 100, Arg.Any<object>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());
        this._git.GetPullRequestsByProjectAsync(
                Arg.Any<string>(), Arg.Any<GitPullRequestSearchCriteria>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => this.CreateProvider(this.SelectedConnection(connectionId))
            .ListOpenReviewsAsync(this._clientId, this.Repository(), null, context: new(connectionId, "https://dev.azure.com/acme")));
    }

    private IClientScmConnectionRepository SelectedConnection(Guid connectionId)
    {
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalConnectionByIdAsync(this._clientId, connectionId, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    connectionId, this._clientId, ScmProvider.AzureDevOps,
                    "https://dev.azure.com/acme", ScmAuthenticationKind.PersonalAccessToken, "Selected", "selected-fixture", true));
        return connections;
    }

    [Fact]
    public async Task ExplicitContext_RejectsAnotherAzureOrganization()
    {
        var connectionId = Guid.NewGuid();
        var sut = this.CreateProvider(this.SelectedConnection(connectionId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ListOpenReviewsAsync(
            this._clientId,
            this.Repository(), null, context: new(connectionId, "https://dev.azure.com/other")));
        await this._git.DidNotReceiveWithAnyArgs().GetPullRequestsAsync(
            default(string)!, default(string)!, default!, default, default, default, default!, default);
    }

    [Fact]
    public async Task ExplicitContext_RequiresEnabledScopeForAnAuthorityConnection()
    {
        var connectionId = Guid.NewGuid();
        var connections = this.SelectedConnection(connectionId);
        connections.GetOperationalConnectionByIdAsync(this._clientId, connectionId, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    connectionId, this._clientId, ScmProvider.AzureDevOps,
                    "https://dev.azure.com", ScmAuthenticationKind.PersonalAccessToken, "Selected", "selected-fixture", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => this.CreateProvider(connections)
            .ListOpenReviewsAsync(this._clientId, this.Repository(), null, context: new(connectionId, "https://dev.azure.com/acme")));
        await this._git.DidNotReceiveWithAnyArgs().GetPullRequestsAsync(
            default(string)!, default(string)!, default!, default, default, default, default!, default);
    }

    [Fact]
    public async Task ContextFreeDiscovery_TriesNextOrganizationAndRetainsReviewerFilter()
    {
        var connectionId = Guid.NewGuid();
        var connections = Substitute.For<IClientScmConnectionRepository>();
        var now = DateTimeOffset.UtcNow;
        connections.GetByClientIdAsync(this._clientId, Arg.Any<CancellationToken>())
            .Returns(
            [
                new ClientScmConnectionDto(
                    connectionId, this._clientId, ScmProvider.AzureDevOps,
                    "https://dev.azure.com", ScmAuthenticationKind.PersonalAccessToken, "Configured", true, "verified", now, null, null, now, now)
            ]);
        var scopes = Substitute.For<IClientScmScopeRepository>();
        scopes.GetByConnectionIdAsync(this._clientId, connectionId, Arg.Any<CancellationToken>())
            .Returns(
                new[] { "acme", "other" }.Select(organization => new ClientScmScopeDto(
                    Guid.NewGuid(), this._clientId, connectionId,
                    "organization", organization, $"https://dev.azure.com/{organization}", organization, "verified", true, now, null, now, now)).ToList());
        var reviewerId = Guid.NewGuid();
        var reviewer = new ReviewerIdentity(this.Repository().Host, reviewerId.ToString(), "reviewer", "Reviewer", false);
        var pullRequest = this.PullRequest();
        pullRequest.Reviewers = [new IdentityRefWithVote { Id = reviewer.ExternalUserId, UniqueName = reviewer.Login }];
        this._git.GetPullRequestsAsync(
            "project", this._repositoryId, Arg.Any<GitPullRequestSearchCriteria>(),
            Arg.Any<int?>(), Arg.Any<int?>(), 100, Arg.Any<object>(), Arg.Any<CancellationToken>()).Returns([pullRequest]);
        this._git.GetPullRequestIterationsAsync("project", this._repositoryId, 42, false, null, Arg.Any<CancellationToken>()).Returns([]);
        var attempted = new List<string>();
        var sut = new AdoReviewDiscoveryProvider(connections, scopes, new VssConnectionFactory(Substitute.For<TokenCredential>()))
        {
            GitClientResolver = (organization, _) =>
            {
                attempted.Add(organization);
                return organization.EndsWith("/acme", StringComparison.Ordinal)
                    ? Task.FromException<GitHttpClient>(new HttpRequestException("Rejected"))
                    : Task.FromResult(this._git);
            },
        };

        Assert.Single(await sut.ListOpenReviewsAsync(this._clientId, this.Repository(), reviewer));
        Assert.Equal(["https://dev.azure.com/acme", "https://dev.azure.com/other"], attempted);
        await this._git.Received(1).GetPullRequestsAsync(
            "project", this._repositoryId,
            Arg.Is<GitPullRequestSearchCriteria>(criteria => criteria.ReviewerId == reviewerId),
            null, null, 100, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ContextFreeDiscovery_AllCandidatesRejectedReturnsEmpty()
    {
        var sut = this.CreateProvider();
        sut.GitClientResolver = (_, _) => Task.FromException<GitHttpClient>(new HttpRequestException("Rejected"));
        Assert.Empty(await sut.ListOpenReviewsAsync(this._clientId, this.Repository(), null));
    }

    [Fact]
    public async Task ExplicitContext_PropagatesEnrichmentCancellation()
    {
        this.StubRepositoryQuery();
        var connectionId = Guid.NewGuid();
        this._git.GetPullRequestIterationsAsync("project", this._repositoryId, 42, false, null, Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() => this.CreateProvider(this.SelectedConnection(connectionId))
            .ListOpenReviewsAsync(this._clientId, this.Repository(), null, context: new(connectionId, "https://dev.azure.com/acme")));
    }

    [Theory]
    [InlineData(false, "acme", false, false)]
    [InlineData(true, "other", false, false)]
    [InlineData(true, "acme", true, false)]
    [InlineData(true, "acme", false, true)]
    public async Task ExplicitContext_ValidatesSelectedOrganizationScope(bool enabled, string organization, bool foreignScope, bool succeeds)
    {
        this.StubRepositoryQuery();
        var connectionId = Guid.NewGuid();
        var connections = this.SelectedConnection(connectionId);
        connections.GetOperationalConnectionByIdAsync(this._clientId, connectionId, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    connectionId, this._clientId, ScmProvider.AzureDevOps,
                    "https://dev.azure.com", ScmAuthenticationKind.PersonalAccessToken, "Selected", "selected-fixture", true));
        var scopes = Substitute.For<IClientScmScopeRepository>();
        var now = DateTimeOffset.UtcNow;
        scopes.GetByConnectionIdAsync(this._clientId, connectionId, Arg.Any<CancellationToken>())
            .Returns(
            [
                new ClientScmScopeDto(
                    Guid.NewGuid(), this._clientId, foreignScope ? Guid.NewGuid() : connectionId,
                    "organization", organization, $"https://dev.azure.com/{organization}", organization, "verified", enabled, now, null, now, now)
            ]);
        var sut = this.CreateProvider(connections, scopes);
        if (succeeds)
        {
            Assert.Single(
                await sut.ListOpenReviewsAsync(
                    this._clientId, this.Repository(), null,
                    context: new(connectionId, "https://dev.azure.com/acme")));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ListOpenReviewsAsync(
                this._clientId, this.Repository(),
                null, context: new(connectionId, "https://dev.azure.com/acme")));
            await this._git.DidNotReceiveWithAnyArgs().GetPullRequestsAsync(
                default(string)!, default(string)!, default!, default, default, default, default!, default);
        }
    }

    [Fact]
    public async Task ExplicitContext_PreservesDraftState()
    {
        this.StubRepositoryQuery();
        var draft = this.PullRequest();
        draft.IsDraft = true;
        this._git.GetPullRequestsAsync(
            "project", this._repositoryId, Arg.Any<GitPullRequestSearchCriteria>(),
            Arg.Any<int?>(), Arg.Any<int?>(), 100, Arg.Any<object>(), Arg.Any<CancellationToken>()).Returns([draft]);
        var connectionId = Guid.NewGuid();
        var result = await this.CreateProvider(this.SelectedConnection(connectionId)).ListOpenReviewsAsync(
            this._clientId,
            this.Repository(), null, context: new(connectionId, "https://dev.azure.com/acme"));
        Assert.Equal(CodeReviewState.Draft, Assert.Single(result).ReviewState);
    }

    private AdoReviewDiscoveryProvider CreateProvider(
        IClientScmConnectionRepository? connections = null,
        IClientScmScopeRepository? scopes = null) => new(
        connections ?? Substitute.For<IClientScmConnectionRepository>(), scopes ?? Substitute.For<IClientScmScopeRepository>(),
        new VssConnectionFactory(Substitute.For<TokenCredential>()))
    {
        GitClientResolver = (_, _) => Task.FromResult(this._git),
    };

    private RepositoryRef Repository() => new(
        new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/acme"),
        this._repositoryId, "project", "project");

    private GitPullRequest PullRequest() => new()
    {
        PullRequestId = 42, Title = "Update", Status = PullRequestStatus.Active,
        Repository = new GitRepository { Id = Guid.Parse(this._repositoryId) },
    };

    private void StubRepositoryQuery()
    {
        this._git.GetPullRequestsAsync(
                "project", this._repositoryId, Arg.Any<GitPullRequestSearchCriteria>(),
                Arg.Any<int?>(), Arg.Any<int?>(), 100, Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns([this.PullRequest()]);
        this._git.GetPullRequestIterationsAsync("project", this._repositoryId, 42, false, null, Arg.Any<CancellationToken>())
            .Returns([]);
    }
}
