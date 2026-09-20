// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Declaration;

/// <summary>The contract a provider family is built against.</summary>
/// <remarks>
///     There is no compatibility guarantee across versions. A change to the contract is a rebuild of every family
///     built against it, which is affordable while the families are held by one team and is the reason
///     <see cref="ProviderDeclaration.ContractVersion" /> is checked when a family is loaded rather than left to
///     surface as a missing member on the first model call.
/// </remarks>
public static class ProviderContract
{
    /// <summary>The version this build of the contract declares.</summary>
    /// <remarks>
    ///     Raised to 1.1 because the contract lost members a family could call. A family built against 1.0 is
    ///     refused by the version check, which is where that has to surface: a host that took it would fail on
    ///     the missing member instead, and an administrator would read a MethodNotFound where a statement about
    ///     versions belongs.
    /// </remarks>
    public const string Version = "1.1";
}
