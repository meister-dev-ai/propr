// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using MeisterDev.Ai.Providers.Conformance;
using MeisterDev.Ai.Providers.Drivers;

namespace MeisterDev.Ai.Providers.Tests.Conformance;

/// <summary>
///     Guards what makes the conformance kit usable where it has to run: in a host while it starts, and in the
///     build of a family written outside this repository.
/// </summary>
/// <remarks>
///     Both would be lost to one convenient using directive. A reference to the shared runtime would take the
///     decorator stages, the guarded egress handler and six vendor SDKs into an add-in's build, and a reference
///     to a test framework would mean a host hosting a test runner in order to check the families it loaded.
/// </remarks>
public sealed class ConformanceKitIsolationTests
{
    private static Assembly KitAssembly => typeof(DriverConformance).Assembly;

    [Fact]
    public void TheKitIsItsOwnAssembly()
    {
        Assert.Equal("MeisterDev.Ai.Providers.Conformance", KitAssembly.GetName().Name);
    }

    // Over the whole closure, not the direct references alone: a family takes everything the kit drags in, so
    // something reached through the one package the kit does take would arrive with it while a check of the
    // direct list said nothing.
    [Fact]
    public void TheKitReachesNeitherTheSharedRuntimeNorAnyHostProject()
    {
        var forbidden = ReachableFrom(KitAssembly)
            .Where(name => name.StartsWith("MeisterDev.ProPR", StringComparison.Ordinal)
                           || string.Equals(name, "MeisterDev.Ai.Providers", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(forbidden);
    }

    [Fact]
    public void TheKitTakesNoTestFramework()
    {
        var frameworks = ReachableFrom(KitAssembly)
            .Where(name => name.Contains("xunit", StringComparison.OrdinalIgnoreCase)
                           || name.Contains("nunit", StringComparison.OrdinalIgnoreCase)
                           || name.Contains("NSubstitute", StringComparison.OrdinalIgnoreCase)
                           || name.StartsWith("Microsoft.VisualStudio.TestPlatform", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(frameworks);
    }

    // The same rule the contract and the shared runtime keep, so a public type of the kit's cannot appear under
    // a namespace an add-in author would not look in.
    [Fact]
    public void TheKitExposesEveryPublicTypeUnderTheProviderNamespace()
    {
        var strays = KitAssembly
            .GetExportedTypes()
            .Where(type => !(type.Namespace ?? string.Empty).StartsWith("MeisterDev.Ai.Providers", StringComparison.Ordinal))
            .Select(type => type.FullName ?? type.Name)
            .ToList();

        Assert.Empty(strays);
    }

    // The kit measures a driver, so the driver contract has to be the host's own and not a second copy. A copy
    // would make every check run against an interface no host driver implements.
    [Fact]
    public void TheKitReadsTheDriverContractFromTheContractAssembly()
    {
        Assert.Same(
            typeof(IAiProviderDriver).Assembly, typeof(ConformanceSubject).Assembly.GetReferencedAssemblies()
                .Select(reference => Assembly.Load(reference))
                .Single(assembly => assembly.GetName().Name == "MeisterDev.Ai.Providers.Abstractions"));
    }

    private static IEnumerable<string> ReachableFrom(Assembly root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>([root]);

        while (queue.Count > 0)
        {
            foreach (var reference in queue.Dequeue().GetReferencedAssemblies())
            {
                if (reference.Name is not { } name || !seen.Add(name))
                {
                    continue;
                }

                yield return name;

                Assembly loaded;
                try
                {
                    loaded = Assembly.Load(reference);
                }
                catch (Exception exception) when (exception is FileNotFoundException or BadImageFormatException)
                {
                    continue;
                }

                queue.Enqueue(loaded);
            }
        }
    }
}
