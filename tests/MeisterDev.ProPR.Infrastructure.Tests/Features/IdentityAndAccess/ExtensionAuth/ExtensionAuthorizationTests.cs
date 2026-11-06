// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.Features.IdentityAndAccess.ExtensionAuth;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.ExtensionAuth;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Microsoft.Extensions.Time.Testing;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.IdentityAndAccess.ExtensionAuth;

public sealed class ExtensionAuthorizationTests
{
    private const string Issuer = "https://propr.test/api";
    private const string Callback = "https://abcdefghijklmnopabcdefghijklmnop.chromiumapp.org/";
    private const string Verifier = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJK";
    private readonly AppUser user = new() { Id = Guid.NewGuid(), Username = "alice", IsActive = true };
    private readonly RefreshToken session;
    private readonly IUserRepository users = Substitute.For<IUserRepository>();
    private readonly IRefreshTokenRepository sessions = Substitute.For<IRefreshTokenRepository>();
    private readonly IJwtTokenService jwt = Substitute.For<IJwtTokenService>();
    private readonly MeisterProPRDbContext db;
    private readonly ExtensionAuthorizationService service;
    private readonly FakeTimeProvider clock = new(DateTimeOffset.UtcNow);

    public ExtensionAuthorizationTests()
    {
        this.db = new MeisterProPRDbContext(new DbContextOptionsBuilder<MeisterProPRDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        this.session = new RefreshToken
            { Id = Guid.NewGuid(), UserId = this.user.Id, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), LastUsedAt = DateTimeOffset.UtcNow };
        this.users.GetByIdAsync(this.user.Id, Arg.Any<CancellationToken>()).Returns(this.user);
        this.sessions.GetActiveByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(this.session);
        this.sessions.GetActiveByIdAsync(this.session.Id, Arg.Any<CancellationToken>()).Returns(this.session);
        this.jwt.GenerateExtensionAccessToken(this.user, Arg.Any<Guid>(), this.session.Id).Returns("access-only");
        this.service = new ExtensionAuthorizationService(
            this.db, new SecretProtectionCodec(new EphemeralDataProtectionProvider()), new ExternalSignInRedemptions(this.db), this.users, this.sessions,
            this.jwt, new ExtensionAuthorizationOptions(Issuer, [Callback]), this.clock);
    }

    private static ExtensionAuthorizationRequest Request(string? prompt = null) => new(
        "propr-lens", Callback, "code", AuthorizationCodeFlow.ComputeCodeChallenge(Verifier), "S256", "state-123", prompt);

    [Fact]
    public async Task Consent_CodeExchange_AndReplayFollowWebsiteSession()
    {
        var start = await this.service.StartAsync(Request(), "cookie", default);
        Assert.NotNull(start.PendingRequest);
        var consent = await this.service.GetConsentAsync(start.PendingRequest!, this.user.Id, "cookie", default);
        Assert.NotNull(consent);
        var approved = await this.service.DecideAsync(start.PendingRequest!, this.user.Id, "cookie", true, default);
        var code = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(approved.RedirectUrl!).Query)["code"].ToString();
        var exchange = new ExtensionTokenRequest("propr-lens", Callback, code, Verifier, "authorization_code");
        var answer = await this.service.ExchangeAsync(exchange, default);
        Assert.Equal("access-only", answer?.AccessToken);
        Assert.Equal(900, answer?.ExpiresIn);
        Assert.Null(await this.service.ExchangeAsync(exchange, default));
        var grant = Assert.Single(await this.service.ListGrantsAsync(this.user.Id, default));
        Assert.True(await this.service.IsAccessActiveAsync(this.user.Id, grant.Id, this.session.Id, default));
        await this.service.RevokeAsync(this.user.Id, grant.Id, default);
        Assert.False(await this.service.IsAccessActiveAsync(this.user.Id, grant.Id, this.session.Id, default));
    }

    [Fact]
    public async Task WrongVerifierDoesNotConsumeCode()
    {
        var start = await this.service.StartAsync(Request(), "cookie", default);
        var approved = await this.service.DecideAsync(start.PendingRequest!, this.user.Id, "cookie", true, default);
        var code = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(approved.RedirectUrl!).Query)["code"].ToString();
        Assert.Null(await this.service.ExchangeAsync(new("propr-lens", Callback, code, new string('x', 43), "authorization_code"), default));
        Assert.NotNull(await this.service.ExchangeAsync(new("propr-lens", Callback, code, Verifier, "authorization_code"), default));
    }

    [Fact]
    public async Task SilentAuthorizationRequiresConsentAndAnActiveWebsiteSession()
    {
        var start = await this.service.StartAsync(Request("none"), "cookie", default);
        Assert.Contains("consent_required", start.RedirectUrl);
        this.sessions.GetActiveByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((RefreshToken?)null);
        Assert.Contains("login_required", (await this.service.StartAsync(Request("none"), "cookie", default)).RedirectUrl);
    }

    [Theory]
    [InlineData("https://evil.test/")]
    [InlineData(Callback + "other")]
    public async Task UnregisteredCallbacksNeverReceiveRedirects(string callback)
    {
        var result = await this.service.StartAsync(Request() with { RedirectUri = callback }, "cookie", default);
        Assert.Null(result.RedirectUrl);
        Assert.Equal("invalid_request", result.Error);
    }

    [Fact]
    public async Task InactiveBoundSessionAndDifferentConsentUserAreRejected()
    {
        var start = await this.service.StartAsync(Request(), "cookie", default);
        Assert.Null(await this.service.GetConsentAsync(start.PendingRequest!, Guid.NewGuid(), "cookie", default));
        this.sessions.GetActiveByIdAsync(this.session.Id, Arg.Any<CancellationToken>()).Returns((RefreshToken?)null);
        var approved = await this.service.DecideAsync(start.PendingRequest!, this.user.Id, "cookie", true, default);
        var code = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(approved.RedirectUrl!).Query)["code"].ToString();
        Assert.Null(await this.service.ExchangeAsync(new("propr-lens", Callback, code, Verifier, "authorization_code"), default));
    }

    [Fact]
    public async Task CodesAndPendingRequestsExpireAndWebsiteLogoutRevokesAccess()
    {
        var start = await this.service.StartAsync(Request(), "cookie", default);
        var pending = await this.service.StartAsync(Request(), "cookie", default);
        var approved = await this.service.DecideAsync(start.PendingRequest!, this.user.Id, "cookie", true, default);
        var code = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(approved.RedirectUrl!).Query)["code"].ToString();
        this.clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(await this.service.ExchangeAsync(new("propr-lens", Callback, code, Verifier, "authorization_code"), default));
        Assert.NotNull(await this.service.GetConsentAsync(pending.PendingRequest!, this.user.Id, "cookie", default));
        this.clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(await this.service.GetConsentAsync(pending.PendingRequest!, this.user.Id, "cookie", default));
        var grant = Assert.Single(await this.service.ListGrantsAsync(this.user.Id, default));
        Assert.True(await this.service.IsAccessActiveAsync(this.user.Id, grant.Id, this.session.Id, default));
        this.sessions.GetActiveByIdAsync(this.session.Id, Arg.Any<CancellationToken>()).Returns((RefreshToken?)null);
        Assert.False(await this.service.IsAccessActiveAsync(this.user.Id, grant.Id, this.session.Id, default));
    }

    [Fact]
    public async Task ChangedClientCallbackAndAlteredCodeAreRejected()
    {
        var start = await this.service.StartAsync(Request(), "cookie", default);
        var approved = await this.service.DecideAsync(start.PendingRequest!, this.user.Id, "cookie", true, default);
        var code = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(approved.RedirectUrl!).Query)["code"].ToString();
        Assert.Null(await this.service.ExchangeAsync(new("another-client", Callback, code, Verifier, "authorization_code"), default));
        Assert.Null(await this.service.ExchangeAsync(new("propr-lens", Callback + "other", code, Verifier, "authorization_code"), default));
        Assert.Null(await this.service.ExchangeAsync(new("propr-lens", Callback, code + "altered", Verifier, "authorization_code"), default));
        Assert.NotNull(await this.service.ExchangeAsync(new("propr-lens", Callback, code, Verifier, "authorization_code"), default));
    }

    [Fact]
    public async Task DenialNeverGrantsAccessAndPendingRequestCannotBeApprovedTwice()
    {
        var start = await this.service.StartAsync(Request(), "cookie", default);
        Assert.Contains("access_denied", (await this.service.DecideAsync(start.PendingRequest!, this.user.Id, "cookie", false, default)).RedirectUrl);
        Assert.Empty(await this.service.ListGrantsAsync(this.user.Id, default));
        Assert.Equal("invalid_request", (await this.service.DecideAsync(start.PendingRequest!, this.user.Id, "cookie", true, default)).Error);
    }
}
