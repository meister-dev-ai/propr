// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;
using MeisterDev.ProPR.Application.Features.IdentityAndAccess.ExtensionAuth;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.ExtensionAuth;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Services;
using MeisterDev.ProPR.TestSupport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.IdentityAndAccess.ExtensionAuth;

[Collection("PostgresIntegration4")]
public sealed class ExtensionAuthorizationPersistenceTests(PostgresContainerFixture fixture)
{
    [Fact]
    public async Task ExplicitConcurrentApprovalsReplaceAnOldScopeWithoutConflictingActiveGrants()
    {
        fixture.SkipIfUnavailable();
        var name = "propr_extension_upgrade_" + Guid.NewGuid().ToString("N");
        await fixture.CreateDatabaseAtMigrationAsync(name, "AddExtensionAuthorizationGrants");
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name, Pooling = false }.ConnectionString;
        try
        {
            var user = new AppUser { Id = Guid.NewGuid(), Username = "alice", IsActive = true };
            var users = Substitute.For<IUserRepository>();
            users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
            var jwt = Substitute.For<IJwtTokenService>();
            jwt.GenerateExtensionAccessToken(user, Arg.Any<Guid>(), Arg.Any<Guid>()).Returns("access-only");
            var codec = new SecretProtectionCodec(new EphemeralDataProtectionProvider());
            var policy = new SessionPolicy { IdleTimeout = TimeSpan.FromHours(8), AbsoluteLifetime = TimeSpan.FromHours(72) };
            var options = new ExtensionAuthorizationOptions("https://propr.test/api", ["https://extension.chromiumapp.org/propr"]);
            MeisterProPRDbContext Context() => new(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(connection, pg => pg.UseVector()).Options);

            ExtensionAuthorizationService Service(MeisterProPRDbContext context) => new(
                context, codec, new ExternalSignInRedemptions(context), users,
                new RefreshTokenRepository(context, policy), jwt, options);

            await using var setup = Context();
            setup.AppUsers.Add(new AppUserRecord { Id = user.Id, Username = user.Username, CreatedAt = DateTimeOffset.UtcNow });
            var oldId = Guid.NewGuid();
            setup.ExtensionAuthorizationGrants.Add(
                new ExtensionAuthorizationGrantRecord
                {
                    Id = oldId, UserId = user.Id, ClientId = ExtensionAuthorizationOptions.ClientId,
                    ScopeVersion = ExtensionAuthorizationGrant.CurrentScopeVersion - 1, GrantedAt = DateTimeOffset.UtcNow,
                });
            await setup.SaveChangesAsync();
            await new RefreshTokenRepository(setup, policy).AddAsync(
                new RefreshToken
                {
                    Id = Guid.NewGuid(), UserId = user.Id,
                    TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("cookie"))).ToLowerInvariant(),
                    CreatedAt = DateTimeOffset.UtcNow, LastUsedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                });
            var request = new ExtensionAuthorizationRequest(
                "propr-lens", options.RedirectUris[0], "code",
                AuthorizationCodeFlow.ComputeCodeChallenge("abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJK"), "S256", "state");
            var pendingA = await Service(setup).StartAsync(request, "cookie", default);
            var pendingB = await Service(setup).StartAsync(request, "cookie", default);
            Assert.NotNull(pendingA.PendingRequest);
            Assert.NotNull(pendingB.PendingRequest);
            await using var first = Context();
            await using var second = Context();
            var results = await Task.WhenAll(
                Service(first).DecideAsync(pendingA.PendingRequest!, user.Id, "cookie", true, default),
                Service(second).DecideAsync(pendingB.PendingRequest!, user.Id, "cookie", true, default));
            Assert.All(results, result => Assert.NotNull(result.RedirectUrl));
            var active = Assert.Single(await setup.ExtensionAuthorizationGrants.AsNoTracking().Where(grant => grant.RevokedAt == null).ToListAsync());
            Assert.Equal(ExtensionAuthorizationGrant.CurrentScopeVersion, active.ScopeVersion);
            Assert.NotEqual(oldId, active.Id);
            Assert.NotNull((await setup.ExtensionAuthorizationGrants.AsNoTracking().SingleAsync(grant => grant.Id == oldId)).RevokedAt);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(fixture.ConnectionString);
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task CodesAreRedeemedOnceAcrossContextsAndRevocationFollowsTheWebsiteSession()
    {
        fixture.SkipIfUnavailable();
        var name = "propr_extension_auth_" + Guid.NewGuid().ToString("N");
        await fixture.CreateDatabaseAtMigrationAsync(name, "AddExtensionAuthorizationGrants");
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name, Pooling = false }.ConnectionString;
        try
        {
            var user = new AppUser { Id = Guid.NewGuid(), Username = "alice", IsActive = true };
            var users = Substitute.For<IUserRepository>();
            users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
            var jwt = Substitute.For<IJwtTokenService>();
            jwt.GenerateExtensionAccessToken(user, Arg.Any<Guid>(), Arg.Any<Guid>()).Returns("access-only");
            var codec = new SecretProtectionCodec(new EphemeralDataProtectionProvider());
            var policy = new SessionPolicy { IdleTimeout = TimeSpan.FromHours(8), AbsoluteLifetime = TimeSpan.FromHours(72) };
            var options = new ExtensionAuthorizationOptions("https://propr.test/api", ["https://extension.chromiumapp.org/propr"]);
            MeisterProPRDbContext Context() => new(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(connection, pg => pg.UseVector()).Options);

            ExtensionAuthorizationService Service(MeisterProPRDbContext context) => new(
                context, codec,
                new ExternalSignInRedemptions(context), users, new RefreshTokenRepository(context, policy), jwt, options);

            await using var setup = Context();
            setup.AppUsers.Add(new AppUserRecord { Id = user.Id, Username = user.Username, CreatedAt = DateTimeOffset.UtcNow });
            await setup.SaveChangesAsync();
            var sessionId = Guid.NewGuid();
            var sessions = new RefreshTokenRepository(setup, policy);
            await sessions.AddAsync(
                new RefreshToken
                {
                    Id = sessionId, UserId = user.Id,
                    TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("cookie"))).ToLowerInvariant(),
                    CreatedAt = DateTimeOffset.UtcNow, LastUsedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
                });
            const string verifier = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJK";
            var request = new ExtensionAuthorizationRequest(
                "propr-lens", options.RedirectUris[0], "code", AuthorizationCodeFlow.ComputeCodeChallenge(verifier), "S256", "state");
            var started = await Service(setup).StartAsync(request, "cookie", default);
            var decision = await Service(setup).DecideAsync(started.PendingRequest!, user.Id, "cookie", true, default);
            var code = QueryHelpers.ParseQuery(new Uri(decision.RedirectUrl!).Query)["code"].ToString();
            var exchange = new ExtensionTokenRequest("propr-lens", options.RedirectUris[0], code, verifier, "authorization_code");
            await using var first = Context();
            await using var second = Context();
            var answers = await Task.WhenAll(Service(first).ExchangeAsync(exchange, default), Service(second).ExchangeAsync(exchange, default));
            Assert.Single(answers.Where(answer => answer is not null));
            var grant = Assert.Single(await Service(setup).ListGrantsAsync(user.Id, default));
            Assert.True(await Service(first).IsAccessActiveAsync(user.Id, grant.Id, sessionId, default));
            await Service(setup).RevokeAsync(user.Id, grant.Id, default);
            Assert.False(await Service(first).IsAccessActiveAsync(user.Id, grant.Id, sessionId, default));
            var next = await Service(setup).StartAsync(request, "cookie", default);
            Assert.NotNull(next.PendingRequest);
            await Service(setup).DecideAsync(next.PendingRequest!, user.Id, "cookie", true, default);
            var replacement = Assert.Single(await Service(setup).ListGrantsAsync(user.Id, default));
            Assert.NotEqual(grant.Id, replacement.Id);
            await sessions.RevokeAllForUserAsync(user.Id);
            Assert.False(await Service(first).IsAccessActiveAsync(user.Id, replacement.Id, sessionId, default));
            Assert.False(setup.Database.HasPendingModelChanges());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(fixture.ConnectionString);
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
            await command.ExecuteNonQueryAsync();
        }
    }
}
