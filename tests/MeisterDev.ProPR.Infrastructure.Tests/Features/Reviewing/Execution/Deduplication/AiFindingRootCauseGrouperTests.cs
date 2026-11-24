// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Deduplication;
using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Deduplication;

/// <summary>
///     The model-backed root-cause grouper reads one answer for a pull request and checks it before any finding merges.
///     An unknown id, an id in two groups, an oversized group, a malformed answer and a failed call each return no groups,
///     so a wrong answer keeps every finding. A pull request with more findings than one call holds is grouped in two
///     rounds, and the second round can join groups across directories.
/// </summary>
public sealed class AiFindingRootCauseGrouperTests
{
    private static readonly Guid Client = Guid.NewGuid();

    [Fact]
    public async Task GroupAsync_ValidAnswer_ReturnsTheGroupWithItsPrimaryCauseAndSpend()
    {
        var response = new ChatResponse(
            new ChatMessage(
                ChatRole.Assistant,
                """{"groups":[{"finding_ids":["F1","F3"],"primary_finding_id":"F3","cause":"Cache key without tenant id"}]}"""))
        {
            Usage = new UsageDetails { InputTokenCount = 900, OutputTokenCount = 40 },
        };
        var grouper = GrouperWith(ChatReturning(_ => response));

        var grouping = await grouper.GroupAsync(Comments(4), Client);

        var group = Assert.Single(grouping.Groups);
        Assert.Equal([0, 2], group.MemberIndexes);
        Assert.Equal(2, group.PrimaryIndex);
        Assert.Equal("Cache key without tenant id", group.Cause);
        Assert.Null(grouping.FallbackReason);
        var spend = Assert.Single(grouping.Spend);
        Assert.Equal(900L, spend.InputTokens);
        Assert.Equal(40L, spend.OutputTokens);
    }

    [Fact]
    public async Task GroupAsync_PrimaryOutsideTheGroup_KeepsTheGroupWithoutAPrimary()
    {
        var grouper = GrouperWith(ChatAnswering("""{"groups":[{"finding_ids":["F1","F2"],"primary_finding_id":"F4"}]}"""));

        var grouping = await grouper.GroupAsync(Comments(4), Client);

        Assert.Null(Assert.Single(grouping.Groups).PrimaryIndex);
    }

    [Theory]
    [InlineData("""{"groups":[{"finding_ids":["F1","F9"]}]}""", FindingRootCauseGrouping.UnknownFindingIdReason)]
    [InlineData("""{"groups":[{"finding_ids":["F1","F2"]},{"finding_ids":["F2","F3"]}]}""", FindingRootCauseGrouping.FindingInTwoGroupsReason)]
    [InlineData("""{"groups":[{"finding_ids":["F1","F2","F3","F4","F5"]}]}""", FindingRootCauseGrouping.OversizedGroupReason)]
    [InlineData("I could not find any shared causes.", FindingRootCauseGrouping.MalformedAnswerReason)]
    [InlineData("""{"clusters":[{"finding_ids":["F1","F2"]}]}""", FindingRootCauseGrouping.MalformedAnswerReason)]
    [InlineData("""{"groups":[{"finding_ids":[1,2]}]}""", FindingRootCauseGrouping.MalformedAnswerReason)]
    [InlineData("""{"groups":[{"finding_ids":["F1","F2"]}""", FindingRootCauseGrouping.MalformedAnswerReason)]
    public async Task GroupAsync_InvalidAnswer_ReturnsNoGroups(string answer, string reason)
    {
        // An invalid part discards the whole answer, including a valid group before it.
        var grouper = GrouperWith(ChatAnswering(answer));

        var grouping = await grouper.GroupAsync(Comments(8), Client);

        Assert.Empty(grouping.Groups);
        Assert.Equal(reason, grouping.FallbackReason);
    }

    // A group may hold half of the findings, and with six findings or fewer it may hold all of them.
    [Theory]
    [InlineData(8, """{"groups":[{"finding_ids":["F1","F2","F3","F4"]}]}""")]
    [InlineData(6, """{"groups":[{"finding_ids":["F1","F2","F3","F4","F5","F6"]}]}""")]
    public async Task GroupAsync_GroupWithinTheSizeLimit_IsKept(int findingCount, string answer)
    {
        var grouper = GrouperWith(ChatAnswering(answer));

        var grouping = await grouper.GroupAsync(Comments(findingCount), Client);

        Assert.Single(grouping.Groups);
        Assert.Null(grouping.FallbackReason);
    }

    [Fact]
    public async Task GroupAsync_FailedCall_ReturnsNoGroups()
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("The provider is unavailable."));
        var grouper = GrouperWith(chat);

        var grouping = await grouper.GroupAsync(Comments(4), Client);

        Assert.Empty(grouping.Groups);
        Assert.Equal(FindingRootCauseGrouping.CallFailedReason, grouping.FallbackReason);
    }

    [Fact]
    public async Task GroupAsync_WithoutAResolvableModel_ReturnsNoGroupsWithoutACall()
    {
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), AiPurpose.ReviewVerification, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("No active ReviewVerification binding."));

        var unbound = await new AiFindingRootCauseGrouper().GroupAsync(Comments(4), Client);
        var unresolved = await new AiFindingRootCauseGrouper(resolver).GroupAsync(Comments(4), Client);

        Assert.Empty(unbound.Groups);
        Assert.Equal(FindingRootCauseGrouping.ModelUnavailableReason, unbound.FallbackReason);
        Assert.Empty(unresolved.Groups);
        Assert.Equal(FindingRootCauseGrouping.ModelUnavailableReason, unresolved.FallbackReason);
    }

    [Fact]
    public async Task GroupAsync_OneFinding_AsksNoModel()
    {
        var chat = ChatAnswering("""{"groups":[]}""");

        var grouping = await GrouperWith(chat).GroupAsync(Comments(1), Client);

        Assert.Empty(grouping.Groups);
        Assert.Null(grouping.FallbackReason);
        await chat.DidNotReceiveWithAnyArgs().GetResponseAsync(default!, default, default);
    }

    [Fact]
    public async Task GroupAsync_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GrouperWith(chat).GroupAsync(Comments(4), Client, null, cancellation.Token));
    }

    // 90 findings in two directories do not fit one call. The first round groups within each directory, and the second
    // round, over the representatives of both groups and the ungrouped findings, joins the two groups.
    [Fact]
    public async Task GroupAsync_MoreFindingsThanOneCallHolds_GroupsInTwoRoundsAcrossDirectories()
    {
        var comments = Enumerable.Range(0, 90)
            .Select(index => new ReviewComment(
                index < 50 ? $"src/a/File{index:D2}.cs" : $"src/b/File{index:D2}.cs",
                index + 1,
                CommentSeverity.Warning,
                $"Finding number {index}."))
            .ToList();
        var firstChunk = """{"groups":[{"finding_ids":["F1","F2","F3","F4","F5","F6","F7","F8","F9","F10"],"primary_finding_id":"F1"}]}""";
        var secondChunk = """{"groups":[{"finding_ids":["F51","F52","F53","F54","F55","F56"],"primary_finding_id":"F51"}]}""";
        var secondRound = """{"groups":[{"finding_ids":["F1","F51"],"primary_finding_id":"F51","cause":"Shared cause"}]}""";
        var chat = ChatReturning(messages =>
        {
            var request = messages.Single(message => message.Role == ChatRole.User).Text;
            var hasFirst = request.Contains("\"id\":\"F1\"", StringComparison.Ordinal);
            var hasSecond = request.Contains("\"id\":\"F51\"", StringComparison.Ordinal);
            var answer = hasFirst && hasSecond ? secondRound : hasFirst ? firstChunk : secondChunk;
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, answer));
        });

        var grouping = await GrouperWith(chat).GroupAsync(comments, Client);

        var group = Assert.Single(grouping.Groups);
        Assert.Equal(Enumerable.Range(0, 10).Concat(Enumerable.Range(50, 6)), group.MemberIndexes);
        Assert.Equal(50, group.PrimaryIndex);
        Assert.Equal("Shared cause", group.Cause);
        Assert.Equal(3, chat.ReceivedCalls().Count());
    }

    private static IReadOnlyList<ReviewComment> Comments(int count) => Enumerable.Range(0, count)
        .Select(index => new ReviewComment($"src/File{index}.cs", 10 + index, CommentSeverity.Warning, $"Finding number {index} reports a defect."))
        .ToList();

    private static IChatClient ChatAnswering(string answer) => ChatReturning(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));

    private static IChatClient ChatReturning(Func<IReadOnlyList<ChatMessage>, ChatResponse> respond)
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => respond(call.Arg<IEnumerable<ChatMessage>>().ToList()));
        return chat;
    }

    private static AiFindingRootCauseGrouper GrouperWith(IChatClient chatClient)
    {
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(chatClient);
        runtime.Model.Returns(
            new AiConfiguredModelDto(
                Guid.NewGuid(),
                "test-model",
                "Test Model",
                [AiOperationKind.Chat],
                [ProviderDeclaredProtocolModes.Auto]));

        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(Arg.Any<Guid>(), AiPurpose.ReviewVerification, Arg.Any<CancellationToken>())
            .Returns(runtime);

        return new AiFindingRootCauseGrouper(resolver);
    }
}
