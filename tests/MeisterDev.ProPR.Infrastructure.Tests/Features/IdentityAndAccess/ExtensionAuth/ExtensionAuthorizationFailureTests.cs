// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.IdentityAndAccess.ExtensionAuth;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.ExtensionAuth;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.IdentityAndAccess.ExtensionAuth;

public sealed class ExtensionAuthorizationFailureTests
{
    private const string Callback = "https://abcdefghijklmnopabcdefghijklmnop.chromiumapp.org/propr";
    private const string Verifier = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJK";
    private readonly string database = Guid.NewGuid().ToString();
    private readonly AppUser user = new() { Id = Guid.NewGuid(), Username = "alice", IsActive = true };
    private readonly RefreshToken session;
    private readonly IUserRepository users = Substitute.For<IUserRepository>();
    private readonly IRefreshTokenRepository sessions = Substitute.For<IRefreshTokenRepository>();
    private readonly IJwtTokenService jwt = Substitute.For<IJwtTokenService>();
    private readonly ISecretProtectionCodec codec = new SecretProtectionCodec(new EphemeralDataProtectionProvider());

    public ExtensionAuthorizationFailureTests()
    {
        this.session = new RefreshToken { Id = Guid.NewGuid(), UserId = this.user.Id, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        this.users.GetByIdAsync(this.user.Id, Arg.Any<CancellationToken>()).Returns(this.user);
        this.sessions.GetActiveByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(this.session);
        this.jwt.GenerateExtensionAccessToken(this.user, Arg.Any<Guid>(), this.session.Id).Returns("access-only");

        // The per-request access check reads the account and the website session from the database.
        using var setup = this.Context();
        setup.AppUsers.Add(new AppUserRecord { Id = this.user.Id, Username = this.user.Username, CreatedAt = DateTimeOffset.UtcNow });
        setup.RefreshTokens.Add(
            new RefreshTokenRecord
            {
                Id = this.session.Id, UserId = this.user.Id, TokenHash = "session-hash", CreatedAt = DateTimeOffset.UtcNow,
                LastUsedAt = DateTimeOffset.UtcNow, ExpiresAt = this.session.ExpiresAt,
            });
        setup.SaveChanges();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GrantPersistenceFailureConsumesTheRequestAndAFreshAuthorizationRecovers(bool canceled)
    {
        string pending;
        await using (var failed = this.Context(new GrantSaveFailure(canceled)))
        {
            var service = this.Service(failed);
            pending = (await service.StartAsync(Request(), "cookie", default)).PendingRequest!;
            if (canceled)
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => service.DecideAsync(pending, this.user.Id, "cookie", true, default));
            }
            else
            {
                await Assert.ThrowsAsync<DbUpdateException>(() => service.DecideAsync(pending, this.user.Id, "cookie", true, default));
            }
        }

        await using var recovered = this.Context();
        var retry = this.Service(recovered);
        Assert.Empty(await retry.ListGrantsAsync(this.user.Id, default));
        Assert.Null(await retry.GetConsentAsync(pending, this.user.Id, "cookie", default));
        Assert.Equal("invalid_request", (await retry.DecideAsync(pending, this.user.Id, "cookie", true, default)).Error);
        this.jwt.DidNotReceive().GenerateExtensionAccessToken(Arg.Any<AppUser>(), Arg.Any<Guid>(), Arg.Any<Guid>());
        var fresh = await retry.StartAsync(Request(), "cookie", default);
        var approved = await retry.DecideAsync(fresh.PendingRequest!, this.user.Id, "cookie", true, default);
        Assert.NotNull(await retry.ExchangeAsync(Exchange(approved), default));
    }

    [Fact]
    public async Task FailedSessionActivityConsumesTheCodeWithoutIssuingAccessAndReplayCannotKeepTheSessionAlive()
    {
        await using var db = this.Context();
        var service = this.Service(db);
        var pending = await service.StartAsync(Request(), "cookie", default);
        var approved = await service.DecideAsync(pending.PendingRequest!, this.user.Id, "cookie", true, default);
        var exchange = Exchange(approved);
        this.sessions.TouchLastUsedAsync(this.session.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Injected session activity failure")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExchangeAsync(exchange, default));
        Assert.Null(await service.ExchangeAsync(exchange, default));
        await this.sessions.Received(1).TouchLastUsedAsync(this.session.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        this.jwt.DidNotReceive().GenerateExtensionAccessToken(Arg.Any<AppUser>(), Arg.Any<Guid>(), Arg.Any<Guid>());
        this.sessions.TouchLastUsedAsync(this.session.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var fresh = await service.StartAsync(Request() with { Prompt = "none" }, "cookie", default);
        Assert.NotNull(await service.ExchangeAsync(Exchange(fresh), default));
        await this.sessions.Received(2).TouchLastUsedAsync(this.session.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    private MeisterProPRDbContext Context(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(this.database);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new(options.Options);
    }

    private ExtensionAuthorizationService Service(MeisterProPRDbContext db) => new(
        db, this.codec, new ExternalSignInRedemptions(db), this.users, this.sessions,
        new SessionPolicy { IdleTimeout = TimeSpan.FromHours(8), AbsoluteLifetime = TimeSpan.FromHours(72) }, this.jwt,
        new ExtensionAuthorizationOptions("https://propr.test/api", [Callback]));

    private static ExtensionAuthorizationRequest Request() => new(
        "propr-lens", Callback, "code", AuthorizationCodeFlow.ComputeCodeChallenge(Verifier), "S256", "state");

    private static ExtensionTokenRequest Exchange(ExtensionAuthorizationStart result) => new(
        "propr-lens", Callback,
        QueryHelpers.ParseQuery(new Uri(result.RedirectUrl!).Query)["code"].ToString(), Verifier, "authorization_code");

    private sealed class GrantSaveFailure(bool canceled) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<ExtensionAuthorizationGrantRecord>().Any(entry => entry.State == EntityState.Added))
            {
                if (canceled)
                {
                    throw new OperationCanceledException("Injected grant persistence cancellation");
                }

                throw new DbUpdateException("Injected grant persistence failure");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
