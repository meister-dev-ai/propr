// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Admission;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Admission;

/// <summary>
///     Who the admission notice is published as. A provider writes the publishing identity's display name in
///     the heading of the comment it posts, so the identity this path resolves is what an author reads on the
///     pull request.
/// </summary>
public sealed class ReviewAdmissionNoticeTests
{
    [Fact]
    public async Task PostAsync_PublishesUnderTheConfiguredReviewerIdentity()
    {
        var job = CreateJob();
        var publication = Substitute.For<ICodeReviewPublicationService>();
        var clients = CreateClientRegistry(job, new ReviewerIdentity(job.ProviderHost, "42", "local_admin", "local_admin", isBot: false));

        await CreateNotice(job, publication, clients).PostAsync(job, "The repository is larger than this client allows.");

        var identity = CapturePublishedIdentity(publication);
        Assert.Equal("local_admin", identity.DisplayName);
    }

    // The synthetic key naming the connection is an internal identifier. Published, it becomes the heading of
    // the comment the author reads.
    [Fact]
    public async Task PostAsync_WithoutAConfiguredReviewerIdentity_PublishesUnderANameAndNotTheConnectionKey()
    {
        var job = CreateJob();
        var publication = Substitute.For<ICodeReviewPublicationService>();
        var clients = CreateClientRegistry(job, identity: null);

        await CreateNotice(job, publication, clients).PostAsync(job, "The repository is larger than this client allows.");

        var identity = CapturePublishedIdentity(publication);
        Assert.DoesNotContain("connection:", identity.DisplayName, StringComparison.Ordinal);
        Assert.DoesNotContain(job.ClientId.ToString("D"), identity.DisplayName, StringComparison.Ordinal);
    }

    private static ReviewerIdentity CapturePublishedIdentity(ICodeReviewPublicationService publication)
    {
        var call = publication.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(ICodeReviewPublicationService.PublishReviewAsync));
        return call.GetArguments().OfType<ReviewerIdentity>().First();
    }

    private static ReviewAdmissionNotice CreateNotice(
        ReviewJob job,
        ICodeReviewPublicationService publication,
        IClientRegistry clients)
    {
        var providers = Substitute.For<IScmProviderRegistry>();
        providers.GetCodeReviewPublicationService(job.Provider).Returns(publication);

        return new ReviewAdmissionNotice(providers, clients, NullLogger<ReviewAdmissionNotice>.Instance);
    }

    private static IClientRegistry CreateClientRegistry(ReviewJob job, ReviewerIdentity? identity)
    {
        var clients = Substitute.For<IClientRegistry>();
        clients.GetScmCommentPostingEnabledAsync(job.ClientId, Arg.Any<CancellationToken>()).Returns(true);
        clients.GetEffectiveReviewerIdentityAsync(job.ClientId, job.ProviderHost, Arg.Any<CancellationToken>())
            .Returns(identity);
        return clients;
    }

    private static ReviewJob CreateJob()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://forgejo.example", "acme", "74", 1, 1);
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://forgejo.example");
        var repository = new RepositoryRef(host, "74", "acme", "acme/propr");
        job.SetProviderReviewContext(new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "1", 1));
        job.SetReviewRevision(new ReviewRevision("head-sha", "base-sha", null, null, null));
        return job;
    }
}
