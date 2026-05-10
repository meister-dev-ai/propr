// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace MeisterProPR.Application.Tests.Architecture;

public sealed class ProCursorReferenceBoundaryTests
{
    [Fact]
    public void ApiAssembly_DoesNotDependOnProCursorImplementationAssembly()
    {
        Types().That()
            .ResideInAssembly("MeisterProPR.Api")
            .Should()
            .NotDependOnAny(Types().That().ResideInAssembly("MeisterProPR.ProCursor"))
            .WithoutRequiringPositiveResults()
            .Check(ArchitectureTestContext.Architecture);
    }

    [Fact]
    public void ProCursorAssembly_DoesNotDependOnApplicationOrInfrastructureAssemblies()
    {
        Types().That()
            .ResideInAssembly("MeisterProPR.ProCursor")
            .Should()
            .NotDependOnAny(
                Types().That().ResideInAssembly("MeisterProPR.Application")
                    .Or().ResideInAssembly("MeisterProPR.Infrastructure"))
            .WithoutRequiringPositiveResults()
            .Check(ArchitectureTestContext.Architecture);
    }
}
