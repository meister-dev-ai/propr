// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

/// <summary>
///     The runner record stores its client scope and tags as PostgreSQL arrays behind backing fields, which
///     is the kind of mapping that compiles and passes unit tests and then fails on the first real save.
///     These prove the round-trip against the database.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class RunnerRegistryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly Guid TenantId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid ClientA = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid ClientB = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    private DbContextOptions<MeisterProPRDbContext> _options = null!;
    private MeisterProPRDbContext _dbContext = null!;
    private RunnerRegistry _registry = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        this._options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(this._options);
        await this._dbContext.ReviewRunners.ExecuteDeleteAsync();
        await this._dbContext.RunnerRegistrationTokens.ExecuteDeleteAsync();
        this._registry = new RunnerRegistry(this._dbContext);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is not null)
        {
            await this._dbContext.DisposeAsync();
        }
    }

    private static RunnerRegistrationToken MakeToken(params Guid[] scope)
    {
        return new RunnerRegistrationToken(
            Guid.NewGuid(),
            TenantId,
            scope,
            "hashed:tok",
            $"LOOKUP{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1),
            2,
            Guid.NewGuid());
    }

    private static ReviewRunner MakeRunner(params Guid[] scope)
    {
        return new ReviewRunner(
            Guid.NewGuid(),
            TenantId,
            "runner-01",
            scope,
            1,
            "hashed:secret",
            $"LOOKUP{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow.AddDays(30),
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task AnEnrolledRunner_RoundTripsItsScopeAndTags()
    {
        var token = MakeToken(ClientA, ClientB);
        var runner = MakeRunner(ClientA, ClientB);
        runner.DeclareTags(["linux", "gpu"]);

        this._dbContext.RunnerRegistrationTokens.Add(token);
        await this._dbContext.SaveChangesAsync();
        await this._registry.AddAsync(runner, token);

        await using var freshContext = new MeisterProPRDbContext(this._options);
        var stored = await new RunnerRegistry(freshContext).FindByIdAsync(runner.Id);

        Assert.NotNull(stored);
        Assert.Equal([ClientA, ClientB], stored!.ClientScope);
        Assert.Equal(["linux", "gpu"], stored.Tags);
        Assert.Equal(RunnerState.Enrolled, stored.State);
        Assert.Equal(TenantId, stored.TenantId);
    }

    [Fact]
    public async Task ARunnerScopedToNothingInParticular_RoundTripsAsCoveringEveryClient()
    {
        var token = MakeToken();
        var runner = MakeRunner();
        this._dbContext.RunnerRegistrationTokens.Add(token);
        await this._dbContext.SaveChangesAsync();
        await this._registry.AddAsync(runner, token);

        await using var freshContext = new MeisterProPRDbContext(this._options);
        var stored = await new RunnerRegistry(freshContext).FindByIdAsync(runner.Id);

        Assert.Empty(stored!.ClientScope);
        Assert.True(stored.CoversClient(ClientA));
    }

    // The lookup hash is how a presented credential finds its row; an unindexed or mismapped column would
    // turn every authenticated call into a scan or a miss.
    [Fact]
    public async Task ARunnerIsFoundByItsCredentialLookupHash()
    {
        var token = MakeToken(ClientA);
        var runner = MakeRunner(ClientA);
        this._dbContext.RunnerRegistrationTokens.Add(token);
        await this._dbContext.SaveChangesAsync();
        await this._registry.AddAsync(runner, token);

        await using var freshContext = new MeisterProPRDbContext(this._options);
        var found = await new RunnerRegistry(freshContext)
            .FindByCredentialLookupAsync(runner.CredentialLookupHash);

        Assert.Equal(runner.Id, found?.Id);
    }

    // Enrollment and the token use it consumed have to land together, or a crash between them either loses
    // the runner or lets the token be spent twice.
    [Fact]
    public async Task EnrolmentAndTheTokenUse_ArePersistedTogether()
    {
        var token = MakeToken(ClientA);
        this._dbContext.RunnerRegistrationTokens.Add(token);
        await this._dbContext.SaveChangesAsync();

        token.RecordUse();
        await this._registry.AddAsync(MakeRunner(ClientA), token);

        await using var freshContext = new MeisterProPRDbContext(this._options);
        var storedToken = await new RunnerRegistry(freshContext).FindTokenAsync(token.TokenLookupHash);

        Assert.Equal(1, storedToken!.UseCount);
        Assert.Equal([ClientA], storedToken.ClientScope);
    }

    [Fact]
    public async Task RevokingARunner_IsPersisted()
    {
        var token = MakeToken(ClientA);
        var runner = MakeRunner(ClientA);
        this._dbContext.RunnerRegistrationTokens.Add(token);
        await this._dbContext.SaveChangesAsync();
        await this._registry.AddAsync(runner, token);

        runner.Revoke(DateTimeOffset.UtcNow);
        await this._registry.UpdateAsync(runner);

        await using var freshContext = new MeisterProPRDbContext(this._options);
        var stored = await new RunnerRegistry(freshContext).FindByIdAsync(runner.Id);

        Assert.Equal(RunnerState.Revoked, stored!.State);
        Assert.NotNull(stored.RevokedAt);
        Assert.False(stored.CoversClient(ClientA));
    }

    [Fact]
    public async Task ReassigningScope_ReplacesItRatherThanAppending()
    {
        var token = MakeToken(ClientA);
        var runner = MakeRunner(ClientA);
        this._dbContext.RunnerRegistrationTokens.Add(token);
        await this._dbContext.SaveChangesAsync();
        await this._registry.AddAsync(runner, token);

        runner.AssignClientScope([ClientB]);
        await this._registry.UpdateAsync(runner);

        await using var freshContext = new MeisterProPRDbContext(this._options);
        var stored = await new RunnerRegistry(freshContext).FindByIdAsync(runner.Id);

        Assert.Equal([ClientB], stored!.ClientScope);
    }
}
