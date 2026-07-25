// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace MeisterDev.ProPR.Application.Tests.Architecture;

public sealed class ProCursorReferenceBoundaryTests
{
    [Fact]
    public void ApiAssembly_DoesNotDependOnProCursorImplementationAssembly()
    {
        Types().That()
            .ResideInAssembly("MeisterDev.ProPR.Api")
            .Should()
            .NotDependOnAny(Types().That().ResideInAssembly("MeisterDev.ProPR.ProCursor"))
            .WithoutRequiringPositiveResults()
            .Check(ArchitectureTestContext.Architecture);
    }

    [Fact]
    public void ProCursorAssembly_DoesNotDependOnApplicationOrInfrastructureAssemblies()
    {
        Types().That()
            .ResideInAssembly("MeisterDev.ProPR.ProCursor")
            .Should()
            .NotDependOnAny(
                Types().That().ResideInAssembly("MeisterDev.ProPR.Application")
                    .Or().ResideInAssembly("MeisterDev.ProPR.Infrastructure"))
            .WithoutRequiringPositiveResults()
            .Check(ArchitectureTestContext.Architecture);
    }
}
