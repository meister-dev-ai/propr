// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace MeisterDev.ProPR.Application.Tests.Architecture;

public sealed class ProCursorRuntimeOwnershipBoundaryTests
{
    [Fact]
    public void ProCursorRuntimeTypes_DoNotDependOnApplicationAssembly()
    {
        Types().That()
            .ResideInAssembly("MeisterDev.ProPR.ProCursor")
            .Should()
            .NotDependOnAny(Types().That().ResideInAssembly("MeisterDev.ProPR.Application"))
            .WithoutRequiringPositiveResults()
            .Check(ArchitectureTestContext.Architecture);
    }

    [Fact]
    public void ProCursorRuntimeTypes_DoNotDependOnApiAssembly()
    {
        Types().That()
            .ResideInAssembly("MeisterDev.ProPR.ProCursor")
            .Should()
            .NotDependOnAny(Types().That().ResideInAssembly("MeisterDev.ProPR.Api"))
            .WithoutRequiringPositiveResults()
            .Check(ArchitectureTestContext.Architecture);
    }

    [Fact]
    public void ProCursorCoreAndWorkerTypes_UseProCursorOwnedNamespaces()
    {
        Types().That()
            .ResideInAssembly("MeisterDev.ProPR.ProCursor")
            .And().HaveNameMatching(
                ".*(Gateway|Coordinator|QueryService|MiniIndexBuilder|FreshnessEvaluator|RefreshScheduler|IndexWorker|RollupWorker|HealthCheck|Options)$")
            .Should()
            .NotResideInNamespace("MeisterDev.ProPR.Application")
            .AndShould().NotResideInNamespace("MeisterDev.ProPR.Api")
            .WithoutRequiringPositiveResults()
            .Check(ArchitectureTestContext.Architecture);
    }
}
