// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Licensing;

/// <summary>
///     What the recorder hands the store: the exclusion decision, made from the observation's own signals and
///     from the identities ProPR is configured to act as.
/// </summary>
public sealed class AuthorActivityRecorderTests
{
    private static readonly ProviderHostRef GitHubHost = new(ScmProvider.GitHub, "https://github.com");

    [Fact]
    public async Task APersonOnAHostWithNoConfiguredIdentity_IsRecordedCounted()
    {
        var store = Substitute.For<IAuthorActivityRollupStore>();
        var recorder = new AuthorActivityRecorder(store, Identities());

        await recorder.RecordAsync(Observation("octo.dev"));

        await store.Received(1).RecordAuthorAsync(
            GitHubHost,
            "4242",
            AuthorActivitySource.Review,
            false,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnAuthorTheNameRulesIdentify_IsRecordedExcluded()
    {
        var store = Substitute.For<IAuthorActivityRollupStore>();
        var recorder = new AuthorActivityRecorder(store, Identities());

        await recorder.RecordAsync(Observation("dependabot[bot]"));

        await store.Received(1).RecordAuthorAsync(
            GitHubHost,
            "4242",
            AuthorActivitySource.Review,
            true,
            Arg.Any<CancellationToken>());
    }

    // The identity read is a database round trip, and an author the name rules already identified does not
    // need it.
    [Fact]
    public async Task AnAuthorTheNameRulesIdentify_IsNotComparedAgainstTheConfiguredIdentities()
    {
        var identities = Identities();
        var recorder = new AuthorActivityRecorder(Substitute.For<IAuthorActivityRollupStore>(), identities);

        await recorder.RecordAsync(Observation("dependabot[bot]"));

        await identities.DidNotReceive()
            .ListExternalUserIdsAsync(Arg.Any<ProviderHostRef>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnAuthorThatIsAConfiguredIdentity_IsRecordedExcluded()
    {
        var store = Substitute.For<IAuthorActivityRollupStore>();
        var recorder = new AuthorActivityRecorder(store, Identities("9911", "4242"));

        await recorder.RecordAsync(Observation("octo.dev"));

        await store.Received(1).RecordAuthorAsync(
            GitHubHost,
            "4242",
            AuthorActivitySource.Review,
            true,
            Arg.Any<CancellationToken>());
    }

    // The comparison is on the host-scoped key, which is lower-cased, so a provider that spells its
    // identifiers in mixed case still recognizes its own account.
    [Fact]
    public async Task AConfiguredIdentitySpelledInAnotherCase_StillMatchesTheAuthor()
    {
        var store = Substitute.For<IAuthorActivityRollupStore>();
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/acme");
        var recorder = new AuthorActivityRecorder(store, Identities("0F1E2D3C-4B5A-6978-8796-A5B4C3D2E1F0"));

        await recorder.RecordAsync(
            new AuthorActivityObservation(
                host,
                "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0",
                AuthorActivitySource.MentionAnswer,
                "ProPR Reviewer",
                "ProPR Reviewer"));

        await store.Received(1).RecordAuthorAsync(
            host,
            "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0",
            AuthorActivitySource.MentionAnswer,
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailedIdentityRead_IsTheCallersToAbsorb()
    {
        var identities = Substitute.For<IConfiguredReviewerIdentitySource>();
        identities.ListExternalUserIdsAsync(Arg.Any<ProviderHostRef>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<string>>>(_ => throw new InvalidOperationException("unreachable"));
        var recorder = new AuthorActivityRecorder(Substitute.For<IAuthorActivityRollupStore>(), identities);

        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.RecordAsync(Observation("octo.dev")));
    }

    private static IConfiguredReviewerIdentitySource Identities(params string[] externalUserIds)
    {
        var identities = Substitute.For<IConfiguredReviewerIdentitySource>();
        identities.ListExternalUserIdsAsync(Arg.Any<ProviderHostRef>(), Arg.Any<CancellationToken>())
            .Returns(externalUserIds);

        return identities;
    }

    private static AuthorActivityObservation Observation(string login)
    {
        return new AuthorActivityObservation(
            GitHubHost,
            "4242",
            AuthorActivitySource.Review,
            login,
            login);
    }
}
