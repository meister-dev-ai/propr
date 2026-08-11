// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.CodeAnalysis;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;
using MeisterDev.ProPR.ProRV.Abstractions;
using MeisterDev.ProPR.Runner.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterDev.ProPR.Runner.Tests;

/// <summary>
///     The drift guard on the runner's side of the composition.
///     <para>
///         The defect class this exists for: a collaborator is added to the in-process pipeline, the
///         runner's composition is not updated, the null takes an early return, and a remote review becomes a
///         materially different review with nothing reporting it. These tests hold the composition's report
///         complete against the constructors it mirrors, so the addition fails a test here until it is given
///         a disposition: supplied, equivalent, or absent. An absence is recorded on every remote review's
///         trace.
///     </para>
/// </summary>
public sealed class RunnerReviewPipelineTests
{
    private static readonly Type[] MirroredTypes =
    [
        typeof(FileReviewer),
        typeof(FileByFileReviewOrchestrator),
        typeof(FileReviewDispatchPlanner),
    ];

    [Fact]
    public void EveryConstructorParameter_IsNamedInTheReport()
    {
        using var pipeline = Compose();
        var named = pipeline.Report.Select(entry => entry.Parameter).ToHashSet(StringComparer.Ordinal);

        var missing = ConstructorParameterNames().Where(parameter => !named.Contains(parameter)).ToList();

        Assert.True(
            missing.Count == 0,
            "The in-process pipeline takes collaborators the runner composition does not name: "
            + string.Join(", ", missing)
            + ". Decide each one — supply it, declare it equivalent, or declare it absent — in RunnerReviewPipeline.");
    }

    [Fact]
    public void TheReport_NamesNothingThePipelineNoLongerTakes()
    {
        using var pipeline = Compose();
        var parameters = ConstructorParameterNames().ToHashSet(StringComparer.Ordinal);

        var stale = pipeline.Report.Select(entry => entry.Parameter).Where(name => !parameters.Contains(name)).ToList();

        Assert.True(
            stale.Count == 0,
            "The composition report names parameters no mirrored constructor takes: " + string.Join(", ", stale));
    }

    // Absences are decisions, not defaults. A new absence must be added here deliberately, with its
    // consequence written down, which is what separates a smaller review from a different one.
    [Fact]
    public void TheAbsences_AreExactlyTheDecidedOnes()
    {
        using var pipeline = Compose();

        var absent = pipeline.Report
            .Where(entry => entry.Disposition == RunnerCompositionDisposition.Absent)
            .Select(entry => entry.Parameter)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["prWideCandidateGeneratorFactory"], absent);
    }

    [Fact]
    public void TheComposition_Builds()
    {
        using var pipeline = Compose();

        Assert.NotNull(pipeline.Orchestrator);
    }

    private static IEnumerable<string> ConstructorParameterNames()
    {
        return MirroredTypes
            .SelectMany(type => type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            .SelectMany(ctor => ctor.GetParameters())
            .Select(parameter => parameter.Name!)
            .Distinct(StringComparer.Ordinal);
    }

    private static RunnerReviewPipeline Compose()
    {
        return RunnerReviewPipeline.Compose(
            Options.Create(new AiReviewOptions()),
            Substitute.For<IProtocolRecorder>(),
            Substitute.For<IReviewFileResultStore>(),
            Substitute.For<IChatClient>(),
            Substitute.For<IAiRuntimeResolver>(),
            Substitute.For<ILogicalModelResolver>(),
            Substitute.For<IThreadMemoryService>(),
            Substitute.For<IProRVPrefilter>(),
            Substitute.For<IStructuralCodeAnalyzer>(),
            Substitute.For<MeisterDev.ProPR.Application.Features.Licensing.Ports.ILicensingCapabilityService>(),
            () => false,
            NullLoggerFactory.Instance);
    }
}
