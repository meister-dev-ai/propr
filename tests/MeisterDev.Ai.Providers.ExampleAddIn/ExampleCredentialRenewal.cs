// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Diagnostics;
using MeisterDev.Ai.Providers.Hosting;

namespace MeisterDev.Ai.Providers.ExampleAddIn;

/// <summary>
///     How a family keeps a usable credential on a client the host built once and calls for as long as a review
///     runs.
/// </summary>
/// <remarks>
///     <para>
///         Written against the contract assembly alone, so that a step of this which the primitives cannot
///         express is a compile error here rather than something discovered while writing the first private
///         family.
///     </para>
///     <para>
///         The shape is the one the primitives are built for. The ordinary call reads without taking the row, so
///         a review's parallel passes do not queue behind each other. A caller that finds the credential expiring
///         opens a session, which takes the row, and reads again inside it: by then another caller may already
///         have renewed, and that caller takes what it finds instead of starting a second exchange. With a
///         credential the vendor rotates, two exchanges lose it permanently.
///     </para>
/// </remarks>
public static class ExampleCredentialRenewal
{
    /// <summary>The field the access token is stored under.</summary>
    public const string AccessTokenField = "accessToken";

    /// <summary>The field the renewal token is stored under.</summary>
    public const string RefreshTokenField = "refreshToken";

    /// <summary>
    ///     How close to its expiry a credential is renewed rather than used. A call made now outlives the moment
    ///     it was made, so a credential expiring inside the margin is renewed before the call rather than during
    ///     it.
    /// </summary>
    public static TimeSpan RenewalMargin => TimeSpan.FromSeconds(120);

    /// <summary>Returns a usable access token, renewing the stored credential when it is close to expiring.</summary>
    /// <param name="context">What the host allows this family to do against this connection.</param>
    /// <param name="exchange">
    ///     The vendor exchange, which takes the renewal token it was stored with and answers with the credential
    ///     to store. Called at most once per expiry across every caller on this connection.
    /// </param>
    /// <param name="now">The instant the expiry is judged against.</param>
    /// <param name="ct">Cancels the renewal.</param>
    public static async Task<string> CurrentAsync(
        IProviderConnectionContext context,
        Func<string, CancellationToken, Task<ExampleRenewedCredential>> exchange,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exchange);

        var stored = await context.Credentials.ReadAsync(ct);
        if (stored.IsUsableAt(now, RenewalMargin))
        {
            return stored.Fields[AccessTokenField];
        }

        await using var session = await context.Credentials.OpenAsync(ct);

        // Read again under the lock. A caller that waited for the row while another renewed finds the renewed
        // credential here and has nothing left to do.
        var underLock = await session.ReadAsync(ct);
        if (underLock.IsUsableAt(now, RenewalMargin))
        {
            return underLock.Fields[AccessTokenField];
        }

        if (!underLock.Fields.TryGetValue(RefreshTokenField, out var renewalToken))
        {
            await context.Health.ReportAsync(
                Enums.AiCredentialHealth.NeedsReauthorization,
                "The stored credential carries nothing to renew it with.",
                ct);

            throw new InvalidOperationException("The connection has no renewal token stored.");
        }

        var renewed = await exchange(renewalToken, ct);

        await session.WriteAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AccessTokenField] = renewed.AccessToken,
                [RefreshTokenField] = renewed.RefreshToken,
            },
            renewed.ExpiresAt,
            ct);
        await session.CompleteAsync(ct);

        return renewed.AccessToken;
    }
}

/// <summary>What a vendor answered a renewal with.</summary>
/// <param name="AccessToken">The token to present on the next calls.</param>
/// <param name="RefreshToken">The token the next renewal presents. Rotated by the vendor on each renewal.</param>
/// <param name="ExpiresAt">When the new access token stops being usable.</param>
public sealed record ExampleRenewedCredential(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt)
{
    /// <summary>
    ///     Renders the credential without either token. The generated version prints every property, which puts
    ///     both into the first log line or exception message that mentions the type.
    /// </summary>
    public override string ToString()
    {
        return $"{nameof(ExampleRenewedCredential)} {{ "
               + $"{nameof(ExampleRenewedCredential.AccessToken)} = {SecretSafeRendering.Elide(this.AccessToken)}, "
               + $"{nameof(ExampleRenewedCredential.RefreshToken)} = {SecretSafeRendering.Elide(this.RefreshToken)}, "
               + $"{nameof(ExampleRenewedCredential.ExpiresAt)} = {this.ExpiresAt:O} }}";
    }
}
