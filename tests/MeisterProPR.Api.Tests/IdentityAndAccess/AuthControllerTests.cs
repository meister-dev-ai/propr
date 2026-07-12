// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Controllers;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace MeisterProPR.Api.Tests.IdentityAndAccess;

public sealed class AuthControllerTests
{
    private static readonly SessionPolicy Policy = new()
    {
        IdleTimeout = TimeSpan.FromHours(8),
        AbsoluteLifetime = TimeSpan.FromHours(72),
    };

    [Fact]
    public async Task Login_IssuesRefreshTokenWithConfiguredAbsoluteLifetimeAndSeedsLastUsed()
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = "alice",
            PasswordHash = "stored-hash",
            IsActive = true,
        };
        var users = Substitute.For<IUserRepository>();
        users.GetByUsernameAsync("alice", Arg.Any<CancellationToken>()).Returns(user);
        var hasher = Substitute.For<IPasswordHashService>();
        hasher.Verify("pw", "stored-hash").Returns(true);
        var jwt = Substitute.For<IJwtTokenService>();
        jwt.GenerateAccessToken(user).Returns("access-token");
        var refreshTokens = Substitute.For<IRefreshTokenRepository>();
        RefreshToken? issued = null;
        refreshTokens.When(r => r.AddAsync(Arg.Any<RefreshToken>(), Arg.Any<CancellationToken>()))
            .Do(ci => issued = ci.Arg<RefreshToken>());

        var controller = CreateController(users, refreshTokens, hasher, jwt);

        var before = DateTimeOffset.UtcNow;
        var result = await controller.Login(new LoginRequest("alice", "pw"), CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(issued);
        // The absolute cap is exactly the configured window measured from issuance.
        Assert.Equal(issued!.CreatedAt + Policy.AbsoluteLifetime, issued.ExpiresAt);
        // A fresh token starts its idle window at issuance.
        Assert.Equal(issued.CreatedAt, issued.LastUsedAt);
        Assert.InRange(issued.CreatedAt, before, after);
    }

    [Fact]
    public async Task Refresh_WhenTokenActive_AdvancesLastUsedTimestamp()
    {
        var user = new AppUser { Id = Guid.NewGuid(), Username = "alice", IsActive = true };
        var users = Substitute.For<IUserRepository>();
        users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        var tokenId = Guid.NewGuid();
        var token = new RefreshToken
        {
            Id = tokenId,
            UserId = user.Id,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            LastUsedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        var refreshTokens = Substitute.For<IRefreshTokenRepository>();
        refreshTokens.GetActiveByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(token);
        var jwt = Substitute.For<IJwtTokenService>();
        jwt.GenerateAccessToken(user).Returns("access-token");

        var controller = CreateController(users, refreshTokens, Substitute.For<IPasswordHashService>(), jwt);

        var result = await controller.Refresh(new RefreshRequest("raw-token"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        await refreshTokens.Received(1)
            .TouchLastUsedAsync(tokenId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_WhenTokenNotActive_ReturnsUnauthorizedAndDoesNotTouch()
    {
        var refreshTokens = Substitute.For<IRefreshTokenRepository>();
        refreshTokens.GetActiveByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((RefreshToken?)null);

        var controller = CreateController(
            Substitute.For<IUserRepository>(),
            refreshTokens,
            Substitute.For<IPasswordHashService>(),
            Substitute.For<IJwtTokenService>());

        var result = await controller.Refresh(new RefreshRequest("raw-token"), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        await refreshTokens.DidNotReceive()
            .TouchLastUsedAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    private static AuthController CreateController(
        IUserRepository users,
        IRefreshTokenRepository refreshTokens,
        IPasswordHashService hasher,
        IJwtTokenService jwt)
    {
        return new AuthController(
            users,
            refreshTokens,
            hasher,
            jwt,
            Substitute.For<IAccountLockoutService>(),
            Policy)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }
}
