// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;
using MeisterDev.Ai.Providers.Conformance.Checks;
using MeisterDev.Ai.Providers.Drivers;

namespace MeisterDev.Ai.Providers.Conformance;

/// <summary>
///     The checks every provider family has to pass, and the way to run them.
/// </summary>
/// <remarks>
///     <para>
///         Each family has a test class of its own, and that is where its own rules belong: which hosts it
///         accepts, which authentication modes it reads, what its endpoint has to name. What is here is the one
///         property such a class structurally cannot assert, that the families behave the <em>same</em> at the
///         seam. A check that knows only its own family cannot notice two families drifting apart, and the
///         review loop calls all of them through one seam with no way to special-case one.
///     </para>
///     <para>
///         A family that cannot pass these is not shippable. An author references this assembly and runs
///         <see cref="Run(ConformanceSubject)" /> in their own build; a host runs the same checks against every
///         family it loaded while it starts, and skips one that fails.
///     </para>
/// </remarks>
public static class DriverConformance
{
    /// <summary>The checks, in the order they run.</summary>
    /// <remarks>
    ///     Ordered so a family that is wrong about something basic is reported against the basic check rather
    ///     than against every case built on top of it: what the family says it is, then what it declares, then
    ///     the one property that is about what its code does with a payload.
    /// </remarks>
    public static IReadOnlyList<DriverConformanceCheck> Checks { get; } = ImmutableArray.Create<DriverConformanceCheck>(
        new FamilyIdentityCheck(),
        new ConformanceInputsCheck(),
        new DeclaredVocabularyCheck(),
        new ProtocolShapeCheck(),
        new CredentialShapeCheck(),
        new CredentialFieldCheck(),
        new CredentialFieldScopeCheck(),
        new DeclaredFieldReferenceCheck(),
        new ConformanceCredentialShapeCheck(),
        new UsageArithmeticCheck());

    /// <summary>Runs every check against one family.</summary>
    /// <param name="subject">The family to measure.</param>
    /// <remarks>
    ///     Never throws on account of the family. Each check contains whatever the family does and reports it as
    ///     that check's failure, and every check runs whatever the ones before it concluded, so one report names
    ///     everything wrong with a family rather than the first thing.
    /// </remarks>
    public static ConformanceReport Run(ConformanceSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        return new ConformanceReport(subject.Name, Checks.Select(check => check.Run(subject)));
    }

    /// <summary>Runs every check against one driver, reading the inputs from the family's own declaration.</summary>
    /// <param name="driver">The family's driver.</param>
    /// <remarks>
    ///     What a host calls for a family it loaded from a directory: the declaration is where such a family
    ///     states what the checks need, because there is nowhere else it could have stated it.
    /// </remarks>
    public static ConformanceReport Run(IAiProviderDriver driver)
    {
        return Run(new ConformanceSubject(driver));
    }
}
