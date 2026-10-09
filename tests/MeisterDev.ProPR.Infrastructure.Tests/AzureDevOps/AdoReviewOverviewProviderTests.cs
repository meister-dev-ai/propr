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

public sealed class AdoReviewOverviewProviderTests
{
    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized, false)]
    [InlineData(System.Net.HttpStatusCode.Forbidden, false)]
    [InlineData(System.Net.HttpStatusCode.Unauthorized, true)]
    [InlineData(System.Net.HttpStatusCode.Forbidden, true)]
    public async Task NativeSdkDenialIsNormalizedAcrossNestedPreparationAndThreadFailures(System.Net.HttpStatusCode status, bool preparation)
    {
        var sut = this.CreateProvider();
        var native = (VssServiceResponseException)Activator.CreateInstance(
            typeof(VssServiceResponseException), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [status, "Private detail"], null)!;
        var wrapped = new InvalidOperationException("Outer read failed.", new InvalidOperationException("Inner read failed.", native));
        if (preparation)
        {
            sut.GitClientResolver = (_, _) => Task.FromException<GitHttpClient>(wrapped);
        }
        else
        {
            this._git.GetThreadsAsync("project", this._repositoryId, 42, null, null, null, Arg.Any<CancellationToken>())
                .ThrowsAsync(wrapped);
        }

        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => this.Read(sut));
        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain("Private detail", error.Message, StringComparison.Ordinal);
        Assert.Equal(
            status == System.Net.HttpStatusCode.Unauthorized,
            MeisterDev.ProPR.Infrastructure.Features.Providers.Common.ProviderReadFailures.IsConnectionDenied(error));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedSdkThrottleTakesPrecedenceOverDenial(bool preparation)
    {
        var sut = this.CreateProvider();
        var native = (VssServiceResponseException)Activator.CreateInstance(
            typeof(VssServiceResponseException), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [System.Net.HttpStatusCode.TooManyRequests, "Private detail"], null)!;
        var wrapped = new HttpRequestException(
            "Outer denial.", new InvalidOperationException("Inner read failed.", native),
            System.Net.HttpStatusCode.Forbidden);
        if (preparation)
        {
            sut.GitClientResolver = (_, _) => Task.FromException<GitHttpClient>(wrapped);
        }
        else
        {
            this._git.GetThreadsAsync("project", this._repositoryId, 42, null, null, null, Arg.Any<CancellationToken>())
                .ThrowsAsync(wrapped);
        }

        await Assert.ThrowsAsync<MeisterDev.ProPR.Infrastructure.Features.Providers.Common.ProviderThrottledException>(() => this.Read(sut));
    }

    [Fact]
    public async Task PreservesPreliminaryConnectionWideForbidden()
    {
        var sut = this.CreateProvider();
        sut.GitClientResolver = (_, _) => Task.FromException<GitHttpClient>(AdoReadFailures.Denial(System.Net.HttpStatusCode.Forbidden, true));
        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => this.Read(sut));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, error.StatusCode);
        Assert.True(AdoReadFailures.IsConnectionDenied(error));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("vssResponse")]
    [InlineData("vssUnauthorized")]
    [InlineData("typed")]
    public void TypedAuthenticationDenialInvalidatesTheConnection(string kind)
    {
        Exception exception = kind switch
        {
            "http" => new HttpRequestException("Private detail", null, System.Net.HttpStatusCode.Unauthorized),
            "vssResponse" => (VssServiceResponseException)Activator.CreateInstance(
                typeof(VssServiceResponseException), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, [System.Net.HttpStatusCode.Unauthorized, "Private detail"], null)!,
            "vssUnauthorized" => new VssUnauthorizedException("Private detail"),
            _ => AdoReadFailures.Denial(System.Net.HttpStatusCode.Unauthorized),
        };
        Assert.Equal(
            System.Net.HttpStatusCode.Unauthorized,
            AdoReadFailures.DeniedStatus(exception));
        Assert.True(AdoReadFailures.IsConnectionDenied(exception));
        Assert.True(AdoReadFailures.IsConnectionDenied(new InvalidOperationException("Read failed.", exception)));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests, null)]
    [InlineData(System.Net.HttpStatusCode.Forbidden, "retry")]
    [InlineData(System.Net.HttpStatusCode.Forbidden, "remaining")]
    public async Task SyntheticProviderHttpThrottleRemainsTransient(System.Net.HttpStatusCode status, string? throttle)
    {
        var sut = this.CreateProvider();
        using var git = SyntheticAdoReadClient.Create(status, true, throttle);
        sut.GitClientResolver = (_, _) => Task.FromResult(git);
        await Assert.ThrowsAsync<MeisterDev.ProPR.Infrastructure.Features.Providers.Common.ProviderThrottledException>(() => this.Read(sut));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized)]
    [InlineData(System.Net.HttpStatusCode.Forbidden)]
    public async Task SyntheticProviderHttpDenialPreservesTypedStatus(System.Net.HttpStatusCode status)
    {
        var sut = this.CreateProvider();
        using var git = SyntheticAdoReadClient.Create(status, true);
        sut.GitClientResolver = (_, _) => Task.FromResult(git);
        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => this.Read(sut));
        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain("Private detail", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.Unauthorized)]
    [InlineData(System.Net.HttpStatusCode.Forbidden)]
    public async Task ProviderDenialPreservesTypedStatus(System.Net.HttpStatusCode status)
    {
        this._git.GetThreadsAsync("project", this._repositoryId, 42, null, null, null, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Private detail", null, status));
        var error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => this.Read());
        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain("Private detail", error.Message, StringComparison.Ordinal);
    }

    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly string _repositoryId = Guid.NewGuid().ToString();
    private readonly IClientScmConnectionRepository _connections = Substitute.For<IClientScmConnectionRepository>();
    private readonly IClientScmScopeRepository _scopes = Substitute.For<IClientScmScopeRepository>();
    private readonly GitHttpClient _git = Substitute.For<GitHttpClient>(new Uri("https://dev.azure.com/acme"), new VssCredentials());

    [Fact]
    public async Task CountsMessagesFromEveryAuthorAndNativeDiscussions()
    {
        var first = Thread(1, CommentThreadStatus.Active);
        first.Comments.Add(new Comment { Id = 2, CommentType = CommentType.Text, Content = "Reply", Author = new IdentityRef { Id = "other" } });
        first.Comments.Add(new Comment { Id = 3, CommentType = CommentType.System, Content = "Status" });
        first.Comments.Add(new Comment { Id = 4, CommentType = CommentType.Text, Content = "Deleted", IsDeleted = true });
        var deleted = Thread(9, CommentThreadStatus.Active);
        deleted.IsDeleted = true;
        var system = Thread(10, CommentThreadStatus.Unknown);
        system.Comments[0].CommentType = CommentType.System;
        this.Threads(
        [
            first, Thread(2, CommentThreadStatus.Fixed), Thread(3, CommentThreadStatus.Closed),
            Thread(4, CommentThreadStatus.WontFix), Thread(5, CommentThreadStatus.ByDesign), Thread(6, CommentThreadStatus.Pending), deleted, system
        ]);

        var result = await this.Read();

        Assert.Equal(new ReviewOverviewDto(7, 4, 2, true, true), result);
    }

    [Fact]
    public async Task SuccessfulEmptyReadReturnsZero()
    {
        this.Threads([]);
        Assert.Equal(new ReviewOverviewDto(0, 0, 0, true, true), await this.Read());
    }

    [Fact]
    public async Task UnknownStatusPreservesMessagesButMakesResolutionUnavailable()
    {
        this.Threads([Thread(1, CommentThreadStatus.Unknown)]);
        Assert.Equal(new ReviewOverviewDto(1, null, null, false, true), await this.Read());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("duplicate")]
    [InlineData("missing-comments")]
    [InlineData("invalid-id")]
    [InlineData("duplicate-comment")]
    [InlineData("invalid-comment")]
    [InlineData("comment-limit")]
    [InlineData("limit")]
    public async Task MalformedOrBoundedReadNeverReportsZero(string scenario)
    {
        List<GitPullRequestCommentThread>? threads = scenario switch
        {
            "null" => null,
            "duplicate" => [Thread(1, CommentThreadStatus.Active), Thread(1, CommentThreadStatus.Active)],
            "missing-comments" => [new GitPullRequestCommentThread { Id = 1, Status = CommentThreadStatus.Active }],
            "invalid-id" => [Thread(0, CommentThreadStatus.Active)],
            _ => Enumerable.Range(1, 1001).Select(id => Thread(id, CommentThreadStatus.Active)).ToList(),
        };
        if (scenario is "duplicate-comment" or "invalid-comment" or "comment-limit")
        {
            var thread = Thread(1, CommentThreadStatus.Active);
            thread.Comments = scenario == "comment-limit"
                ? Enumerable.Range(1, 10001).Select(id => new Comment { Id = (short)id, CommentType = CommentType.Text, Content = "Message" }).ToList()
                :
                [
                    new Comment { Id = 1, CommentType = CommentType.Text, Content = "Message" },
                    new Comment { Id = (short)(scenario == "duplicate-comment" ? 1 : 2), CommentType = CommentType.Text, Content = null }
                ];
            threads = [thread];
        }

        this.Threads(threads);
        Assert.Equal(new ReviewOverviewDto(null, null, null, false, true), await this.Read());
    }

    [Fact]
    public async Task ProviderFailureThrowsFixedError()
    {
        this._git.GetThreadsAsync("project", this._repositoryId, 42, null, null, null, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Private detail"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => this.Read());
        Assert.Equal("The provider could not return pull request metadata.", error.Message);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("wrong-id")]
    [InlineData("inactive")]
    [InlineData("provider")]
    [InlineData("origin")]
    [InlineData("unverified")]
    [InlineData("secret")]
    public async Task RejectsUnavailableSelectedCredentialsBeforeResolver(string scenario)
    {
        var sut = this.CreateProvider();
        var credential = this.Credential();
        this._connections.GetOperationalConnectionByIdAsync(this._clientId, this._connectionId, Arg.Any<CancellationToken>())
            .Returns(
                scenario switch
                {
                    "missing" => null,
                    "foreign" => credential with { ClientId = Guid.NewGuid() },
                    "wrong-id" => credential with { Id = Guid.NewGuid() },
                    "inactive" => credential with { IsActive = false },
                    "provider" => credential with { ProviderFamily = ScmProvider.GitHub },
                    "origin" => credential with { HostBaseUrl = "https://dev.azure.com/other" },
                    "secret" => credential with { Secret = "" },
                    _ => credential,
                });
        if (scenario == "unverified")
        {
            this.VerifiedConnection("failed");
        }

        var called = false;
        sut.GitClientResolver = (_, _) =>
        {
            called = true;
            return Task.FromResult(this._git);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => this.Read(sut));
        Assert.False(called);
        await this._connections.DidNotReceiveWithAnyArgs().GetOperationalConnectionAsync(default, default!, default);
    }

    [Theory]
    [InlineData(false, "verified", false)]
    [InlineData(true, "neverVerified", true)]
    [InlineData(true, "verified", true)]
    public async Task RootConnectionRequiresEnabledOrganizationScope(bool enabled, string verification, bool succeeds)
    {
        this.Threads([]);
        var sut = this.CreateProvider();
        this._connections.GetOperationalConnectionByIdAsync(this._clientId, this._connectionId, Arg.Any<CancellationToken>())
            .Returns(this.Credential() with { HostBaseUrl = "https://dev.azure.com" });
        var now = DateTimeOffset.UtcNow;
        this._scopes.GetByConnectionIdAsync(this._clientId, this._connectionId, Arg.Any<CancellationToken>()).Returns(
        [
            new ClientScmScopeDto(
                Guid.NewGuid(), this._clientId, this._connectionId, "organization", "acme", "https://dev.azure.com/acme", "acme",
                verification, enabled, now, null, now, now)
        ]);
        if (succeeds)
        {
            Assert.True((await this.Read(sut)).IsComplete);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => this.Read(sut));
        }
    }

    [Fact]
    public async Task UsesExactSelectedOrganizationAndPropagatesCancellation()
    {
        var sut = this.CreateProvider();
        sut.GitClientResolver = (organization, _) =>
        {
            Assert.Equal("https://dev.azure.com/acme", organization);
            return Task.FromResult(this._git);
        };
        using var cancellation = new CancellationTokenSource();
        this._git.GetThreadsAsync("project", this._repositoryId, 42, null, null, null, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromException<List<GitPullRequestCommentThread>>(new OperationCanceledException(cancellation.Token));
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.GetOverviewAsync(
            this._clientId, this.Review(), new(this._connectionId, "https://dev.azure.com/acme"), cancellation.Token));
    }

    [Fact]
    public async Task SuccessfulReadDoesNotResolveAlternateCredentials()
    {
        this.Threads([]);
        Assert.True((await this.Read()).IsComplete);
        await this._connections.Received(1).GetOperationalConnectionByIdAsync(this._clientId, this._connectionId, Arg.Any<CancellationToken>());
        await this._connections.DidNotReceiveWithAnyArgs().GetOperationalConnectionAsync(default, default!, default);
        await this._git.Received(1).GetThreadsAsync("project", this._repositoryId, 42, null, null, null, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("foreign-client")]
    [InlineData("foreign-connection")]
    [InlineData("other-organization")]
    public async Task RootConnectionRejectsMismatchedScope(string scenario)
    {
        var sut = this.CreateProvider();
        this._connections.GetOperationalConnectionByIdAsync(this._clientId, this._connectionId, Arg.Any<CancellationToken>())
            .Returns(this.Credential() with { HostBaseUrl = "https://dev.azure.com" });
        var now = DateTimeOffset.UtcNow;
        this._scopes.GetByConnectionIdAsync(this._clientId, this._connectionId, Arg.Any<CancellationToken>()).Returns(
        [
            new ClientScmScopeDto(
                Guid.NewGuid(), scenario == "foreign-client" ? Guid.NewGuid() : this._clientId,
                scenario == "foreign-connection" ? Guid.NewGuid() : this._connectionId, "organization", "acme",
                scenario == "other-organization" ? "https://dev.azure.com/other" : "https://dev.azure.com/acme", "Scope", "verified", true, now, null, now, now)
        ]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => this.Read(sut));
        await this._git.DidNotReceiveWithAnyArgs().GetThreadsAsync(default(string)!, default(string)!, default, default, default, default!, default);
    }

    [Fact]
    public async Task CancelledCallerDoesNotResolveCredentials()
    {
        var sut = this.CreateProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.GetOverviewAsync(
            this._clientId, this.Review(),
            new(this._connectionId, "https://dev.azure.com/acme"), cancellation.Token));
        await this._connections.DidNotReceiveWithAnyArgs().GetOperationalConnectionByIdAsync(default, default, default);
    }

    private Task<ReviewOverviewDto> Read(AdoReviewOverviewProvider? sut = null) => (sut ?? this.CreateProvider()).GetOverviewAsync(
        this._clientId, this.Review(), new(this._connectionId, "https://dev.azure.com/acme"));

    private CodeReviewRef Review() => new(
        new RepositoryRef(
            new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/acme"),
            this._repositoryId, "project", "project"), CodeReviewPlatformKind.PullRequest, "42", 42);

    private void Threads(List<GitPullRequestCommentThread>? threads) => this._git.GetThreadsAsync(
        "project", this._repositoryId, 42, null, null, null, Arg.Any<CancellationToken>()).Returns(Task.FromResult(threads!));

    private static GitPullRequestCommentThread Thread(int id, CommentThreadStatus status) => new()
    {
        Id = id, Status = status,
        Comments = [new Comment { Id = 1, CommentType = CommentType.Text, Content = "Message", Author = new IdentityRef { Id = "human" } }],
    };

    private ClientScmConnectionCredentialDto Credential() => new(
        this._connectionId, this._clientId, ScmProvider.AzureDevOps,
        "https://dev.azure.com/acme", ScmAuthenticationKind.PersonalAccessToken, "Selected", "fixture", true);

    private void VerifiedConnection(string status = "verified")
    {
        var now = DateTimeOffset.UtcNow;
        this._connections.GetByIdAsync(this._clientId, this._connectionId, Arg.Any<CancellationToken>()).Returns(
            new ClientScmConnectionDto(
                this._connectionId, this._clientId, ScmProvider.AzureDevOps, "https://dev.azure.com/acme",
                ScmAuthenticationKind.PersonalAccessToken, "Selected", true, status, now, null, null, now, now));
    }

    private AdoReviewOverviewProvider CreateProvider()
    {
        this.VerifiedConnection();
        this._connections.GetOperationalConnectionByIdAsync(this._clientId, this._connectionId, Arg.Any<CancellationToken>()).Returns(this.Credential());
        return new(this._connections, this._scopes, new VssConnectionFactory(Substitute.For<TokenCredential>()))
            { GitClientResolver = (_, _) => Task.FromResult(this._git) };
    }
}
