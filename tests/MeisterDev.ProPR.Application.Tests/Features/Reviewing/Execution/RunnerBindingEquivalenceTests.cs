// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

/// <summary>
///     Proves the two bindings answer the same. Two adapter sets behind one pipeline drift, and a proxy
///     adapter that differs subtly from its direct counterpart produces a review that is wrong only when
///     executed remotely, which is the hardest class of defect to notice and the easiest to ship.
///     <para>
///         Runs entirely in process against a recorded fixture: no transport, no provider, no paid call, so
///         it belongs in the standard gate rather than in a nightly.
///     </para>
/// </summary>
public sealed class RunnerBindingEquivalenceTests
{
    private static readonly RunnerCallContext Call =
        new(Guid.Parse("12121212-1212-1212-1212-121212121212"), 1, "runner-a");

    /// <summary>The recorded fixture both bindings answer from. One source of truth, two ways of reaching it.</summary>
    private static IReviewContextTools Fixture()
    {
        var tools = Substitute.For<IReviewContextTools>();

        tools.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChangedFileSummary>>(_ =>
            [
                new ChangedFileSummary("src/a.cs", ChangeType.Edit),
                new ChangedFileSummary("src/b.cs", ChangeType.Add),
            ]);
        tools.GetFileTreeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<string>>(_ => ["src/a.cs", "src/b.cs", "src/untouched.cs"]);
        tools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("public class A { }");
        tools.AskProCursorKnowledgeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorKnowledgeAnswerDto("answered", [], null));
        tools.GetLinkedItemDiscussionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<LinkedItemComment>>(_ => []);

        return tools;
    }

    /// <summary>
    ///     The proxy binding: the same fixture reached through the control plane's proxy for the six
    ///     credentialed operations, and directly for the twelve that read the working copy.
    /// </summary>
    private static (IReviewContextTools Tools, IRunnerJobToolsRegistry Registry) ProxyBinding(
        IReviewContextTools fixture,
        bool codeKnowledgeOffered = true)
    {
        var authorizer = Substitute.For<IRunnerCallAuthorizer>();
        authorizer.AuthorizeAsync(Arg.Any<RunnerCallContext>(), Arg.Any<CancellationToken>())
            .Returns(RunnerCallAuthorization.Allow(Guid.NewGuid()));

        var registry = new RunnerJobToolsRegistry();
        registry.Register(Call.JobId, fixture, codeKnowledgeOffered);

        var proxy = new RunnerToolProxy(authorizer, registry);
        return (new ProxyReviewContextTools(Call, proxy, fixture), registry);
    }

    // The operation that crosses the boundary. Both bindings must answer identically, or a review sees a
    // different change set depending on where it ran.
    [Fact]
    public async Task AProxiedOperation_AnswersTheSameAsTheDirectBinding()
    {
        var fixture = Fixture();
        var (proxied, _) = ProxyBinding(fixture);

        var direct = await fixture.GetChangedFilesAsync(CancellationToken.None);
        var remote = await proxied.GetChangedFilesAsync(CancellationToken.None);

        Assert.Equal(direct, remote);
    }

    // The twelve that stay local must answer identically too: the point of replicating the workspace is
    // that they are the same reads, not similar ones.
    [Fact]
    public async Task AWorkspaceOperation_AnswersTheSameAsTheDirectBinding()
    {
        var fixture = Fixture();
        var (proxied, _) = ProxyBinding(fixture);

        Assert.Equal(
            await fixture.GetFileTreeAsync("head", CancellationToken.None),
            await proxied.GetFileTreeAsync("head", CancellationToken.None));
        Assert.Equal(
            await fixture.GetFileContentAsync("src/a.cs", "head", 1, 10, CancellationToken.None),
            await proxied.GetFileContentAsync("src/a.cs", "head", 1, 10, CancellationToken.None));
    }

    [Fact]
    public async Task EveryOperationInTheFixture_AgreesAcrossBothBindings()
    {
        var fixture = Fixture();
        var (proxied, _) = ProxyBinding(fixture);

        Assert.Equal(
            await fixture.GetChangedFilesAsync(CancellationToken.None),
            await proxied.GetChangedFilesAsync(CancellationToken.None));
        Assert.Equal(
            (await fixture.AskProCursorKnowledgeAsync("why", CancellationToken.None)).Status,
            (await proxied.AskProCursorKnowledgeAsync("why", CancellationToken.None)).Status);
        Assert.Equal(
            await fixture.GetLinkedItemDiscussionAsync("AB#1", CancellationToken.None),
            await proxied.GetLinkedItemDiscussionAsync("AB#1", CancellationToken.None));
    }

    // The deliberate mutation. A suite that cannot fail proves nothing, so this introduces exactly the
    // kind of subtle proxy-side difference the suite exists to catch and asserts that it is caught.
    [Fact]
    public async Task ADeliberateDifferenceIntroducedInTheProxyBinding_IsCaught()
    {
        var fixture = Fixture();
        var mutatedProxy = Substitute.For<IRunnerToolProxy>();
        mutatedProxy.GetChangedFilesAsync(Arg.Any<RunnerCallContext>(), Arg.Any<CancellationToken>())
            .Returns(
                RunnerToolResult<IReadOnlyList<ChangedFileSummary>>.Served(
                    // One file dropped: the sort of difference that produces a review missing a file only
                    // when it runs remotely.
                    [new ChangedFileSummary("src/a.cs", ChangeType.Edit)]));

        var mutated = new ProxyReviewContextTools(Call, mutatedProxy, fixture);

        var direct = await fixture.GetChangedFilesAsync(CancellationToken.None);
        var remote = await mutated.GetChangedFilesAsync(CancellationToken.None);

        Assert.NotEqual(direct, remote);
    }

    // An installation without code knowledge must produce the same explicit unavailable outcome on both
    // sides. An executor told "nothing found" would record that as evidence about the code.
    [Fact]
    public async Task WithCodeKnowledgeOff_BothBindingsGiveTheExplicitUnavailableOutcome()
    {
        var fixture = Fixture();
        fixture.AskProCursorKnowledgeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorKnowledgeAnswerDto("unavailable", [], "disabled"));
        var (proxied, _) = ProxyBinding(fixture, codeKnowledgeOffered: false);

        var remote = await proxied.AskProCursorKnowledgeAsync("why", CancellationToken.None);

        Assert.Equal("unavailable", remote.Status);
        Assert.Empty(remote.Results);
    }

    // A refusal is not a tool failure the review should absorb. Losing the lease has to stop the review,
    // not quietly give it an empty context to draw conclusions from.
    [Fact]
    public async Task WhenTheControlPlaneRefuses_TheProxyBindingStopsRatherThanDegrades()
    {
        var refusing = Substitute.For<IRunnerToolProxy>();
        refusing.GetChangedFilesAsync(Arg.Any<RunnerCallContext>(), Arg.Any<CancellationToken>())
            .Returns(RunnerToolResult<IReadOnlyList<ChangedFileSummary>>.Refused(RunnerCallRefusal.SupersededGeneration));

        var proxied = new ProxyReviewContextTools(Call, refusing, Fixture());

        var thrown = await Assert.ThrowsAsync<RunnerCallRefusedException>(() => proxied.GetChangedFilesAsync(CancellationToken.None));
        Assert.Equal(RunnerCallRefusal.SupersededGeneration, thrown.Refusal);
    }

    // Naming the port in the failure is what makes an unimplemented one findable rather than a mystery
    // null somewhere downstream.
    [Fact]
    public async Task ARefusalNamesTheOperationThatWasRefused()
    {
        var refusing = Substitute.For<IRunnerToolProxy>();
        refusing.ResolveLinkedItemAsync(Arg.Any<RunnerCallContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(RunnerToolResult<LinkedItem?>.Refused(RunnerCallRefusal.JobNotExecuting));

        var proxied = new ProxyReviewContextTools(Call, refusing, Fixture());

        var thrown = await Assert.ThrowsAsync<RunnerCallRefusedException>(() => proxied.ResolveLinkedItemAsync("AB#9", CancellationToken.None));
        Assert.Contains("ResolveLinkedItem", thrown.Operation, StringComparison.Ordinal);
    }

    // A transport fault is a tool failure the model should see and may retry — the same behaviour an
    // in-process provider blip has. Absorbed into an empty answer, a 502 during a rolling restart told
    // the reviewer the pull request changed no files, silently.
    [Fact]
    public async Task ATransportFault_SurfacesAsAToolErrorRatherThanAnEmptyAnswer()
    {
        var faulting = Substitute.For<IRunnerToolProxy>();
        faulting.GetChangedFilesAsync(Arg.Any<RunnerCallContext>(), Arg.Any<CancellationToken>())
            .Returns(RunnerToolResult<IReadOnlyList<ChangedFileSummary>>.Faulted("the control plane answered HTTP 502"));

        var proxied = new ProxyReviewContextTools(Call, faulting, Fixture());

        var thrown = await Assert.ThrowsAsync<RunnerToolFaultedException>(() => proxied.GetChangedFilesAsync(CancellationToken.None));
        Assert.Contains("502", thrown.Reason, StringComparison.Ordinal);
    }
}
