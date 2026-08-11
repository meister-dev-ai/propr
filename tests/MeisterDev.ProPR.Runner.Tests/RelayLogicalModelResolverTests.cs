// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Runner.Contracts;
using MeisterDev.ProPR.Runner.Execution;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Runner.Tests;

/// <summary>
///     The catalog a review resolves its models through on a host with no database. What matters is that a
///     pass reaches the model it was configured with, described the way the pipeline needs it, and that a name
///     the manifest never carried fails rather than falling back to the default.
/// </summary>
public sealed class RelayLogicalModelResolverTests
{
    [Fact]
    public async Task APassModel_ResolvesToItsOwnRelayAndItsOwnEffort()
    {
        var asked = new List<string>();
        var resolver = CreateResolver(
            RunnerManifests.Sample(), role =>
            {
                asked.Add(role);
                return Substitute.For<IChatClient>();
            });

        var resolved = await resolver.ResolveChatRuntimeAsync(Guid.NewGuid(), "reviewer-high");

        Assert.Equal("reviewer-high", resolved.RoleName);
        Assert.Equal(ReviewReasoningEffort.High, resolved.ReasoningEffort);
        Assert.Equal(["reviewer-high"], asked);
    }

    // The pipeline counts a prompt against these before it sends one. A binding that arrived without them
    // would have the budgeter guess, and a guess in that direction truncates real context.
    [Fact]
    public async Task AResolvedModel_CarriesWhatThePipelineCountsTokensWith()
    {
        var resolver = CreateResolver(RunnerManifests.Sample(), _ => Substitute.For<IChatClient>());

        var resolved = await resolver.ResolveChatRuntimeAsync(Guid.NewGuid(), "reviewer-high");

        Assert.Equal("gpt-5", resolved.Runtime.Model.RemoteModelId);
        Assert.Equal("o200k_base", resolved.Runtime.Model.TokenizerName);
        Assert.Equal(400_000, resolved.Runtime.Model.MaxContextTokens);
        Assert.Equal("reviewer-high", resolved.Runtime.LogicalModelName);
    }

    [Fact]
    public async Task TheDefaultModel_ResolvesByNameLikeAnyOther()
    {
        var resolver = CreateResolver(RunnerManifests.Sample(), _ => Substitute.For<IChatClient>());

        var resolved = await resolver.ResolveChatRuntimeAsync(Guid.NewGuid(), "reviewer-default");

        Assert.Equal("reviewer-default", resolved.RoleName);
    }

    // Models are resolved once, at dispatch. A name that is not in the manifest is one this review was not
    // configured with, and answering with the default would run a pass on the wrong model.
    [Fact]
    public async Task AnUnknownName_FailsRatherThanFallingBack()
    {
        var resolver = CreateResolver(RunnerManifests.Sample(), _ => Substitute.For<IChatClient>());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveChatRuntimeAsync(Guid.NewGuid(), "reviewer-nonexistent"));

        Assert.Contains("reviewer-nonexistent", error.Message, StringComparison.Ordinal);
    }

    // The provider behind the relay caches or not regardless of which side composed the prompt. Reporting
    // caching unsupported here labelled every remote cache hit provider_unsupported on the trace; the
    // session capabilities stay off because the relay serves whole completions only.
    [Fact]
    public async Task AResolvedModel_ReportsTheCachingTheManifestVouchesFor()
    {
        var resolver = CreateResolver(RunnerManifests.Sample(), _ => Substitute.For<IChatClient>());

        var resolved = await resolver.ResolveChatRuntimeAsync(Guid.NewGuid(), "reviewer-high");

        Assert.True(resolved.Runtime.Capabilities.SupportsPromptCaching);
        Assert.False(resolved.Runtime.Capabilities.SupportsProviderManagedSessions);
        Assert.False(resolved.Runtime.Capabilities.SupportsBackgroundResponses);
    }

    // Everything that embeds runs where findings are published. A stub returning zeros here would corrupt
    // every similarity comparison downstream of it without anything failing.
    [Fact]
    public async Task Embeddings_AreRefusedRatherThanStubbed()
    {
        var resolver = CreateResolver(RunnerManifests.Sample(), _ => Substitute.For<IChatClient>());

        await Assert.ThrowsAsync<NotSupportedException>(() => resolver.ResolveEmbeddingRuntimeAsync(Guid.NewGuid(), "embedder"));
    }

    private static RelayLogicalModelResolver CreateResolver(RunnerJobManifest manifest, Func<string, IChatClient> relay)
    {
        return new RelayLogicalModelResolver(manifest, relay);
    }
}

/// <summary>Manifests the runner tests review against.</summary>
internal static class RunnerManifests
{
    public static RunnerJobManifest Sample(IReadOnlyList<string>? changedPaths = null)
    {
        return new RunnerJobManifest(
            RunnerContractVersion.Current,
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            4,
            new RunnerReviewTarget(
                "Forgejo",
                "https://forge.invalid",
                "team",
                "repo-id",
                "repo",
                "12",
                12,
                2,
                "Add the widget",
                "It adds the widget.",
                "feature/widget",
                "main",
                "head-sha",
                "base-sha",
                changedPaths ?? ["src/a.cs"],
                []),
            new RunnerWorkspaceReference("runners/execution/workspace/x/4", "head-sha", "base-sha", 1024),
            Model("reviewer-default", "None"),
            [new RunnerReviewPass(1, Model("reviewer-high", "High"), "security", null, false)],
            new RunnerPromptConfiguration("en", "Medium", new Dictionary<string, string>()),
            [],
            [],
            null,
            new RunnerTraceContext(string.Empty, null));
    }

    public static RunnerModelBinding Model(string name, string effort)
    {
        return new RunnerModelBinding(name, "gpt-5", "OpenAi", effort, "o200k_base", 200_000, 400_000, true, true, true);
    }
}
