// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Hosting;

namespace MeisterDev.Ai.Providers.ExampleAddIn;

/// <summary>
///     The credential flow a family with a vendor sign-in runs, written against the contract assembly alone.
/// </summary>
/// <remarks>
///     <para>
///         It exists so the host primitives are exercised from the position an add-in author is in: a primitive
///         that cannot be called from a project holding only the contract, or a step of this flow that cannot be
///         expressed with the seven, is a compile error here rather than a discovery made while writing the first
///         private family.
///     </para>
///     <para>
///         The instance spans one invocation rather than one call, because the listener port has to stay held
///         from the moment the operator is sent to the vendor until the callback arrives. Disposing the action
///         releases the port, and every exit path below reaches that.
///     </para>
/// </remarks>
public sealed class ExampleConnectAction : IAsyncDisposable
{
    /// <summary>The name the listener port is leased under.</summary>
    public const string ListenerResource = "listener-port";

    /// <summary>Where the vendor's sign-in and token endpoints are.</summary>
    private static readonly Uri VendorAuthorizeEndpoint = new("https://api.example.com/authorize");

    private static readonly Uri VendorTokenEndpoint = new("https://api.example.com/token");

    private IProviderNamedLease? _listener;

    /// <summary>Starts the flow: take the port, keep the handshake, and send the operator to the vendor.</summary>
    /// <param name="context">What the host allows against this connection and this invocation.</param>
    /// <param name="state">The opaque value the vendor will hand back.</param>
    public async Task<ProviderActionResult> StartAsync(IProviderActionContext context, string state)
    {
        ArgumentNullException.ThrowIfNull(context);

        var outcome = await context.Leases.AcquireAsync(ListenerResource, TimeSpan.FromSeconds(5), context.Cancellation);
        if (outcome.Lease is not { } lease)
        {
            return ProviderActionResult.Failed($"The callback port is held by the connection '{outcome.HeldByConnection}'.");
        }

        this._listener = lease;

        try
        {
            await context.Store.WriteAsync(
                state,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["codeVerifier"] = "a-verifier" },
                DateTimeOffset.UtcNow.AddMinutes(10),
                context.Cancellation);
        }
        catch
        {
            // The operator was never sent anywhere, so nothing is going to arrive on the port and the next
            // connection of this family can have it back now.
            await this.DisposeAsync();
            throw;
        }

        // The state is opaque and arbitrary, so it is escaped rather than interpolated: a value carrying '&' or
        // '#' would otherwise end the parameter and change what the vendor is asked for.
        var authorize = $"{VendorAuthorizeEndpoint}?state={Uri.EscapeDataString(state)}";
        return ProviderActionResult.OpenUrl(authorize, true);
    }

    /// <summary>The fallback every flow keeps: ask the operator to paste the address the vendor redirected to.</summary>
    /// <param name="fields">The value to ask for, declared the way a configuration field is.</param>
    public static ProviderActionResult AskForTheCallbackUrl(IReadOnlyList<ProviderDeclaredField> fields)
    {
        return ProviderActionResult.ShowForm(fields);
    }

    /// <summary>
    ///     Finishes the flow: claim the handshake, exchange the code, store the credential, report the state of
    ///     it, and close the invocation.
    /// </summary>
    /// <param name="context">What the host allows against this connection and this invocation.</param>
    /// <param name="state">The opaque value the vendor handed back.</param>
    /// <param name="code">The authorization code the vendor handed back.</param>
    public async Task<ProviderActionResult> CompleteAsync(
        IProviderActionContext context,
        string state,
        string code)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            return await this.ExchangeAsync(context, state, code);
        }
        finally
        {
            // The invocation is over however it ended, so the port goes back.
            await this.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (this._listener is { } lease)
        {
            this._listener = null;
            await lease.DisposeAsync();
        }
    }

    private async Task<ProviderActionResult> ExchangeAsync(
        IProviderActionContext context,
        string state,
        string code)
    {
        var claim = await context.Store.ClaimAsync(state, context.Cancellation);
        if (!claim.Claimed)
        {
            var message = claim.Refusal switch
            {
                ProviderClaimRefusal.NoSuchEntry => "That sign-in did not start here.",
                ProviderClaimRefusal.Expired => "The sign-in took too long. Start it again.",
                ProviderClaimRefusal.AlreadyConsumed => "That sign-in was already completed.",
                ProviderClaimRefusal.WrongPrincipal => "Another administrator started this sign-in.",
                _ => "The sign-in could not be completed.",
            };

            await context.Invocation.ReportFailedAsync(message, context.Cancellation);
            return ProviderActionResult.Failed(message);
        }

        using var client = context.Http.Create(ProviderHttpPurpose.Admin);
        using var exchange = await client.PostAsync(
            VendorTokenEndpoint,
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["code"] = code,
                    ["code_verifier"] = claim.Values["codeVerifier"],
                }),
            context.Cancellation);

        // A refused exchange answers with a status and a body describing the refusal. Storing that body would
        // put the refusal where the access token belongs, and the connection would then be reported healthy and
        // fail on its first model call with a credential nothing can explain.
        if (!exchange.IsSuccessStatusCode)
        {
            var refused = $"The provider refused the token exchange (HTTP {(int)exchange.StatusCode}).";

            await context.Health.ReportAsync(AiCredentialHealth.NeedsReauthorization, refused, context.Cancellation);
            await context.Invocation.ReportFailedAsync(refused, context.Cancellation);
            return ProviderActionResult.Failed(refused);
        }

        await using var session = await context.Credentials.OpenAsync(context.Cancellation);
        var stored = await session.ReadAsync(context.Cancellation);
        await session.WriteAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ExampleCredentialRenewal.AccessTokenField] =
                    await exchange.Content.ReadAsStringAsync(context.Cancellation),
                [ExampleCredentialRenewal.RefreshTokenField] =
                    stored.Fields.GetValueOrDefault(ExampleCredentialRenewal.RefreshTokenField, string.Empty),
            },
            DateTimeOffset.UtcNow.AddHours(1),
            context.Cancellation);
        await session.CompleteAsync(context.Cancellation);

        await context.Health.ReportAsync(AiCredentialHealth.Healthy, null, context.Cancellation);
        await context.Invocation.ReportCompletedAsync("The account is connected.", context.Cancellation);

        return ProviderActionResult.Completed("The account is connected.");
    }
}
