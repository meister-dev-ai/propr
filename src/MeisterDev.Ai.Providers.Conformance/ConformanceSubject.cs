// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;

namespace MeisterDev.Ai.Providers.Conformance;

/// <summary>
///     One provider family as the checks see it: the driver, and whatever else a particular caller is able to
///     supply.
/// </summary>
/// <remarks>
///     <para>
///         The driver is all a caller has to supply. Everything else has a default the checks derive from the
///         family's declaration, and that lets the host construct this for a family it has never seen while
///         it is starting.
///     </para>
///     <para>
///         The runnable action is the exception, and it is the difference between what an author can check and
///         what a host can. A family's actions reach the driver contract as declarations rather than as something
///         callable, so a host that loaded an assembly cannot obtain one and the check that needs it reports
///         itself as not applicable. An author running the kit in their own build has it in hand and supplies it,
///         and that check then runs.
///     </para>
/// </remarks>
public sealed record ConformanceSubject
{
    /// <summary>Describes one family to the checks.</summary>
    /// <param name="driver">The family's driver.</param>
    public ConformanceSubject(IAiProviderDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);

        this.Driver = driver;
    }

    /// <summary>The family's driver.</summary>
    public IAiProviderDriver Driver { get; }

    /// <summary>
    ///     The inputs the checks cannot derive from the driver, or null to read them from the family's own
    ///     declaration.
    /// </summary>
    /// <remarks>
    ///     A host reads the declaration, because a family built elsewhere has stated them nowhere else. A caller
    ///     that holds the values independently — this repository's own suite states them beside the checks for
    ///     the families it builds — supplies them here, so the driver is measured against a value it did not
    ///     supply.
    /// </remarks>
    public ProviderConformanceInputs? Inputs { get; init; }

    /// <summary>How the family is named in the report: its identity key, or the driver's type when it has none.</summary>
    public string Name
    {
        get
        {
            try
            {
                var key = this.Driver.Declaration?.Key;
                return string.IsNullOrWhiteSpace(key) ? this.Driver.GetType().Name : key;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Reading the declaration runs the family's code. A family that throws here still has to be
                // named in its own report, or the report says nothing about which family produced it.
                return this.Driver.GetType().Name;
            }
        }
    }

    /// <summary>The inputs the checks run against: the ones supplied, or the family's own declared ones.</summary>
    /// <exception cref="InvalidOperationException">The family declares none and none were supplied.</exception>
    internal ProviderConformanceInputs ResolvedInputs =>
        this.Inputs
        ?? this.Driver.Declaration?.ConformanceInputs
        ?? throw new InvalidOperationException(
            "The family declares no conformance inputs. The checks read the authentication mode to present and "
            + "the usage payload to replay from there, and a family that states neither cannot be measured.");
}
