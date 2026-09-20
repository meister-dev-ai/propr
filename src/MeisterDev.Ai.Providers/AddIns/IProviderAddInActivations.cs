// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.AddIns;

/// <summary>
///     Which add-ins a platform administrator has activated.
/// </summary>
/// <remarks>
///     <para>
///         An add-in in the external directory is discovered and not loaded until an administrator activates it.
///         Putting a file in that directory is the filesystem access the deployment grants; deciding that the
///         host should run it is a separate act, and this is where that decision is read from.
///     </para>
///     <para>
///         The decision is bound to the content, not to the path. Replacing the file changes its hash and the
///         new bytes are not activated, so an administrator sees the new version and what it states before it
///         runs. Moving an activated file changes nothing, because the bytes are the same.
///     </para>
///     <para>
///         Families in the directory the deployment image carries need no activation. Their bytes ship with the
///         host, and replacing one means replacing a shipped binary.
///     </para>
/// </remarks>
public interface IProviderAddInActivations
{
    /// <summary>Whether an administrator has activated these exact bytes.</summary>
    /// <param name="contentHash">The SHA-256 of the assembly, or null when it could not be read.</param>
    bool IsActivated(string? contentHash);
}

/// <summary>Activations for a host that gates nothing, which is what a test and an in-process tool have.</summary>
/// <remarks>
///     Everything readable is activated. The host that serves an operator supplies a real store; this exists so
///     the loader has one answer for a caller with no administrator to ask.
/// </remarks>
public sealed class ProviderAddInsAllActivated : IProviderAddInActivations
{
    /// <summary>The one instance, since it holds nothing.</summary>
    public static ProviderAddInsAllActivated Instance { get; } = new();

    /// <inheritdoc />
    public bool IsActivated(string? contentHash)
    {
        return true;
    }
}
