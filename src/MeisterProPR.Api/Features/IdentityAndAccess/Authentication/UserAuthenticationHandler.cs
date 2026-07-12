// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Api.Features.IdentityAndAccess.Authentication;

/// <summary>
///     First-class authentication scheme for interactive users. Delegates credential resolution to
///     <see cref="CallerIdentityResolver" />, which populates the <c>HttpContext.Items</c> contract read by
///     <see cref="MeisterProPR.Api.Extensions.AuthHelpers" /> and yields a <see cref="System.Security.Claims.ClaimsPrincipal" />.
///     Publishing the caller as <c>HttpContext.User</c> lets the deny-by-default authorization fallback see them.
/// </summary>
public sealed class UserAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>Marks the request as carrying a credential for a disabled account so the challenge can explain the 401.</summary>
    internal const string DisabledAccountItemKey = "__meister_auth_disabled_account";

    /// <inheritdoc cref="AuthenticationHandler{TOptions}(IOptionsMonitor{TOptions}, ILoggerFactory, UrlEncoder)" />
    public UserAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var resolution = await CallerIdentityResolver.ResolveAsync(this.Context, this.Scheme.Name);

        if (resolution.AccountDisabled)
        {
            this.Context.Items[DisabledAccountItemKey] = true;
            return AuthenticateResult.Fail("User account is not active.");
        }

        if (resolution.Principal is null)
        {
            return AuthenticateResult.NoResult();
        }

        return AuthenticateResult.Success(new AuthenticationTicket(resolution.Principal, this.Scheme.Name));
    }

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        this.Response.StatusCode = StatusCodes.Status401Unauthorized;
        var message = this.Context.Items.ContainsKey(DisabledAccountItemKey)
            ? "User account is not active."
            : "Valid credentials required.";
        return this.Response.WriteAsJsonAsync(new { error = message });
    }
}
