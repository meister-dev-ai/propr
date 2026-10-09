// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using System.Reflection.Emit;
using ArchUnitNET.xUnit;
using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Infrastructure.Features.ProCursor.Remote;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;

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
    public void SharedContracts_DoNotConcealConcreteProviderApplicationOwnership()
    {
        var roots = typeof(ProCursorKnowledgeSourceDto).Assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith("MeisterDev.ProPR.Application", StringComparison.Ordinal) == true)
            .Append(typeof(CanonicalSourceReferenceDto));
        Assert.All(roots, type => Assert.True(IsSharedContractOwnershipValid(type), type.FullName));
    }

    [Theory]
    [InlineData("MeisterDev.ProPR.Application.DTOs.AzureDevOps.NeutralEnvelope", false)]
    [InlineData("MeisterDev.ProPR.Application.DTOs.ProCursor.GitHubEnvelope", false)]
    [InlineData("MeisterDev.ProPR.Application.DTOs.ProCursor.NeutralEnvelope", true)]
    public void OwnershipGuardRejectsConcealedNativeNamespaceTypeOrMember(string typeName, bool nativeMember)
    {
        Assert.False(IsSharedContractOwnershipValid(SharedContractProbe(typeName, nativeMember)));
    }

    [Fact]
    public void OwnershipGuardPreservesNeutralSharedApplicationNamespaces()
    {
        Assert.True(IsSharedContractOwnershipValid(SharedContractProbe("MeisterDev.ProPR.Application.DTOs.ProCursor.NeutralEnvelope", false)));
    }

    private static bool IsSharedContractOwnershipValid(Type type) =>
        type.Assembly.GetName().Name == "MeisterDev.ProPR.ProCursor.Contracts" &&
        AzureDevOpsApplicationBoundaryTests.FindSignatureViolations([type]).Length == 0;

    private static Type SharedContractProbe(string typeName, bool nativeMember)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("MeisterDev.ProPR.ProCursor.Contracts"), AssemblyBuilderAccess.RunAndCollect);
        var builder = assembly.DefineDynamicModule("Contracts").DefineType(typeName, TypeAttributes.Public);
        if (nativeMember)
        {
            builder.DefineField("GitHubAppId", typeof(long), FieldAttributes.Public);
        }

        return builder.CreateType()!;
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
