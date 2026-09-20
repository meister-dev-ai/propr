// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using System.Runtime.Loader;
using MeisterDev.Ai.Providers.AddIns;
using MeisterDev.Ai.Providers.Drivers;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Tests.AddIns;

/// <summary>
///     What an add-in's own load context resolves from where. Two properties have to hold at once and they pull
///     in opposite directions: an add-in's own dependencies come from its folder, so two families can carry
///     different versions of one package, while the assemblies whose types cross the boundary come from the
///     host, so the family's driver is the host's own interface and not a copy of it.
/// </summary>
public sealed class ProviderAddInLoadContextTests
{
    [Fact]
    public void TheContractResolvesFromTheHostAndNotFromTheAddInsFolder()
    {
        using var directory = StagedAddInDirectory.Create();
        var assemblyPath = directory.Install(StagedAddInDirectory.Example);
        var context = new ProviderAddInLoadContext(assemblyPath);

        var contract = context.LoadFromAssemblyName(new AssemblyName("MeisterDev.Ai.Providers.Abstractions"));

        Assert.Same(typeof(IAiProviderDriver).Assembly, contract);
    }

    [Fact]
    public void TheModelAbstractionsResolveFromTheHostAndNotFromTheAddInsFolder()
    {
        using var directory = StagedAddInDirectory.Create();
        var assemblyPath = directory.Install(StagedAddInDirectory.Example);
        var context = new ProviderAddInLoadContext(assemblyPath);

        var abstractions = context.LoadFromAssemblyName(new AssemblyName("Microsoft.Extensions.AI.Abstractions"));

        Assert.Same(typeof(IChatClient).Assembly, abstractions);
    }

    // The reason the two above are asserted by instance rather than by name: a second copy has the same name,
    // the same types and the same members, and the driver it produces is not assignable to the host's interface.
    [Fact]
    public void ADriverLoadedFromAFolderIsTheHostsOwnDriverInterface()
    {
        using var directory = StagedAddInDirectory.Create();
        var assemblyPath = directory.Install(StagedAddInDirectory.Example);
        var context = new ProviderAddInLoadContext(assemblyPath);

        var driverType = context
            .LoadFromAssemblyPath(assemblyPath)
            .GetExportedTypes()
            .Single(type => type is { IsClass: true, IsAbstract: false }
                            && typeof(IAiProviderDriver).IsAssignableFrom(type));

        Assert.Same(
            typeof(IAiProviderDriver),
            driverType.GetInterfaces().Single(contract => contract.Name == nameof(IAiProviderDriver)));
    }

    // The same add-in, loaded twice from two folders, is two assemblies. That is what keeps two families
    // carrying different versions of one package from having to agree on a version.
    [Fact]
    public void TwoAddInFoldersLoadIntoTwoContextsAndTwoAssemblies()
    {
        using var directory = StagedAddInDirectory.Create();
        var first = directory.Install(StagedAddInDirectory.Example, "first");
        var second = directory.Install(StagedAddInDirectory.Example, "second");

        var firstContext = new ProviderAddInLoadContext(first);
        var secondContext = new ProviderAddInLoadContext(second);
        var firstAssembly = firstContext.LoadFromAssemblyPath(first);
        var secondAssembly = secondContext.LoadFromAssemblyPath(second);

        Assert.NotSame(firstContext, secondContext);
        Assert.NotSame(firstAssembly, secondAssembly);
        Assert.Same(firstContext, AssemblyLoadContext.GetLoadContext(firstAssembly));
        Assert.Same(secondContext, AssemblyLoadContext.GetLoadContext(secondAssembly));
    }

    [Fact]
    public void AnAssemblyBesideTheAddInResolvesFromTheAddInsFolder()
    {
        using var directory = StagedAddInDirectory.Create();
        var assemblyPath = directory.Install(StagedAddInDirectory.Example);
        var besideIt = directory.PutFile(
            StagedAddInDirectory.Example,
            "MeisterDev.Ai.Providers.dll");
        var context = new ProviderAddInLoadContext(assemblyPath);

        var resolved = context.LoadFromAssemblyName(new AssemblyName("MeisterDev.Ai.Providers"));

        Assert.Equal(besideIt, resolved.Location);
        Assert.Same(context, AssemblyLoadContext.GetLoadContext(resolved));
        Assert.NotSame(typeof(AiProviderRegistry).Assembly, resolved);
    }

    // An add-in that references a package the host already carries, and does not ship a copy of it, gets the
    // host's. Without that every add-in would have to ship every dependency it has.
    [Fact]
    public void AnAssemblyTheAddInDoesNotCarryFallsBackToTheHost()
    {
        using var directory = StagedAddInDirectory.Create();
        var assemblyPath = directory.Install(StagedAddInDirectory.Example);
        var context = new ProviderAddInLoadContext(assemblyPath);

        var resolved = context.LoadFromAssemblyName(new AssemblyName("MeisterDev.Ai.Providers"));

        Assert.Same(typeof(AiProviderRegistry).Assembly, resolved);
    }
}
