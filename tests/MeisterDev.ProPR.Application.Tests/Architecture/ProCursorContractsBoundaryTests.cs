// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using ArchUnitNET.xUnit;
using MeisterDev.ProPR.Application.DTOs.AzureDevOps;
using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Infrastructure.Features.ProCursor.Remote;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace MeisterDev.ProPR.Application.Tests.Architecture;

public sealed class ProCursorContractsBoundaryTests
{
    [Fact]
    public void SharedWireContracts_CompileFromDedicatedContractsAssembly()
    {
        Assert.Equal("MeisterDev.ProPR.ProCursor.Contracts", typeof(ProCursorKnowledgeSourceDto).Assembly.GetName().Name);
        Assert.Equal("MeisterDev.ProPR.ProCursor.Contracts", typeof(CanonicalSourceReferenceDto).Assembly.GetName().Name);
        Assert.Equal("MeisterDev.ProPR.ProCursor.Contracts", typeof(ProCursorSharedKeyAuthenticationDefaults).Assembly.GetName().Name);
    }

    [Fact]
    public void ContractsAssembly_DoesNotDependOnApplicationImplementationAssembly()
    {
        Types().That()
            .ResideInAssembly("MeisterDev.ProPR.ProCursor.Contracts")
            .Should()
            .NotDependOnAny(Types().That().ResideInAssembly("MeisterDev.ProPR.Application"))
            .WithoutRequiringPositiveResults()
            .Check(ArchitectureTestContext.Architecture);
    }
}
