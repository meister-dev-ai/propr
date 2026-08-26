// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Licensing;

/// <summary>
///     The third exclusion signal: the accounts ProPR is configured to act as. The identities and the
///     connections they hang off are database rows, so the read and the exclusion it produces run against
///     PostgreSQL.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ConfiguredReviewerIdentityExclusionTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private const string AzureIdentityId = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";

    private const string GitHubIdentityId = "9911";

    private readonly List<Guid> _seededClientIds = [];

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        return this.ResetAsync();
    }

    public Task DisposeAsync()
    {
        return this.ResetAsync();
    }

    [Fact]
    public async Task AnAuthorThatIsTheConfiguredIdentityOnAzureDevOps_IsRecordedExcluded()
    {
        await using var db = this.CreateContext();
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/acme");
        await this.SeedIdentityAsync(db, ScmProvider.AzureDevOps, "https://dev.azure.com", AzureIdentityId);

        await Recorder(db).RecordAsync(Observation(host, AzureIdentityId));

        Assert.True((await db.LicensingAuthorActivity.AsNoTracking().SingleAsync()).Excluded);
    }

    // The same rule on a second provider, where the identifier is a number rather than a GUID.
    [Fact]
    public async Task AnAuthorThatIsTheConfiguredIdentityOnGitHub_IsRecordedExcluded()
    {
        await using var db = this.CreateContext();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        await this.SeedIdentityAsync(db, ScmProvider.GitHub, "https://github.com", GitHubIdentityId);

        await Recorder(db).RecordAsync(Observation(host, GitHubIdentityId));

        Assert.True((await db.LicensingAuthorActivity.AsNoTracking().SingleAsync()).Excluded);
    }

    [Fact]
    public async Task AnotherAccountOnTheSameHost_IsCounted()
    {
        await using var db = this.CreateContext();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        await this.SeedIdentityAsync(db, ScmProvider.GitHub, "https://github.com", GitHubIdentityId);

        await Recorder(db).RecordAsync(Observation(host, "4242"));

        Assert.False((await db.LicensingAuthorActivity.AsNoTracking().SingleAsync()).Excluded);
    }

    // A provider-native identifier names one account only within the host that issued it, so an identity
    // configured on another host of the same provider says nothing about this author.
    [Fact]
    public async Task TheConfiguredIdentityOfAnotherHost_DoesNotExcludeTheAuthor()
    {
        await using var db = this.CreateContext();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        await this.SeedIdentityAsync(db, ScmProvider.GitLab, "https://gitlab.other.example", "7777");

        await Recorder(db).RecordAsync(Observation(host, "7777"));

        Assert.False((await db.LicensingAuthorActivity.AsNoTracking().SingleAsync()).Excluded);
    }

    // A self-hosted Azure DevOps connection stores its collection path in the host URL, while the host a
    // completion reports carries the authority alone. The two still name one host.
    [Fact]
    public async Task AConnectionStoringACollectionPath_StillMatchesTheAuthorsHost()
    {
        await using var db = this.CreateContext();
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://tfs.acme.example");
        await this.SeedIdentityAsync(
            db,
            ScmProvider.AzureDevOps,
            "https://tfs.acme.example/tfs/DefaultCollection",
            AzureIdentityId);

        await Recorder(db).RecordAsync(Observation(host, AzureIdentityId));

        Assert.True((await db.LicensingAuthorActivity.AsNoTracking().SingleAsync()).Excluded);
    }

    private static AuthorActivityRecorder Recorder(MeisterProPRDbContext db)
    {
        return new AuthorActivityRecorder(
            new AuthorActivityRollupRepository(db),
            new ConfiguredReviewerIdentityRepository(db));
    }

    /// <summary>An observation whose names and flag carry no automation signal, so only the identity can decide.</summary>
    private static AuthorActivityObservation Observation(ProviderHostRef host, string externalUserId)
    {
        return new AuthorActivityObservation(
            host,
            externalUserId,
            AuthorActivitySource.Review,
            "octo.dev",
            "Octo Dev");
    }

    private async Task SeedIdentityAsync(
        MeisterProPRDbContext db,
        ScmProvider provider,
        string connectionHostBaseUrl,
        string externalUserId)
    {
        var now = DateTimeOffset.UtcNow;
        var client = new ClientRecord
        {
            Id = Guid.NewGuid(),
            TenantId = TenantCatalog.SystemTenantId,
            DisplayName = "Reviewer Identity Exclusion Client",
            IsActive = true,
            CreatedAt = now,
        };
        var connection = new ClientScmConnectionRecord
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            Provider = provider,
            HostBaseUrl = connectionHostBaseUrl,
            AuthenticationKind = ScmAuthenticationKind.PersonalAccessToken,
            DisplayName = "Reviewer Identity Exclusion Connection",
            EncryptedSecretMaterial = "not-a-secret",
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Clients.Add(client);
        db.ClientScmConnections.Add(connection);
        db.ClientReviewerIdentities.Add(
            new ClientReviewerIdentityRecord
            {
                Id = Guid.NewGuid(),
                ClientId = client.Id,
                ConnectionId = connection.Id,
                Provider = provider,
                ExternalUserId = externalUserId,
                Login = "propr-reviewer",
                DisplayName = "ProPR Reviewer",
                IsBot = true,
                UpdatedAt = now,
            });

        await db.SaveChangesAsync();
        this._seededClientIds.Add(client.Id);
    }

    private async Task ResetAsync()
    {
        await using var db = this.CreateContext();
        await db.LicensingAuthorActivity.ExecuteDeleteAsync();

        if (this._seededClientIds.Count > 0)
        {
            await db.Clients.Where(client => this._seededClientIds.Contains(client.Id)).ExecuteDeleteAsync();
            this._seededClientIds.Clear();
        }
    }

    private MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector())
            .Options;

        return new MeisterProPRDbContext(options);
    }
}
