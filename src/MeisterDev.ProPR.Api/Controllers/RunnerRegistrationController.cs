// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Api.Features.Reviewing.Runners;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>
///     How a host becomes a runner, and how it keeps being one.
///     <para>
///         Enrollment is the one runner operation that cannot present a runner credential, because
///         obtaining one is the point. It presents an operator-issued registration token instead, which is
///         single-use and carries the scope the operator chose — the runner never names its own tenant or
///         which clients it may serve, because a host that could would be choosing its own permissions.
///     </para>
///     <para>
///         Renewal does present a credential, and is authorized like everything else. It exists so a
///         credential can expire without an operator having to re-enroll every host by hand, which is the
///         thing that makes short expiries survivable.
///     </para>
/// </summary>
[ApiController]
[Route("runners")]
public sealed class RunnerRegistrationController(IRunnerRegistrationService registration) : ControllerBase
{
    /// <summary>Enrolls a host presenting an operator-issued registration token.</summary>
    /// <param name="request">The token, the name the host reports, and the contract it speaks.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    // Anonymous because obtaining a credential is the point, and rate-limited with the same policy the
    // sign-in endpoints use: it is the one runner endpoint an attacker can reach, and a single-use token
    // is still a secret somebody can try values for.
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(RunnerCredentialResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RunnerContractError), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(RunnerContractError), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RunnerRegisterRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!RunnerContractVersion.IsSupported(request.ContractVersion))
        {
            // Refused before the token is spent. A single-use token consumed by a host this control plane
            // cannot serve would leave an operator issuing another for the same machine.
            return this.Conflict(RunnerContractError.ForUnsupportedVersion(request.ContractVersion));
        }

        var result = await registration.RegisterAsync(
            new RunnerRegistrationRequest(
                request.RegistrationToken,
                request.DisplayName,
                SplitTags(request.Tags),
                request.ContractVersion),
            ct);

        return result.Succeeded
            ? this.Ok(
                new RunnerCredentialResponse
                {
                    RunnerId = result.RunnerId!.Value,
                    Credential = result.Credential!,
                    ExpiresAt = result.ExpiresAt,
                })

            // One refusal for a token that is unknown, spent, expired, or revoked. Telling a caller which
            // it is holding turns a bad token into a probe.
            : this.Unauthorized(
                new RunnerContractError(
                    RunnerContractError.RegistrationRevoked,
                    result.Refusal ?? "The registration token was not accepted."));
    }

    /// <summary>Issues a fresh credential to an enrolled runner, keeping its identity and stamped scope.</summary>
    /// <param name="request">The contract the runner speaks.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    [HttpPost("credential/renew")]
    [Authorize(AuthenticationSchemes = RunnerAuthenticationDefaults.Scheme)]
    [ProducesResponseType(typeof(RunnerCredentialResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RunnerContractError), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(RunnerContractError), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Renew([FromBody] RunnerCredentialRenewRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var runnerId = RunnerCallerIdentity.RunnerId(this.HttpContext);
        if (runnerId is null)
        {
            return this.Unauthorized(new RunnerContractError(RunnerContractError.RegistrationRevoked, "No runner credential was resolved."));
        }

        if (!RunnerContractVersion.IsSupported(request.ContractVersion))
        {
            return this.Conflict(RunnerContractError.ForUnsupportedVersion(request.ContractVersion));
        }

        // The credential it is renewing comes from the header the authentication already validated, not
        // from the body: a caller that could name a credential could name somebody else's.
        var presented = this.Request.Headers[RunnerAuthenticationDefaults.CredentialHeader].ToString();
        var result = await registration.RenewCredentialAsync(runnerId.Value, presented, request.ContractVersion, ct);

        return result.Succeeded
            ? this.Ok(
                new RunnerCredentialResponse
                {
                    RunnerId = result.RunnerId!.Value,
                    Credential = result.Credential!,
                    ExpiresAt = result.ExpiresAt,
                })
            : this.Unauthorized(
                new RunnerContractError(
                    RunnerContractError.RegistrationRevoked,
                    result.Refusal ?? "The credential could not be renewed."));
    }

    private static IReadOnlyList<string> SplitTags(string? tags)
    {
        return string.IsNullOrWhiteSpace(tags)
            ? []
            : [.. tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }
}

/// <summary>A host asking to become a runner.</summary>
public sealed class RunnerRegisterRequest
{
    /// <summary>The single-use token an operator issued for this host.</summary>
    public string RegistrationToken { get; init; } = string.Empty;

    /// <summary>The name the host reports for itself, shown in the registry.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The tags this host declares, comma-separated.</summary>
    public string? Tags { get; init; }

    /// <summary>The contract version the host speaks.</summary>
    public int ContractVersion { get; init; }
}

/// <summary>An enrolled runner asking for a fresh credential.</summary>
public sealed class RunnerCredentialRenewRequest
{
    /// <summary>The contract version the runner speaks.</summary>
    public int ContractVersion { get; init; }
}

/// <summary>
///     A credential, returned exactly once. Nothing stores it in a form it can be read back from, so a
///     runner that loses it renews rather than recovers.
/// </summary>
public sealed class RunnerCredentialResponse
{
    /// <summary>The runner's identity in the registry.</summary>
    public Guid RunnerId { get; init; }

    /// <summary>The credential to present on every subsequent call.</summary>
    public string Credential { get; init; } = string.Empty;

    /// <summary>When it must be renewed by.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}
