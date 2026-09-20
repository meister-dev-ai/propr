// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Declaration;

/// <summary>
///     What an add-in states about itself in a form the host can read without running it.
/// </summary>
/// <remarks>
///     <para>
///         A driver's <see cref="ProviderDeclaration" /> is the full statement, and reading it means
///         constructing the driver, which is running the add-in's code. An administrator decides whether to
///         activate an add-in before any of it has run, so the facts that decision rests on have to be readable
///         from the file: the identity a connection would be stored against, the version, the contract it was
///         built for, the hosts it says it will contact, and the capability it needs.
///     </para>
///     <para>
///         Attribute arguments are metadata. The host reads them through a metadata-only load, which maps the
///         file for reflection and executes no code in it, so an add-in nobody has activated never runs.
///     </para>
///     <para>
///         What this states is checked against the driver's declaration when the add-in is activated. The two
///         disagreeing is refused, because the administrator activated what this said.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class ProviderAddInAttribute : Attribute
{
    /// <summary>The identity key every connection of this family is stored against.</summary>
    /// <remarks>A vendor prefix, one <c>/</c>, and an internal name: <c>acme/gateway</c>.</remarks>
    public string Key { get; init; } = string.Empty;

    /// <summary>What an operator sees where the family is named.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>The family's own version, as its author numbers it.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>The contract version this add-in was built against.</summary>
    /// <remarks>
    ///     Read before the assembly is loaded, so an add-in built for another contract is reported as such
    ///     instead of loading and failing on a member that is not there.
    /// </remarks>
    public string ContractVersion { get; init; } = string.Empty;

    /// <summary>Every host this family may contact, in the form the declaration states them.</summary>
    /// <remarks>
    ///     A leading dot covers a domain and everything under it. An administrator decides on this list, so it
    ///     is stated here as well as in the declaration, and activation refuses a driver that declares a host
    ///     absent from it. A driver declaring fewer is inside the decision, so a family whose endpoints come
    ///     from local configuration states its full set here once.
    /// </remarks>
    public string[] ReachedHosts { get; init; } = [];

    /// <summary>The premium capability this family needs, or null when it needs none.</summary>
    public string? RequiredCapability { get; init; }
}
