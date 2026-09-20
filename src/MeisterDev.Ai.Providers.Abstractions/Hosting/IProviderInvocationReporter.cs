// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Hosting;

/// <summary>
///     Writes the terminal state of the invocation the host handed the family, when the family's own work
///     finishes.
/// </summary>
/// <remarks>
///     <para>
///         An action's work outlives the dispatch that started it. An operator signing in at a vendor takes
///         minutes, and the completion arrives on a socket the family opened rather than on the request that
///         started the action, so there is no call left to return an answer on. The family reports instead, and
///         the host's own view polls the invocation.
///     </para>
///     <para>
///         This is why the result vocabulary an action returns stays at four: a family whose work outlives the
///         dispatch reports here rather than through a fifth result meaning "still going".
///     </para>
///     <para>
///         The message is capped and scrubbed by the host before it reaches a surface, like every other string a
///         family returns.
///     </para>
/// </remarks>
public interface IProviderInvocationReporter
{
    /// <summary>Reports that the work finished.</summary>
    /// <param name="message">What to show the operator.</param>
    /// <param name="ct">Cancels the report.</param>
    Task ReportCompletedAsync(string message, CancellationToken ct = default);

    /// <summary>Reports that the work failed.</summary>
    /// <param name="message">What went wrong, in terms an operator can act on.</param>
    /// <param name="ct">Cancels the report.</param>
    Task ReportFailedAsync(string message, CancellationToken ct = default);
}
