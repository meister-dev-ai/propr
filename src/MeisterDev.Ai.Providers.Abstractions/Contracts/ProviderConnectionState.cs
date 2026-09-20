// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     What the host knows about one connection at the moment it projects it, handed to the family so it can say
///     which of its actions that connection is offered.
/// </summary>
/// <remarks>
///     <para>
///         Only facts the host already holds. It is composed once per connection while a list is projected, so
///         every member here is read from values the projection had in hand; a member that needed a further read
///         would turn one list into one query per row.
///     </para>
///     <para>
///         The family is handed the state rather than asked to go and find it, because a family has no way to
///         read a connection outside a call the host makes and no way to read one cheaply at all: its credential
///         handle takes a row lock and its health is resolved from three sources the host arbitrates between.
///     </para>
/// </remarks>
/// <param name="HasStoredCredential">
///     Whether the connection holds a credential this family can read. False both for a connection nothing has
///     been stored against and for one whose stored credential another family wrote.
/// </param>
/// <param name="VerificationStatus">The outcome of the last verification the host ran, or that none has run.</param>
/// <param name="CredentialHealth">
///     The connection's resolved credential health. <see cref="AiCredentialHealth.Unreported" /> both for a
///     family whose declaration states its connections have no credential health and for one where nothing has
///     been observed yet.
/// </param>
public sealed record ProviderConnectionState(
    bool HasStoredCredential,
    AiVerificationStatus VerificationStatus,
    AiCredentialHealth CredentialHealth)
{
    /// <summary>A connection with nothing stored against it and nothing observed about it.</summary>
    public static ProviderConnectionState Unconnected { get; } =
        new(false, AiVerificationStatus.NeverVerified, AiCredentialHealth.Unreported);
}
