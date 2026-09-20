// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Conformance;
using MeisterDev.Ai.Providers.ExampleAddIn;

namespace MeisterDev.Ai.Providers.Tests.Conformance;

/// <summary>
///     The shared conformance kit, run against a family this repository has only a declaration for.
/// </summary>
/// <remarks>
///     <para>
///         The checks themselves are in <c>MeisterDev.Ai.Providers.Conformance</c>, because a host runs them
///         while it starts and an add-in author runs them in their own build, and neither hosts a test runner.
///         What is here is the route a family built outside this repository takes: no entry in a list beside the
///         checks, so every input comes off the declaration, and a kit that could only be constructed from such
///         an entry would be no use to that family.
///     </para>
///     <para>
///         No family this product ships is measured here any more, because none of them is compiled in. Each
///         states its inputs and runs these checks in its own test project, which an author outside this
///         repository does, and the composed host runs them again over every family it loads from a directory.
///     </para>
/// </remarks>
public sealed class DriverConformanceTests
{
    /// <summary>Every check the kit holds, one case each.</summary>
    /// <remarks>
    ///     One case per check, so a failure names the check and a check that stops running is visible as a case
    ///     that disappeared.
    /// </remarks>
    public static TheoryData<DriverConformanceCheck> Checks()
    {
        return [.. DriverConformance.Checks];
    }

    [Theory]
    [MemberData(nameof(Checks))]
    public void AFamilyMeasuredAgainstItsOwnDeclarationPassesEveryCheck(DriverConformanceCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);

        var result = check.Run(new ConformanceSubject(new ExampleProviderDriver()));

        Assert.False(result.IsFailure, $"'{check.Name}' fails: {result.Detail}");
    }

    // The kit reports one family in one report, which a host records beside a family it skipped and what
    // an add-in author's build prints. A report that passed but named no checks would satisfy the case above and
    // mean nothing.
    [Fact]
    public void TheKitReportsEveryCheckItRan()
    {
        var report = DriverConformance.Run(new ExampleProviderDriver());

        Assert.True(report.Passed, report.Summary);
        Assert.Equal("example/provider", report.Family);
        Assert.Equal(DriverConformance.Checks.Count, report.Results.Count);
        Assert.Equal(
            DriverConformance.Checks.Select(check => check.Name),
            report.Results.Select(result => result.Check));
    }
}
