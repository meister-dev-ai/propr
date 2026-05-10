// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using ArchUnitNET.xUnit;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace MeisterProPR.Application.Tests.Architecture;

public sealed class ProCursorContractsBoundaryTests
{
    [Fact]
    public void SharedWireContracts_CompileFromDedicatedContractsAssembly()
    {
        Assert.Equal("MeisterProPR.ProCursor.Contracts", typeof(ProCursorKnowledgeSourceDto).Assembly.GetName().Name);
        Assert.Equal("MeisterProPR.ProCursor.Contracts", typeof(CanonicalSourceReferenceDto).Assembly.GetName().Name);
        Assert.Equal("MeisterProPR.ProCursor.Contracts", typeof(ProCursorSharedKeyAuthenticationDefaults).Assembly.GetName().Name);
    }

    [Fact]
    public void ContractsAssembly_DoesNotDependOnApplicationImplementationAssembly()
    {
        Types().That()
            .ResideInAssembly("MeisterProPR.ProCursor.Contracts")
            .Should()
            .NotDependOnAny(Types().That().ResideInAssembly("MeisterProPR.Application"))
            .WithoutRequiringPositiveResults()
            .Check(ArchitectureTestContext.Architecture);
    }
}
