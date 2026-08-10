// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Claims;
using System.Text.Encodings.Web;
using MeisterDev.ProPR.Api.Features.Reviewing.Runners;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Features.Reviewing.Runners;

public sealed class RunnerAuthenticationHandlerTests
{
    private static readonly Guid TenantId = Guid.Parse("15151515-1515-1515-1515-151515151515");

    private readonly IRunnerRegistrationService _registration = Substitute.For<IRunnerRegistrationService>();

    private static ReviewRunner Enrolled()
    {
        return new ReviewRunner(
            Guid.Parse("16161616-1616-1616-1616-161616161616"),
            TenantId,
            "runner-01",
            [],
            1,
            "hashed",
            "LOOKUP",
            DateTimeOffset.UtcNow.AddDays(30),
            DateTimeOffset.UtcNow);
    }

    private async Task<(AuthenticateResult Result, HttpContext Context)> AuthenticateAsync(string? credential)
    {
        // The handler reads its options during initialization, so the monitor has to return real ones.
        var optionsMonitor = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        optionsMonitor.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());

        var handler = new RunnerAuthenticationHandler(
            optionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            this._registration);

        var context = new DefaultHttpContext();
        if (credential is not null)
        {
            context.Request.Headers[RunnerAuthenticationDefaults.CredentialHeader] = credential;
        }

        await handler.InitializeAsync(
            new AuthenticationScheme(
                RunnerAuthenticationDefaults.Scheme,
                null,
                typeof(RunnerAuthenticationHandler)),
            context);

        return (await handler.AuthenticateAsync(), context);
    }

    [Fact]
    public async Task AValidCredential_YieldsTheRunnersIdentity()
    {
        var runner = Enrolled();
        this._registration.AuthenticateAsync("secret", Arg.Any<CancellationToken>()).Returns(runner);

        var (result, _) = await this.AuthenticateAsync("secret");

        Assert.True(result.Succeeded);
        Assert.Equal(
            runner.Id.ToString("D"),
            result.Principal!.FindFirst(RunnerAuthenticationDefaults.RunnerIdClaim)?.Value);
        Assert.Equal(
            TenantId.ToString("D"),
            result.Principal.FindFirst(RunnerAuthenticationDefaults.TenantIdClaim)?.Value);
    }

    // The identity the lease authorization compares against comes from the credential, never from the
    // request. A caller that could name its own identity could name somebody else's.
    [Fact]
    public async Task TheResolvedIdentity_IsReadFromThePrincipalAndNotTheRequest()
    {
        var runner = Enrolled();
        this._registration.AuthenticateAsync("secret", Arg.Any<CancellationToken>()).Returns(runner);
        var (result, context) = await this.AuthenticateAsync("secret");
        context.User = new ClaimsPrincipal(result.Principal!.Identity!);

        Assert.Equal(runner.Id, RunnerCallerIdentity.RunnerId(context));
    }

    [Fact]
    public async Task ARequestWithNoCredential_IsNotAuthenticated()
    {
        var (result, _) = await this.AuthenticateAsync(null);

        Assert.False(result.Succeeded);
        // No result rather than a failure: a request that carries no runner credential is not a rejected
        // runner, it is a request for some other scheme to answer.
        Assert.True(result.None);
        await this._registration.DidNotReceive().AuthenticateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // One failure for unknown, expired, and revoked. Distinguishing them tells a caller which it holds.
    [Fact]
    public async Task ACredentialTheServiceRejects_Fails()
    {
        this._registration.AuthenticateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ReviewRunner?)null);

        var (result, _) = await this.AuthenticateAsync("stale-or-revoked-or-invented");

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task AnUnauthenticatedRequest_ResolvesNoRunnerIdentity()
    {
        var (_, context) = await this.AuthenticateAsync(null);

        Assert.Null(RunnerCallerIdentity.RunnerId(context));
    }

    // Recording that a runner was heard from belongs to the registration service, because only it can write
    // the row. This asserts the handler delegates rather than that the field changed: a handler that mutates
    // the entity itself satisfies the second and still never persists anything, which is what it used to do.
    [Fact]
    public async Task AuthenticatingARunner_LeavesRecordingItToTheRegistrationService()
    {
        var runner = Enrolled();
        this._registration.AuthenticateAsync("secret", Arg.Any<CancellationToken>()).Returns(runner);

        var (result, _) = await this.AuthenticateAsync("secret");

        Assert.True(result.Succeeded);
        await this._registration.Received(1).AuthenticateAsync("secret", Arg.Any<CancellationToken>());
    }
}
