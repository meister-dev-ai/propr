// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     One run of an operator-started add-in action against one connection.
/// </summary>
/// <remarks>
///     A record rather than a held request, because an action's work outlives the call that started it: an
///     operator signing in at a vendor takes minutes, and the result arrives on something other than the request
///     that began it. The add-in writes the terminal state here when its own work finishes.
/// </remarks>
public sealed class ProviderActionInvocationRecord
{
    public Guid Id { get; set; }

    /// <summary>
    ///     The connection the run acted on, or null once that connection has been deleted.
    /// </summary>
    /// <remarks>
    ///     Cleared rather than taken with the connection, so what an administrator started against it stays
    ///     readable. <see cref="ConnectionDisplayName" /> is what names it after that.
    /// </remarks>
    public Guid? ConnectionProfileId { get; set; }

    /// <summary>The connection's name as it stood when the run was opened.</summary>
    /// <remarks>
    ///     Recorded on the row rather than read through the connection, because the row outlives it. A run whose
    ///     connection is gone would otherwise name nothing an operator could recognise.
    /// </remarks>
    public string ConnectionDisplayName { get; set; } = string.Empty;

    /// <summary>The add-in whose action this is.</summary>
    public string AddInKey { get; set; } = string.Empty;

    /// <summary>The action, as the add-in declared it.</summary>
    public string ActionId { get; set; } = string.Empty;

    /// <summary>
    ///     The administrator who started the invocation. The grant a completed action produces is recorded
    ///     against this administrator and never against whoever completed it.
    /// </summary>
    public Guid? InitiatingAdminId { get; set; }

    /// <summary>Pending until the add-in reports, or until the window closes.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>What the add-in said when it finished, capped and scrubbed by the host before it was stored.</summary>
    public string? TerminalMessage { get; set; }

    /// <summary>
    ///     What the invocation is waiting for, composed by the host when the invocation was opened.
    /// </summary>
    /// <remarks>
    ///     Written at the start rather than at the end because the sweep that expires a window is one statement
    ///     over every row it finds and has nothing per-row to say. It becomes the terminal message of an
    ///     invocation that expires, so an operator whose browser never reached the add-in's listener reads what
    ///     the run was waiting for instead of only that it ran out of time.
    /// </remarks>
    public string? WaitingFor { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The end of the bounded window. An invocation still pending past it is expired, not left open.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the invocation reached its terminal state.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
}
