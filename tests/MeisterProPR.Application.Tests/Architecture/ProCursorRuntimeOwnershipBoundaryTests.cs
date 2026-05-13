// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace MeisterProPR.Application.Tests.Architecture;

public sealed class ProCursorRuntimeOwnershipBoundaryTests
{
    [Fact]
    public void ProCursorRuntimeTypes_DoNotDependOnApplicationAssembly()
    {
        Types().That()
            .ResideInAssembly("MeisterProPR.ProCursor")
            .Should()
            .NotDependOnAny(Types().That().ResideInAssembly("MeisterProPR.Application"))
            .WithoutRequiringPositiveResults()
            .Check(ArchitectureTestContext.Architecture);
    }

    [Fact]
    public void ProCursorRuntimeTypes_DoNotDependOnApiAssembly()
    {
        Types().That()
            .ResideInAssembly("MeisterProPR.ProCursor")
            .Should()
            .NotDependOnAny(Types().That().ResideInAssembly("MeisterProPR.Api"))
            .WithoutRequiringPositiveResults()
            .Check(ArchitectureTestContext.Architecture);
    }

    [Fact]
    public void ProCursorCoreAndWorkerTypes_UseProCursorOwnedNamespaces()
    {
        Types().That()
            .ResideInAssembly("MeisterProPR.ProCursor")
            .And().HaveNameMatching(
                ".*(Gateway|Coordinator|QueryService|MiniIndexBuilder|FreshnessEvaluator|RefreshScheduler|IndexWorker|RollupWorker|HealthCheck|Options)$")
            .Should()
            .NotResideInNamespace("MeisterProPR.Application")
            .AndShould().NotResideInNamespace("MeisterProPR.Api")
            .WithoutRequiringPositiveResults()
            .Check(ArchitectureTestContext.Architecture);
    }
}
