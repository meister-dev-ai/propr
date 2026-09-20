// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Conformance;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;

namespace MeisterDev.Ai.Providers.LiteLlmAddIn.Tests;

/// <summary>
///     The shared driver checks, run against this family in its own build, and the reference rules that let it
///     be loaded from a directory at all.
/// </summary>
/// <remarks>
///     This is what an author outside this repository runs: the family, the contract and the kit, with no host
///     to load it. A host runs the same checks against it while starting and skips it if one fails, so a failure
///     found here is one an operator would otherwise meet as an absent family.
/// </remarks>
public sealed class LiteLlmConformanceTests
{
    /// <summary>
    ///     The inputs the checks measure the family against, stated here rather than read off the declaration.
    /// </summary>
    /// <remarks>
    ///     A family that changed its rule and its own declaration together would still pass if the checks read
    ///     the declaration. A gateway is reached wherever an operator deployed it, so an address belonging to no
    ///     vendor is what this family has to take and there is nothing for it to refuse on account of the host.
    /// </remarks>
    private static readonly ProviderConformanceInputs Stated = new(
        LiteLlmProviderDriver.ApiKeyAuth,
        RecordedUsagePayload:
        "{\"inputTokenCount\":4120,\"outputTokenCount\":207,\"totalTokenCount\":4327,"
        + "\"cachedInputTokenCount\":4000,\"reasoningTokenCount\":200,"
        + "\"additionalCounts\":{\"InputTokenDetails.AudioTokenCount\":0,\"OutputTokenDetails.AudioTokenCount\":0}}");

    public static TheoryData<DriverConformanceCheck> Checks()
    {
        return [.. DriverConformance.Checks];
    }

    // One case per check, so a failure names the check and a check that stops running is visible as a case that
    // disappeared.
    [Theory]
    [MemberData(nameof(Checks))]
    public void TheFamilyPassesEveryCheckTheKitCanRunAgainstIt(DriverConformanceCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);

        var result = check.Run(Subject());

        Assert.False(result.IsFailure, $"'{check.Name}' fails: {result.Detail}");
    }

    // The kit reports one family in one report, which a host records beside a family it skipped. A
    // report that passed but named no checks would satisfy the case above and mean nothing.
    [Fact]
    public void TheKitReportsEveryCheckItRan()
    {
        var report = DriverConformance.Run(Subject());

        Assert.True(report.Passed, report.Summary);
        Assert.Equal("meisterdev/liteLlm", report.Family);
        Assert.Equal(DriverConformance.Checks.Count, report.Results.Count);
    }

    // A host loading this family has only the declaration to read the inputs from, so the declaration has to
    // state what this suite states. Without this the checks above would quietly stop measuring the family
    // against anything it did not supply itself.
    [Fact]
    public void TheDeclarationStatesTheInputsThisSuiteStatesForIt()
    {
        var declared = new LiteLlmProviderDriver().Declaration.ConformanceInputs;

        Assert.Equal(Stated.CredentialAuthMode, declared.CredentialAuthMode);
        Assert.Equal(Stated.RecordedUsagePayload, declared.RecordedUsagePayload);
    }

    // The loader constructs a driver itself, so a family whose only constructor takes arguments exposes nothing
    // it can build and is skipped with the type named.
    [Fact]
    public void TheDriverIsConstructedWithNoArguments()
    {
        var constructed = typeof(LiteLlmProviderDriver).GetConstructor(Type.EmptyTypes);

        Assert.NotNull(constructed);
        Assert.IsType<LiteLlmProviderDriver>(constructed.Invoke([]));
    }

    // One driver per assembly: the inventory reports one file, one identity and one content hash, and the loader
    // skips an assembly exposing more than one.
    [Fact]
    public void TheAssemblyExposesOneDriver()
    {
        var drivers = typeof(LiteLlmProviderDriver).Assembly
            .GetExportedTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IAiProviderDriver).IsAssignableFrom(type))
            .ToList();

        Assert.Equal([typeof(LiteLlmProviderDriver)], drivers);
    }

    // The point of the move: this family compiles against the contract and its own vendor dependency, and
    // reaches neither the shared provider runtime nor the product that hosts it. A reference to either would be
    // one using directive away and would make the family unbuildable outside this repository.
    [Fact]
    public void TheAddInReferencesTheContractAndNothingElseFromThisRepository()
    {
        var referenced = typeof(LiteLlmProviderDriver).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("MeisterDev.", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(["MeisterDev.Ai.Providers.Abstractions"], referenced);
    }

    // The family's driver implements the host's interface rather than a copy of it. An add-in that shipped its
    // own contract would load, pass the version check and then be absent from the registry with nothing
    // reporting a failure.
    [Fact]
    public void TheDriverImplementsTheContractsOwnInterface()
    {
        Assert.Same(
            typeof(IAiProviderDriver).Assembly,
            typeof(LiteLlmProviderDriver).GetInterfaces()
                .Single(contract => contract.FullName == typeof(IAiProviderDriver).FullName)
                .Assembly);
    }

    // The add-in's own build output, staged beside these tests, which is the folder a deployment holds. Pointing
    // the packaging check at this project's output instead would read a layout no deployment has: a test project
    // needs the shared assemblies in its own folder in order to run at all.
    private static ConformanceSubject Subject()
    {
        return new ConformanceSubject(new LiteLlmProviderDriver())
        {
            Inputs = Stated,
        };
    }
}
