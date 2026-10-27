// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Security.Cryptography;
using System.Text.Json;
using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Infrastructure.Auth;

/// <summary>
///     Writes and reads the state of a sign-in through an external identity provider. The state leaves ProPR while the
///     browser visits the provider, so it is protected with the data protection keys of the installation and read back
///     only when the protection is intact.
/// </summary>
public static class ProtectedSignInState
{
    /// <summary>Serializes <paramref name="state" /> to JSON and protects it for <paramref name="purpose" />.</summary>
    /// <param name="codec">The codec that applies the data protection keys.</param>
    /// <param name="state">The state of the sign-in.</param>
    /// <param name="purpose">The purpose the protection is bound to, so that one flow cannot read the state of another.</param>
    public static string Protect<TState>(ISecretProtectionCodec codec, TState state, string purpose)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return codec.Protect(JsonSerializer.Serialize(state), purpose);
    }

    /// <summary>Reads a state written by <see cref="Protect{TState}" /> for the same purpose.</summary>
    /// <param name="codec">The codec that applies the data protection keys.</param>
    /// <param name="protectedState">The protected state as the request carried it.</param>
    /// <param name="purpose">The purpose the state was protected for.</param>
    /// <returns>The state, or <see langword="null" /> when the value is missing, altered or protected for another purpose.</returns>
    public static TState? TryRead<TState>(ISecretProtectionCodec codec, string? protectedState, string purpose)
        where TState : class
    {
        ArgumentNullException.ThrowIfNull(codec);

        // The codec returns a value without its protection envelope unchanged, because secrets stored before
        // encryption was introduced are plaintext. A sign-in state always carries the envelope, so a value without
        // it is refused; otherwise a caller could send JSON of its own choosing as the state.
        if (string.IsNullOrWhiteSpace(protectedState) || !codec.IsProtected(protectedState))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TState>(codec.Unprotect(protectedState, purpose));
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
