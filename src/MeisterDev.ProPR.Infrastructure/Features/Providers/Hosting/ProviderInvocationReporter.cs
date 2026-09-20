// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Hosting;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     Writes the terminal state of the one invocation a provider family was handed.
/// </summary>
/// <remarks>
///     The invocation is fixed when this is built, so a family reports against the run the host gave it and
///     cannot name another. The message is capped and scrubbed before it is stored, like every other string a
///     family produces.
/// </remarks>
/// <param name="invocationId">The invocation this reporter writes against.</param>
/// <param name="invocations">Where the terminal state is written.</param>
/// <param name="secrets">
///     The credential values held for the connection, which the message is scrubbed against before it is stored.
/// </param>
public sealed class ProviderInvocationReporter(
    Guid invocationId,
    ProviderActionInvocations invocations,
    Func<CancellationToken, Task<IReadOnlyCollection<string>>> secrets) : IProviderInvocationReporter
{
    /// <inheritdoc />
    public Task ReportCompletedAsync(string message, CancellationToken ct = default)
    {
        return this.CloseAsync(ProviderInvocationState.Completed, message, ct);
    }

    /// <inheritdoc />
    public Task ReportFailedAsync(string message, CancellationToken ct = default)
    {
        return this.CloseAsync(ProviderInvocationState.Failed, message, ct);
    }

    private async Task CloseAsync(string state, string message, CancellationToken ct)
    {
        // The credential is read to scrub against, and reading it can fail: a connection repointed to another
        // family leaves a payload this one cannot decode. Throwing here would hand a family an exception out of
        // a host primitive and leave the invocation Pending until its window lapses, so the message is replaced
        // and the run is still closed in the state the family reported.
        var scrubbed = await ProviderReportedMessage.ScrubbedAsync(message, secrets).ConfigureAwait(false);

        // A report against an invocation that already reached a terminal state changes nothing and is not an
        // error: a family that finished after its window closed is reporting something true about work the host
        // has already given up on, and failing the report would leave the family with an exception it can do
        // nothing about.
        await invocations.TryCloseAsync(invocationId, state, scrubbed, ct).ConfigureAwait(false);
    }
}
