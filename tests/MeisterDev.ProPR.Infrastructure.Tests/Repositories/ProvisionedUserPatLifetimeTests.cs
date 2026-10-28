// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Security;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

public sealed class ProvisionedUserPatLifetimeTests
{
    private const string LimitKey = "MEISTER_PROVISIONED_USER_PAT_MAX_DAYS";

    [Theory]
    [InlineData(null, 90)]
    [InlineData(120, 90)]
    [InlineData(30, 30)]
    public async Task AddAsync_ForAProvisionedUser_AppliesTheLimitAndKeepsAnEarlierExpiry(int? requestedDays, int expectedDays)
    {
        await using var services = BuildServices("90");
        var db = services.GetRequiredService<MeisterProPRDbContext>();
        var userId = await SeedUserAsync(db, provisioned: true, administrator: false);
        var createdAt = DateTimeOffset.UtcNow;
        var token = new UserPat
        {
            UserId = userId,
            TokenHash = "hash",
            Label = "Browser extension",
            CreatedAt = createdAt,
            ExpiresAt = requestedDays.HasValue ? createdAt.AddDays(requestedDays.Value) : null,
        };

        await services.GetRequiredService<IUserPatRepository>().AddAsync(token);

        Assert.Equal(createdAt.AddDays(expectedDays), token.ExpiresAt);
        Assert.Equal(token.ExpiresAt, (await db.UserPats.SingleAsync()).ExpiresAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetActiveByRawTokenAsync_AnOlderCustomerTokenCannotAuthenticateBeyondTheLimit(bool hasLookupHash)
    {
        await using var services = BuildServices("90");
        var db = services.GetRequiredService<MeisterProPRDbContext>();
        var userId = await SeedUserAsync(db, provisioned: true, administrator: false);
        var record = await SeedTokenAsync(db, userId, DateTimeOffset.UtcNow.AddDays(-91), hasLookupHash);

        var token = await services.GetRequiredService<IUserPatRepository>().GetActiveByRawTokenAsync("mpr_customer_test");

        Assert.Null(token);
        Assert.Null(record.LastUsedAt);
        Assert.Equal(hasLookupHash, record.TokenLookupHash is not null);
    }

    [Fact]
    public async Task GetActiveByRawTokenAsync_AnOlderCustomerTokenReturnsItsEffectiveExpiryWithoutChangingTheStoredDate()
    {
        await using var services = BuildServices("90");
        var db = services.GetRequiredService<MeisterProPRDbContext>();
        var userId = await SeedUserAsync(db, provisioned: true, administrator: false);
        var record = await SeedTokenAsync(db, userId, DateTimeOffset.UtcNow.AddDays(-30), hasLookupHash: true);

        var token = await services.GetRequiredService<IUserPatRepository>().GetActiveByRawTokenAsync("mpr_customer_test");

        Assert.NotNull(token);
        Assert.Equal(record.CreatedAt.AddDays(90), token.ExpiresAt);
        Assert.Null(record.ExpiresAt);
        Assert.NotNull(record.LastUsedAt);
    }

    [Fact]
    public async Task ListForUserAsync_OmitsTokensPastTheLimitAndReturnsTheEffectiveExpiry()
    {
        await using var services = BuildServices("90");
        var db = services.GetRequiredService<MeisterProPRDbContext>();
        var userId = await SeedUserAsync(db, provisioned: true, administrator: false);
        await SeedTokenAsync(db, userId, DateTimeOffset.UtcNow.AddDays(-91), hasLookupHash: false);
        var current = await SeedTokenAsync(db, userId, DateTimeOffset.UtcNow.AddDays(-30), hasLookupHash: false);

        var tokens = await services.GetRequiredService<IUserPatRepository>().ListForUserAsync(userId);

        var token = Assert.Single(tokens);
        Assert.Equal(current.Id, token.Id);
        Assert.Equal(current.CreatedAt.AddDays(90), token.ExpiresAt);
        Assert.Empty(token.TokenHash);
        Assert.Null(token.TokenLookupHash);
    }

    [Theory]
    [InlineData(false, false, "90")]
    [InlineData(false, true, "90")]
    [InlineData(true, true, "90")]
    [InlineData(true, false, null)]
    public async Task ExemptAccountsAndAnUnsetLimit_KeepTokensWithoutExpiry(bool provisioned, bool administrator, string? limit)
    {
        await using var services = BuildServices(limit);
        var db = services.GetRequiredService<MeisterProPRDbContext>();
        var userId = await SeedUserAsync(db, provisioned, administrator);
        await SeedTokenAsync(db, userId, DateTimeOffset.UtcNow.AddYears(-1), hasLookupHash: true);
        var repository = services.GetRequiredService<IUserPatRepository>();

        var existingToken = await repository.GetActiveByRawTokenAsync("mpr_customer_test");
        var newToken = new UserPat { UserId = userId, TokenHash = "hash", Label = "Token" };
        await repository.AddAsync(newToken);
        var tokens = await repository.ListForUserAsync(userId);

        Assert.NotNull(existingToken);
        Assert.Null(existingToken.ExpiresAt);
        Assert.Null(newToken.ExpiresAt);
        Assert.All(tokens, token => Assert.Null(token.ExpiresAt));
        Assert.Equal(2, tokens.Count);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("not-a-number")]
    [InlineData("3651")]
    [InlineData("")]
    public async Task InvalidLifetimeSetting_StopsTheHost(string limit)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [LimitKey] = limit });
        builder.Services.AddIdentityAndAccessModule(builder.Configuration);
        using var host = builder.Build();

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains(LimitKey, failure.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider BuildServices(string? limit)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["DB_CONNECTION_STRING"] = "Host=unused;Database=unused",
                [LimitKey] = limit,
            }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIdentityAndAccessModule(configuration);
        services.AddDbContext<MeisterProPRDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        return services.BuildServiceProvider();
    }

    private static async Task<Guid> SeedUserAsync(MeisterProPRDbContext db, bool provisioned, bool administrator)
    {
        var user = new AppUserRecord
        {
            Id = Guid.NewGuid(),
            Username = Guid.NewGuid().ToString(),
            GlobalRole = administrator ? AppUserRole.Admin : AppUserRole.User,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.AppUsers.Add(user);
        if (provisioned)
        {
            db.ProvisionedUsers.Add(
                new ProvisionedUserRecord
                {
                    UserId = user.Id,
                    User = user,
                    Issuer = "https://identity.example.test",
                    Subject = user.Id.ToString(),
                    Email = "customer@example.test",
                    CreatedAt = user.CreatedAt,
                    UpdatedAt = user.CreatedAt,
                });
        }

        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<UserPatRecord> SeedTokenAsync(MeisterProPRDbContext db, Guid userId, DateTimeOffset createdAt, bool hasLookupHash)
    {
        const string token = "mpr_customer_test";
        var record = new UserPatRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = BCrypt.Net.BCrypt.HashPassword(token),
            TokenLookupHash = hasLookupHash ? PatTokenLookupHash.Compute(token) : null,
            Label = "Existing token",
            CreatedAt = createdAt,
        };
        db.UserPats.Add(record);
        await db.SaveChangesAsync();
        return record;
    }
}
