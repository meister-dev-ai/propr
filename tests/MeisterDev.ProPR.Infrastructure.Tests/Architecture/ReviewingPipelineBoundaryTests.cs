// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

namespace MeisterDev.ProPR.Infrastructure.Tests.Architecture;

/// <summary>
///     The split that lets a runner execute a review without being able to reach a database.
///     <para>
///         The review pipeline used to live in the same assembly as the persistence adapters, the
///         credential stores, and the data-protection key ring. While that was true, "the runner runs the
///         same pipeline as the control plane" and "the runner cannot reach the database" could not both
///         hold: the first requires the reference the second forbids.
///     </para>
///     <para>
///         Enforced here rather than trusted, because the direction of travel is always the wrong way. A
///         single `using` added while implementing something unrelated puts EF back on the pipeline's
///         reference graph, and nothing else would notice until a runner host failed to start.
///     </para>
/// </summary>
public sealed class ReviewingPipelineBoundaryTests
{
    private static readonly Assembly ReviewingPipeline = typeof(FileReviewer).Assembly;

    [Fact]
    public void ThePipelineAssembly_IsTheOneTheRunnerCanUse()
    {
        Assert.Equal("MeisterDev.ProPR.Reviewing", ReviewingPipeline.GetName().Name);
    }

    [Theory]
    [InlineData("MeisterDev.ProPR.Infrastructure")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.EntityFrameworkCore.Relational")]
    [InlineData("Npgsql")]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL")]
    [InlineData("Microsoft.AspNetCore.DataProtection")]
    public void ThePipeline_DoesNotReference(string forbiddenAssembly)
    {
        var referenced = ReviewingPipeline.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        Assert.DoesNotContain(forbiddenAssembly, referenced);
    }

    // Belt and braces on the reference check: a type name is what a reviewer would actually notice going
    // back in, and it catches a DbContext reached through a package the reference list does not name.
    [Fact]
    public void ThePipeline_DefinesNoTypeThatTouchesADbContext()
    {
        var offenders = ReviewingPipeline.GetTypes()
            .Where(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .Any(p => p.ParameterType.Name.Contains("DbContext", StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .ToArray();

        Assert.Empty(offenders);
    }
}
