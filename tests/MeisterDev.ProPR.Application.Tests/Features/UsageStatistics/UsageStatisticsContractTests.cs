// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using System.Reflection.Emit;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Support;

namespace MeisterDev.ProPR.Application.Tests.Features.UsageStatistics;

/// <summary>
///     Where a build sends its snapshots.
///     <para>
///         The endpoint is fixed when the assembly is compiled rather than read at run time, so an
///         installation cannot be redirected or silenced by an environment variable. These cases pin the
///         resolution, using assemblies built in memory to stand in for differently-compiled builds.
///     </para>
/// </summary>
public sealed class UsageStatisticsContractTests
{
    [Fact]
    public void ABuildThatSetNoEndpoint_PostsToTheProductionReceiver()
    {
        var assembly = BuildAssemblyWithEndpoint(null);

        Assert.Equal(UsageStatisticsContract.DefaultPingEndpoint, UsageStatisticsContract.ResolvePingEndpoint(assembly));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABuildThatSetAnEmptyEndpoint_PostsToTheProductionReceiver(string configured)
    {
        var assembly = BuildAssemblyWithEndpoint(configured);

        Assert.Equal(UsageStatisticsContract.DefaultPingEndpoint, UsageStatisticsContract.ResolvePingEndpoint(assembly));
    }

    // The build parameter lets a local or staging image post elsewhere. The destination is a property of the
    // image rather than of the environment it runs in.
    [Theory]
    [InlineData("http://localhost:5000/v1/ping")]
    [InlineData("https://telemetry.staging.example.invalid/v1/ping")]
    [InlineData("  http://localhost:5000/v1/ping  ")]
    public void ABuildThatSetAnEndpoint_PostsThere(string configured)
    {
        var assembly = BuildAssemblyWithEndpoint(configured);

        Assert.Equal(configured.Trim(), UsageStatisticsContract.ResolvePingEndpoint(assembly));
    }

    // Falling back to the default would send a staging installation's snapshots to the production receiver
    // after a typo in the build property.
    [Theory]
    [InlineData("localhost:5000")]
    [InlineData("/v1/ping")]
    [InlineData("ftp://example.invalid/v1/ping")]
    [InlineData("not a url at all")]
    public void ABuildThatSetSomethingUnusable_RefusesToStart(string configured)
    {
        var assembly = BuildAssemblyWithEndpoint(configured);

        var exception = Assert.Throws<InvalidOperationException>(() => UsageStatisticsContract.ResolvePingEndpoint(assembly));

        Assert.Contains("UsageStatisticsEndpoint", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            UsageStatisticsContract.DefaultPingEndpoint,
            exception.Message,
            StringComparison.Ordinal);
    }

    // The assembly this test project runs against sets no endpoint, so the resolved value is the default.
    [Fact]
    public void TheAssemblyUnderTest_ResolvesToTheDefault()
    {
        Assert.Equal(UsageStatisticsContract.DefaultPingEndpoint, UsageStatisticsContract.PingEndpoint);
    }

    /// <summary>Builds an assembly carrying the metadata a differently-compiled build would carry.</summary>
    private static Assembly BuildAssemblyWithEndpoint(string? configured)
    {
        var name = new AssemblyName($"UsageStatisticsEndpointProbe_{Guid.NewGuid():N}");
        var assembly = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);

        if (configured is not null)
        {
            var constructor = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
            assembly.SetCustomAttribute(
                new CustomAttributeBuilder(
                    constructor,
                    [UsageStatisticsContract.EndpointMetadataKey, configured]));
        }

        return assembly;
    }
}
