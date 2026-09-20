// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Hosting;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     Caps one string a provider family produced and takes the connection's credential out of it.
/// </summary>
/// <remarks>
///     Shared by the dispatcher and by the reporter a family closes its own invocation through, so a message
///     reaching storage is scrubbed the same way whichever route it arrived on.
/// </remarks>
internal static class ProviderReportedMessage
{
    /// <summary>What a message that could not be scrubbed is replaced with.</summary>
    internal const string Withheld =
        "The provider family's message was withheld: the host could not read this connection's credential, so it "
        + "could not take the credential out of the message.";

    /// <summary>Caps and scrubs <paramref name="message" />, or replaces it when it cannot be scrubbed.</summary>
    /// <remarks>
    ///     <para>
    ///         The credential is read when there is a message rather than held, because it can have changed since
    ///         the action started: a family that reports after renewing one would otherwise be scrubbed against
    ///         the value it replaced.
    ///     </para>
    ///     <para>
    ///         Reading it can fail outright — a connection repointed to another family leaves a payload this one
    ///         cannot decode. The guard elides the values it is handed and nothing else, so scrubbing with none
    ///         is a length cap over text the family wrote, and several providers quote the presented key back in
    ///         a refusal. The family's words are dropped in that case and a fixed sentence takes their place,
    ///         because this message is stored and shown. The caller still closes its run in the state the family
    ///         reported, so a failure here does not leave a run Pending until its window lapses.
    ///     </para>
    /// </remarks>
    /// <param name="message">The message as the family produced it.</param>
    /// <param name="secrets">The credential values to scrub against, read on demand.</param>
    internal static async Task<string> ScrubbedAsync(
        string? message,
        Func<CancellationToken, Task<IReadOnlyCollection<string>>> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        try
        {
            return ProviderMessageGuard.Sanitize(message, await secrets(CancellationToken.None).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Withheld;
        }
    }
}
