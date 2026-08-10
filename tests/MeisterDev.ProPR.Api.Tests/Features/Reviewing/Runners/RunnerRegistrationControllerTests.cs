// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Claims;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Api.Features.Reviewing.Runners;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Features.Reviewing.Runners;

/// <summary>
///     How a host becomes a runner. This is the only runner operation reachable without a credential, so
///     what it refuses matters as much as what it issues.
/// </summary>
public sealed class RunnerRegistrationControllerTests
{
    private static readonly Guid RunnerId = Guid.Parse("17171717-1717-1717-1717-171717171717");

    private readonly IRunnerRegistrationService _registration = Substitute.For<IRunnerRegistrationService>();

    [Fact]
    public async Task AValidToken_IsExchangedForACredential()
    {
        var expiry = DateTimeOffset.UtcNow.AddDays(30);
        this._registration.RegisterAsync(Arg.Any<RunnerRegistrationRequest>(), Arg.Any<CancellationToken>())
            .Returns(RunnerRegistrationResult.Enrolled(RunnerId, "issued", expiry));

        var response = await this.CreateController().Register(Request("operator-issued"), CancellationToken.None);

        var issued = Assert.IsType<RunnerCredentialResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Equal(RunnerId, issued.RunnerId);
        Assert.Equal("issued", issued.Credential);
        Assert.Equal(expiry, issued.ExpiresAt);
    }

    // The host declares its name and tags; it never names its tenant or the clients it may serve. Those are
    // stamped from the token, because a host that chose its own scope would be choosing its own permissions.
    [Fact]
    public async Task TheHost_DeclaresOnlyWhatItIsAllowedTo()
    {
        this._registration.RegisterAsync(Arg.Any<RunnerRegistrationRequest>(), Arg.Any<CancellationToken>())
            .Returns(RunnerRegistrationResult.Enrolled(RunnerId, "issued", DateTimeOffset.UtcNow.AddDays(30)));

        await this.CreateController().Register(
            new RunnerRegisterRequest
            {
                RegistrationToken = "operator-issued",
                DisplayName = "runner-01",
                Tags = "linux, big",
                ContractVersion = RunnerContractVersion.Current,
            },
            CancellationToken.None);

        await this._registration.Received(1).RegisterAsync(
            Arg.Is<RunnerRegistrationRequest>(request =>
                request.DisplayName == "runner-01"
                && request.Tags.Count == 2
                && request.Tags[0] == "linux"
                && request.Tags[1] == "big"),
            Arg.Any<CancellationToken>());
    }

    // One refusal for a token that is unknown, spent, expired, or revoked. Saying which turns a bad token
    // into a probe for a good one.
    [Fact]
    public async Task ARefusedToken_IsAnswered401WithoutSayingWhy()
    {
        this._registration.RegisterAsync(Arg.Any<RunnerRegistrationRequest>(), Arg.Any<CancellationToken>())
            .Returns(RunnerRegistrationResult.Refused("The registration token was not accepted."));

        var response = await this.CreateController().Register(Request("spent"), CancellationToken.None);

        var error = Assert.IsType<RunnerContractError>(Assert.IsType<UnauthorizedObjectResult>(response).Value);
        Assert.Equal(RunnerContractError.RegistrationRevoked, error.Code);
    }

    // Refused before the token is spent. A single-use token consumed by a control plane that cannot serve
    // the host would leave an operator issuing another one for the same machine.
    [Fact]
    public async Task AVersionThisControlPlaneCannotServe_IsRefusedWithoutSpendingTheToken()
    {
        var response = await this.CreateController().Register(
            new RunnerRegisterRequest
            {
                RegistrationToken = "operator-issued",
                DisplayName = "runner-01",
                ContractVersion = RunnerContractVersion.Current + 5,
            },
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response);
        await this._registration.DidNotReceive().RegisterAsync(Arg.Any<RunnerRegistrationRequest>(), Arg.Any<CancellationToken>());
    }

    // Renewal keeps the identity the server stamped. A runner that could rename itself here would be
    // re-enrolling under a scope nobody granted it.
    [Fact]
    public async Task Renewal_UsesTheAuthenticatedIdentityRatherThanTheBody()
    {
        this._registration.RenewCredentialAsync(RunnerId, Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(RunnerRegistrationResult.Enrolled(RunnerId, "renewed", DateTimeOffset.UtcNow.AddDays(30)));

        var controller = this.CreateController(authenticatedAs: RunnerId, presenting: "current-credential");
        var response = await controller.Renew(
            new RunnerCredentialRenewRequest { ContractVersion = RunnerContractVersion.Current },
            CancellationToken.None);

        var issued = Assert.IsType<RunnerCredentialResponse>(Assert.IsType<OkObjectResult>(response).Value);
        Assert.Equal("renewed", issued.Credential);
        await this._registration.Received(1).RenewCredentialAsync(RunnerId, "current-credential", RunnerContractVersion.Current, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenewalWithoutAnAuthenticatedRunner_IsRefused()
    {
        var response = await this.CreateController().Renew(
            new RunnerCredentialRenewRequest { ContractVersion = RunnerContractVersion.Current },
            CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(response);
    }

    private static RunnerRegisterRequest Request(string token)
    {
        return new RunnerRegisterRequest
        {
            RegistrationToken = token,
            DisplayName = "runner-01",
            ContractVersion = RunnerContractVersion.Current,
        };
    }

    private RunnerRegistrationController CreateController(Guid? authenticatedAs = null, string? presenting = null)
    {
        var http = new DefaultHttpContext();
        if (authenticatedAs is { } runnerId)
        {
            var identity = new ClaimsIdentity(RunnerAuthenticationDefaults.Scheme);
            identity.AddClaim(new Claim(RunnerAuthenticationDefaults.RunnerIdClaim, runnerId.ToString("D")));
            http.User = new ClaimsPrincipal(identity);
        }

        if (presenting is not null)
        {
            http.Request.Headers[RunnerAuthenticationDefaults.CredentialHeader] = presenting;
        }

        return new RunnerRegistrationController(this._registration)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }
}
