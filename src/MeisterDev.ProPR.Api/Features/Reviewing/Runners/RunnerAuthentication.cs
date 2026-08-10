// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Security.Claims;
using System.Text.Encodings.Web;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Api.Features.Reviewing.Runners;

/// <summary>Names for the runner authentication scheme and the claims it issues.</summary>
public static class RunnerAuthenticationDefaults
{
    /// <summary>The scheme name runner endpoints authenticate with.</summary>
    public const string Scheme = "RunnerCredential";

    /// <summary>Header the runner presents its credential in.</summary>
    public const string CredentialHeader = "X-ProPR-Runner-Credential";

    /// <summary>Claim carrying the authenticated runner's identity.</summary>
    public const string RunnerIdClaim = "propr:runner_id";

    /// <summary>Claim carrying the runner's tenant.</summary>
    public const string TenantIdClaim = "propr:runner_tenant_id";
}

/// <summary>
///     Authenticates a runner from the credential it presents.
///     <para>
///         This is the piece every proxied operation was waiting on. The services already authorize against
///         the lease and its generation, but that check compares the caller's claimed identity to the lease
///         owner, and until something proved the identity the comparison was against an unproven string.
///         Registration issues the credential; this resolves it.
///     </para>
/// </summary>
public sealed class RunnerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IRunnerRegistrationService registration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = this.Context.Request.Headers[RunnerAuthenticationDefaults.CredentialHeader].ToString();
        if (string.IsNullOrWhiteSpace(presented))
        {
            return AuthenticateResult.NoResult();
        }

        var runner = await registration.AuthenticateAsync(presented, this.Context.RequestAborted);
        if (runner is null)
        {
            // One failure for a credential that is unknown, expired, or revoked. Distinguishing them would
            // tell a caller which of those it is holding.
            this.Logger.LogWarning("A runner credential was presented and was not accepted");
            return AuthenticateResult.Fail("The runner credential is not valid.");
        }

        var identity = new ClaimsIdentity(RunnerAuthenticationDefaults.Scheme);
        identity.AddClaim(new Claim(RunnerAuthenticationDefaults.RunnerIdClaim, runner.Id.ToString("D")));
        identity.AddClaim(new Claim(RunnerAuthenticationDefaults.TenantIdClaim, runner.TenantId.ToString("D")));
        identity.AddClaim(new Claim(ClaimTypes.Name, runner.DisplayName));

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), RunnerAuthenticationDefaults.Scheme));
    }
}

/// <summary>Reads the authenticated runner out of a request.</summary>
public static class RunnerCallerIdentity
{
    /// <summary>
    ///     The authenticated runner's identity, or null when the request carries none.
    ///     <para>
    ///         This is what the lease authorization compares against the lease owner, which is why it comes
    ///         from the authenticated principal and never from the request body: a caller that could name its
    ///         own identity could name somebody else's.
    ///     </para>
    /// </summary>
    public static Guid? RunnerId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var claim = context.User.FindFirst(RunnerAuthenticationDefaults.RunnerIdClaim)?.Value;
        return Guid.TryParse(claim, out var runnerId) ? runnerId : null;
    }
}
