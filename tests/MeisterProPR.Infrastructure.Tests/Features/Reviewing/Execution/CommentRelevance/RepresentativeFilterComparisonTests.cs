// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;
using MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.CommentRelevance.Fixtures;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.CommentRelevance;

public sealed class RepresentativeFilterComparisonTests
{
    [Fact]
    public async Task RepresentativeCorpus_ProducesComparableOutputsAcrossNamedImplementations()
    {
        var runs = await ExecuteCorpusAsync();

        Assert.Equal(RepresentativeFilterEvaluationCorpus.Version, runs.CorpusVersion);
        Assert.Equal(RepresentativeFilterEvaluationCorpus.Cases.Count, runs.OutputsByImplementation["heuristic-v1"].Count);
        Assert.Equal(RepresentativeFilterEvaluationCorpus.Cases.Count, runs.OutputsByImplementation["hybrid-v1"].Count);

        Assert.All(
            runs.OutputsByImplementation["heuristic-v1"],
            output =>
            {
                Assert.Equal("heuristic-v1", output.ImplementationId);
                Assert.Equal("1.0.0", output.ImplementationVersion);
            });

        Assert.All(
            runs.OutputsByImplementation["hybrid-v1"],
            output =>
            {
                Assert.Equal("hybrid-v1", output.ImplementationId);
                Assert.Equal("1.0.0", output.ImplementationVersion);
            });
    }

    [Fact]
    public async Task RepresentativeCorpus_HybridMeetsReductionAndRetentionThresholdsAgainstBaseline()
    {
        var runs = await ExecuteCorpusAsync();
        var hybrid = runs.MetricsByImplementation["hybrid-v1"];

        Assert.True(
            hybrid.FalsePositiveReductionFromBaseline >= 0.30d,
            $"Expected hybrid false-positive reduction >= 30%, actual {hybrid.FalsePositiveReductionFromBaseline:P1}.");
        Assert.True(
            hybrid.RetainedValidRate >= 0.95d,
            $"Expected hybrid retained-valid rate >= 95%, actual {hybrid.RetainedValidRate:P1}.");
        Assert.True(
            hybrid.UnsupportedSpeculativeHighSeverityRemovalRate >= 0.80d,
            $"Expected hybrid unsupported speculative WARNING/ERROR removal >= 80%, actual {hybrid.UnsupportedSpeculativeHighSeverityRemovalRate:P1}.");
    }

    [Fact]
    public async Task RepresentativeCorpus_ExposesDegradedRunRatesAndTokenCostTradeoffs()
    {
        var runs = await ExecuteCorpusAsync();
        var heuristic = runs.MetricsByImplementation["heuristic-v1"];
        var hybrid = runs.MetricsByImplementation["hybrid-v1"];

        Assert.Equal(RepresentativeFilterEvaluationCorpus.Cases.Count, heuristic.FilesEvaluated);
        Assert.Equal(RepresentativeFilterEvaluationCorpus.Cases.Count, hybrid.FilesEvaluated);
        Assert.Equal(0, heuristic.DegradedRunCount);
        Assert.True(hybrid.DegradedRunCount > 0);
        Assert.Equal(0, heuristic.TotalInputTokens);
        Assert.Equal(0, heuristic.TotalOutputTokens);
        Assert.True(hybrid.TotalInputTokens > 0);
        Assert.True(hybrid.TotalOutputTokens > 0);
        Assert.True(hybrid.RetainedValidRate > heuristic.RetainedValidRate);
    }

    private static async Task<CorpusRunResult> ExecuteCorpusAsync()
    {
        var passThroughFilter = new PassThroughCommentRelevanceFilter();
        var heuristicFilter = new HeuristicCommentRelevanceFilter();
        var hybridFilter = new HybridCommentRelevanceFilter(new RepresentativeCorpusAmbiguityEvaluator());

        var outputsByImplementation = new Dictionary<string, IReadOnlyList<RecordedFilterOutput>>(StringComparer.Ordinal)
        {
            [passThroughFilter.ImplementationId] = await ExecuteFilterAsync(passThroughFilter),
            [heuristicFilter.ImplementationId] = await ExecuteFilterAsync(heuristicFilter),
            [hybridFilter.ImplementationId] = await ExecuteFilterAsync(hybridFilter),
        };

        var baseline = CalculateMetrics(outputsByImplementation[passThroughFilter.ImplementationId]);
        var metricsByImplementation = outputsByImplementation
            .Where(pair => pair.Key != passThroughFilter.ImplementationId)
            .ToDictionary(
                pair => pair.Key,
                pair => CalculateMetrics(pair.Value, baseline.FalsePositiveKept),
                StringComparer.Ordinal);

        return new CorpusRunResult(RepresentativeFilterEvaluationCorpus.Version, outputsByImplementation, metricsByImplementation);
    }

    private static async Task<IReadOnlyList<RecordedFilterOutput>> ExecuteFilterAsync(ICommentRelevanceFilter filter)
    {
        var outputs = new List<RecordedFilterOutput>(RepresentativeFilterEvaluationCorpus.Cases.Count);

        foreach (var evaluationCase in RepresentativeFilterEvaluationCorpus.Cases)
        {
            var request = CommentRelevanceFilterTestData.CreateRequest(
                evaluationCase.Comments.Select(item => item.Comment).ToArray(),
                filter.ImplementationId,
                evaluationCase.FilePath,
                20);

            var result = await filter.FilterAsync(request, CancellationToken.None);
            outputs.Add(result.ToRecordedOutput());
        }

        return outputs.AsReadOnly();
    }

    private static CorpusMetrics CalculateMetrics(IReadOnlyList<RecordedFilterOutput> outputs, int baselineFalsePositiveKept = 0)
    {
        var outputByFile = outputs.ToDictionary(output => output.FilePath, output => output, StringComparer.Ordinal);

        var totalValid = 0;
        var validKept = 0;
        var falsePositiveKept = 0;
        var unsupportedSpeculativeHighSeverityTotal = 0;
        var unsupportedSpeculativeHighSeverityDiscarded = 0;

        foreach (var evaluationCase in RepresentativeFilterEvaluationCorpus.Cases)
        {
            var output = outputByFile[evaluationCase.FilePath];
            var discardedMessages = new HashSet<string>(output.Discarded.Select(item => item.Message), StringComparer.Ordinal);

            foreach (var comment in evaluationCase.Comments)
            {
                var wasKept = !discardedMessages.Contains(comment.Comment.Message);

                if (comment.Categories.HasFlag(RepresentativeFilterCommentCategory.ConfirmedValid))
                {
                    totalValid++;
                    if (wasKept)
                    {
                        validKept++;
                    }
                }

                if (comment.Categories.HasFlag(RepresentativeFilterCommentCategory.KnownFalsePositive))
                {
                    if (wasKept)
                    {
                        falsePositiveKept++;
                    }
                }

                if (comment.Categories.HasFlag(RepresentativeFilterCommentCategory.UnsupportedSpeculativeHighSeverity))
                {
                    unsupportedSpeculativeHighSeverityTotal++;
                    if (!wasKept)
                    {
                        unsupportedSpeculativeHighSeverityDiscarded++;
                    }
                }
            }
        }

        var degradedRunCount = outputs.Count(output => output.DegradedComponents.Count > 0);
        var totalInputTokens = outputs.Sum(output => output.AiTokenUsage?.InputTokens ?? 0);
        var totalOutputTokens = outputs.Sum(output => output.AiTokenUsage?.OutputTokens ?? 0);
        var retainedValidRate = totalValid == 0 ? 1d : (double)validKept / totalValid;
        var unsupportedRemovalRate = unsupportedSpeculativeHighSeverityTotal == 0
            ? 1d
            : (double)unsupportedSpeculativeHighSeverityDiscarded / unsupportedSpeculativeHighSeverityTotal;
        var falsePositiveReductionFromBaseline = baselineFalsePositiveKept == 0
            ? 0d
            : 1d - (double)falsePositiveKept / baselineFalsePositiveKept;

        return new CorpusMetrics(
            outputs.Count,
            degradedRunCount,
            totalValid,
            validKept,
            falsePositiveKept,
            unsupportedSpeculativeHighSeverityTotal,
            unsupportedSpeculativeHighSeverityDiscarded,
            totalInputTokens,
            totalOutputTokens,
            retainedValidRate,
            unsupportedRemovalRate,
            falsePositiveReductionFromBaseline);
    }

    private sealed class RepresentativeCorpusAmbiguityEvaluator : ICommentRelevanceAmbiguityEvaluator
    {
        public Task<CommentRelevanceAmbiguityEvaluationResult> EvaluateAsync(
            CommentRelevanceFilterRequest request,
            IReadOnlyList<ReviewComment> comments,
            CancellationToken ct = default)
        {
            var evaluationCase = RepresentativeFilterEvaluationCorpus.Cases.Single(item => item.FilePath == request.FilePath);

            return evaluationCase.HybridEvaluationMode switch
            {
                RepresentativeHybridEvaluationMode.Unavailable => Task.FromResult(
                    CommentRelevanceAmbiguityEvaluationResult.Unavailable(
                        ["comment_relevance_evaluator"],
                        ["ambiguous_survivors_left_unchanged"],
                        evaluationCase.HybridDegradedCause)),
                RepresentativeHybridEvaluationMode.Successful => Task.FromResult(BuildSuccessfulResult(evaluationCase, comments)),
                _ => Task.FromResult(new CommentRelevanceAmbiguityEvaluationResult([], true, null, [], [], null)),
            };
        }

        private static CommentRelevanceAmbiguityEvaluationResult BuildSuccessfulResult(
            RepresentativeFilterEvaluationCase evaluationCase,
            IReadOnlyList<ReviewComment> comments)
        {
            var byMessage = evaluationCase.Comments.ToDictionary(item => item.Comment.Message, item => item, StringComparer.Ordinal);
            var decisions = comments
                .Select(comment =>
                {
                    var fixture = byMessage[comment.Message];
                    return fixture.HybridDecision switch
                    {
                        RepresentativeHybridDecision.Discard => new CommentRelevanceFilterDecision(
                            CommentRelevanceFilterDecision.DiscardDecision,
                            comment,
                            [fixture.HybridDiscardReasonCode ?? CommentRelevanceReasonCodes.UnverifiableCrossFileClaim],
                            CommentRelevanceFilterDecision.AiAdjudicationSource),
                        RepresentativeHybridDecision.Keep => new CommentRelevanceFilterDecision(
                            CommentRelevanceFilterDecision.KeepDecision,
                            comment,
                            [],
                            CommentRelevanceFilterDecision.AiAdjudicationSource),
                        _ => throw new InvalidOperationException($"Missing hybrid evaluator fixture for '{evaluationCase.CaseId}'."),
                    };
                })
                .ToArray();

            return new CommentRelevanceAmbiguityEvaluationResult(
                decisions,
                true,
                new FilterAiTokenUsage(
                    "hybrid-v1",
                    evaluationCase.FilePath,
                    evaluationCase.HybridInputTokens,
                    evaluationCase.HybridOutputTokens,
                    AiConnectionModelCategory.Default,
                    $"representative-corpus-{RepresentativeFilterEvaluationCorpus.Version}"),
                [],
                [],
                null);
        }
    }

    private sealed record CorpusRunResult(
        string CorpusVersion,
        IReadOnlyDictionary<string, IReadOnlyList<RecordedFilterOutput>> OutputsByImplementation,
        IReadOnlyDictionary<string, CorpusMetrics> MetricsByImplementation);

    private sealed record CorpusMetrics(
        int FilesEvaluated,
        int DegradedRunCount,
        int TotalValid,
        int ValidKept,
        int FalsePositiveKept,
        int UnsupportedSpeculativeHighSeverityTotal,
        int UnsupportedSpeculativeHighSeverityDiscarded,
        long TotalInputTokens,
        long TotalOutputTokens,
        double RetainedValidRate,
        double UnsupportedSpeculativeHighSeverityRemovalRate,
        double FalsePositiveReductionFromBaseline);
}
