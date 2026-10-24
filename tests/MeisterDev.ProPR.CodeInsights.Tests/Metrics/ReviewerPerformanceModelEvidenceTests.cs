// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Misses;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.CodeInsights.Tests.Metrics;

public sealed class ReviewerPerformanceModelEvidenceTests
{
    [Theory]
    [InlineData("filter")]
    [InlineData("group")]
    [InlineData("matrix")]
    public void RiskRestrictionsDisableRecallDespiteValidIndependentModelProof(string mode)
    {
        var fixture = new Fixture();
        fixture.Concern("missed", false);
        var publication = new ReviewerPerformanceDailyCount
        {
            CodeInsightPullRequestId = fixture.AggregateId, ClientId = ModelOpportunityEvaluatorTests.Source.ClientId,
            RepositoryId = "repo", PullRequestId = 42, ProviderScope = fixture.Key.ProviderScope, BucketDate = fixture.To,
            ModelId = "A", LogicalModelName = "logical-A", Outcome = "positive", PublicationState = CodeInsightPublicationState.Published,
            DuplicateState = CodeInsightDuplicateState.CheckedNonduplicate, IsClassified = true, Count = 3,
            Impact = FindingImpact.High, Likelihood = FindingLikelihood.Likely, RiskSeverity = FindingRiskSeverity.High,
            RiskRubricVersion = FindingRiskAssessment.CurrentRubricVersion,
        };
        var scope = fixture.Scope("A");
        var snapshot = fixture.Snapshot([publication]);
        var baselineQuery = new ReviewerPerformanceQuery { Views = [scope] };
        var baseline = ReviewerPerformanceViewBuilder.Build(baselineQuery, scope, 0, [publication.ClientId], snapshot, new(80, 1), DateTimeOffset.UtcNow);
        Assert.NotNull(baseline.Series[0].Points.Last().Score.Summary.Recall);
        if (mode == "filter")
        {
            scope = scope with { RiskSeverities = ["High"] };
        }

        var query = baselineQuery with
        {
            Views = [scope], Grouping = mode == "group" ? "likelihood" : "none",
            Breakdown = mode == "matrix" ? new("model", "impact", fixture.To) : null,
        };
        var view = ReviewerPerformanceViewBuilder.Build(query, scope, 0, [publication.ClientId], snapshot, new(80, 1), DateTimeOffset.UtcNow);
        var points = (mode == "matrix" ? view.Breakdown.Select(cell => cell.Measurement) : view.Series.Select(series => series.Points.Last())).ToList();
        Assert.NotEmpty(points);
        Assert.All(
            points, point =>
            {
                Assert.Equal(1, point.Coverage!.CoveredPullRequests);
                Assert.False(point.Coverage.RecallSupported);
                Assert.Contains("miss-risk-assessment-unavailable", point.UnavailableReasons);
                Assert.Null(point.Score.Summary.Recall);
                Assert.Null(point.Score.Summary.F1);
                Assert.NotNull(point.OverallPrecision!.Summary);
            });
    }

    [Fact]
    public void ModelExclusionsCaptureClassificationCooldownAndPermanentHistoryActions()
    {
        var fixture = new Fixture();
        var concern = fixture.Concern("X", false);
        concern.RetainedSourceComplete = true;
        concern.JudgementFailed = true;
        var snapshot = fixture.Snapshot();
        var captured = ModelOpportunityEvaluatorTests.Origin.AddHours(2);
        concern.LastModelEvidenceRetryAt = captured.AddMinutes(-2);
        snapshot = snapshot with { ModelRetryCooldown = TimeSpan.FromMinutes(17) };
        var scope = fixture.Scope("A");
        var query = new ReviewerPerformanceQuery { Views = [scope] };
        var view = ReviewerPerformanceViewBuilder.Build(query, scope, 0, [ModelOpportunityEvaluatorTests.Source.ClientId], snapshot, new(80, 1), captured);
        var exclusion = Assert.Single(view.ExclusionCatalog!);
        Assert.Equal("retry-model-evidence", exclusion.RecoveryAction);
        Assert.Equal(ModelOpportunityEvaluatorTests.Source.ConnectionId, exclusion.SourceConnectionId);
        Assert.Equal("cooldown", exclusion.RecoveryDisposition);
        Assert.Equal(captured.AddMinutes(15), exclusion.ModelRetryNotBefore);
        Assert.Equal("model-opportunity-v1", view.Evidence.ModelEvidenceSchema);

        concern.JudgementFailed = false;
        concern.Anchor = concern.Anchor! with { OriginalScopeProven = false };
        view = ReviewerPerformanceViewBuilder.Build(
            query, scope, 0, [ModelOpportunityEvaluatorTests.Source.ClientId], fixture.Snapshot(), new(80, 1), captured);
        exclusion = Assert.Single(view.ExclusionCatalog!);
        Assert.Equal("none", exclusion.RecoveryAction);
        Assert.Equal("unreconstructable-history", exclusion.RecoveryDisposition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EvaluationLimitIsRejectedBeforeTraversalRegardlessOfConcernOrdering(bool reverse)
    {
        var fixture = new Fixture();
        fixture.Concern("X", false);
        fixture.Concern("Y", false);
        if (reverse)
        {
            fixture.Concerns.Reverse();
        }

        Assert.Throws<ArgumentException>(() => fixture.Build(budget: new(CandidateReferences: 1)));
    }

    [Fact]
    public void PublishedModelWithMissingProofRemainsExcludedForSingletonAndEnsemble()
    {
        var fixture = new Fixture();
        fixture.Proof = fixture.Proof with { Passes = [ModelOpportunityEvaluatorTests.Pass("A")] };
        var publication = new ReviewerPerformanceDailyCount
        {
            CodeInsightPullRequestId = fixture.AggregateId, ClientId = ModelOpportunityEvaluatorTests.Source.ClientId,
            RepositoryId = "repo", PullRequestId = 42, ProviderScope = fixture.Key.ProviderScope, BucketDate = fixture.To, ModelId = "B",
            LogicalModelName = "logical-B",
            Outcome = "positive", PublicationState = CodeInsightPublicationState.Published, DuplicateState = CodeInsightDuplicateState.CheckedNonduplicate,
            IsClassified = true, Count = 3
        };
        foreach (var models in new[] { new[] { "B" }, new[] { "A", "B" } })
        {
            var scope = fixture.Scope(models);
            var query = new ReviewerPerformanceQuery { Bucket = "day", Aggregation = "cumulative", Views = [scope] };
            var view = ReviewerPerformanceViewBuilder.Build(
                query, scope, 0, [publication.ClientId], fixture.Snapshot([publication]), new(80, 1), ModelOpportunityEvaluatorTests.Origin.AddHours(2));
            var point = Assert.Single(view.Series).Points.Last();
            Assert.Equal(1, point.Coverage!.CandidatePullRequests);
            Assert.Equal(1, point.Coverage.ExcludedPullRequests);
            Assert.Equal(0, point.Coverage.CoveredPullRequests);
            Assert.Equal(0, point.Score.Counts.Outcomes.Total);
            Assert.Equal(3, point.OverallPrecision!.Counts.Outcomes.Positive);
        }
    }

    [Fact]
    public void QualifiedZeroModelHasNumericZeroRecallAndF1AndMatchesTheMatrix()
    {
        var fixture = new Fixture();
        fixture.Concern("missed", false);
        var scope = fixture.Scope("A");
        var query = new ReviewerPerformanceQuery
        {
            Aggregation = "period", Bucket = "day", Grouping = "model", Views = [scope],
            Breakdown = new("model", "qualifier", fixture.To)
        };
        var view = ReviewerPerformanceViewBuilder.Build(
            query, scope, 0, [ModelOpportunityEvaluatorTests.Source.ClientId], fixture.Snapshot(), new(80, 1),
            ModelOpportunityEvaluatorTests.Origin.AddHours(2));
        var point = Assert.Single(view.Series).Points.Last();
        Assert.Equal(1, point.Coverage!.CoveredPullRequests);
        Assert.Null(point.Score.Summary.Precision);
        Assert.Equal(0, point.Score.Summary.Recall!.Median);
        Assert.Equal(0, point.Score.Summary.F1!.Median);
        var matrix = Assert.Single(view.Breakdown.Where(cell => cell.ColumnId == "Incorrect"));
        Assert.Equal(point.Score.Summary.F1, matrix.Measurement.Score.Summary.F1);
        Assert.Equal(point.Coverage.CoveredPullRequests, matrix.Measurement.Coverage!.CoveredPullRequests);
        Assert.Contains(view.Facets.Models, model => model.Id == scope.Models![0]);
    }

    [Fact]
    public void CombinedLegacyMissesRemainUnchangedWhileSelectedModelsUseIndependentConcerns()
    {
        var fixture = new Fixture();
        fixture.Concern("X", true);
        fixture.Concern("Y", false);
        var modelScope = fixture.Scope("A", "B");
        var legacyScope = modelScope with { Models = null };
        var query = new ReviewerPerformanceQuery { Aggregation = "cumulative", Bucket = "day", Views = [modelScope] };
        var model = ReviewerPerformanceViewBuilder.Build(
            query, modelScope, 0, [ModelOpportunityEvaluatorTests.Source.ClientId], fixture.Snapshot(), new(80, 1),
            ModelOpportunityEvaluatorTests.Origin.AddHours(2));
        Assert.Equal(1, Assert.Single(model.Series).Points.Last().Score.Counts.ActedMisses);
        var legacy = ReviewerPerformanceViewBuilder.Build(
            query with { Views = [legacyScope] }, legacyScope, 0,
            [ModelOpportunityEvaluatorTests.Source.ClientId], fixture.Snapshot(), new(80, 1), ModelOpportunityEvaluatorTests.Origin.AddHours(2));
        Assert.Equal(7, Assert.Single(legacy.Series).Points.Last().Score.Counts.ActedMisses);
    }

    [Fact]
    public void SingletonsAndEnsembleCountCanonicalConcernsOnceAndPreserveBroaderPrecision()
    {
        var fixture = new Fixture();
        fixture.Concern("X", detectedByB: true);
        fixture.Concern("Y", detectedByB: false);
        var context = fixture.Build();
        var a = context.Misses(fixture.Scope("A"), [fixture.Key], fixture.From, fixture.To);
        var b = context.Misses(fixture.Scope("B"), [fixture.Key], fixture.From, fixture.To);
        var ensemble = context.Misses(fixture.Scope("A", "B"), [fixture.Key], fixture.From, fixture.To);
        Assert.Equal(2, a.ActedMisses);
        Assert.Equal(1, b.ActedMisses);
        Assert.Equal(1, ensemble.ActedMisses);
        Assert.Empty(context.Assess(fixture.Key, fixture.Scope("A", "B"), fixture.To).Reasons);
    }

    [Fact]
    public void FailedBExcludesOnlyAffectedModelSetAndScopeIsEvaluatedBeforeTypeSelection()
    {
        var fixture = new Fixture();
        fixture.Concern("X", false);
        fixture.Decisions.Single(row => row.ModelId == "B").Decision = "unknown";
        var context = fixture.Build();
        Assert.Empty(context.Assess(fixture.Key, fixture.Scope("A"), fixture.To).Reasons);
        Assert.Contains("model-concern-match-unavailable", context.Assess(fixture.Key, fixture.Scope("B") with { Types = ["other"] }, fixture.To).Reasons);
        Assert.NotEmpty(context.Assess(fixture.Key, fixture.Scope("A", "B"), fixture.To).Reasons);
    }

    [Fact]
    public void UnknownAnchorAndFailedTruthExcludeButSuccessfulOpenConcernIsInformational()
    {
        var fixture = new Fixture();
        var concern = fixture.Concern("X", false);
        concern.Anchor = ModelOpportunityEvaluatorTests.Anchor with { OriginalScopeProven = false };
        Assert.Contains("model-concern-anchor-unavailable", fixture.Build().Assess(fixture.Key, fixture.Scope("A"), fixture.To).Reasons);
        concern.ThreadResolved = false;
        Assert.Empty(fixture.Build().Assess(fixture.Key, fixture.Scope("A"), fixture.To).Reasons);
        concern.JudgementFailed = true;
        Assert.Contains("model-concern-judgement-failed", fixture.Build().Assess(fixture.Key, fixture.Scope("A"), fixture.To).Reasons);
    }

    [Fact]
    public void CanonicalAliasDoesNotMultiplyMissesAndFutureConcernDoesNotPoisonEarlierPoint()
    {
        var fixture = new Fixture();
        var first = fixture.Concern("X", false);
        var alias = fixture.Concern("same X", false);
        alias.CanonicalConcernId = first.Id;
        alias.EquivalenceProof = "explicit-candidate-or-concern-match";
        var future = fixture.Concern("later", false);
        future.HarvestedAt = future.HarvestedAt.AddDays(2);
        future.JudgementFailed = true;
        var context = fixture.Build();
        Assert.Empty(context.Assess(fixture.Key, fixture.Scope("A"), fixture.To).Reasons);
        Assert.Equal(1, context.Misses(fixture.Scope("A"), [fixture.Key], fixture.From, fixture.To).ActedMisses);
        Assert.NotEmpty(context.Assess(fixture.Key, fixture.Scope("A"), fixture.To.AddDays(2)).Reasons);
    }

    internal sealed class Fixture
    {
        internal readonly Guid AggregateId = Guid.NewGuid();
        internal readonly List<CodeInsightHumanConcern> Concerns = [];
        internal readonly List<CodeInsightModelConcernEvidence> Decisions = [];

        internal ModelReviewEvidence Proof = ModelOpportunityEvaluatorTests.Proof(
        [
            ModelOpportunityEvaluatorTests.Pass("A"),
            ModelOpportunityEvaluatorTests.Pass("B") with { Candidates = [new("B-X", "a.cs", 12, "X concern")] }
        ]);

        internal readonly DateOnly From = new(2026, 10, 9), To = new(2026, 10, 10);
        internal ReviewerPerformanceSourceKey Key => new(this.AggregateId, ModelOpportunityEvaluatorTests.Source.ProviderScope);

        internal ReviewerPerformanceViewQuery Scope(params string[] models) => new()
        {
            From = this.From, To = this.To, Models =
                models.Select(model => ReviewerPerformanceRangeReader.ModelKey(new() { ModelId = model, LogicalModelName = "logical-" + model })).ToArray()
        };

        internal CodeInsightHumanConcern Concern(string name, bool detectedByB)
        {
            var concern = new CodeInsightHumanConcern
            {
                Id = Guid.NewGuid(), CodeInsightPullRequestId = this.AggregateId, ProviderScope = this.Key.ProviderScope,
                ConnectionId = ModelOpportunityEvaluatorTests.Source.ConnectionId!.Value, Anchor = ModelOpportunityEvaluatorTests.Anchor,
                HarvestedAt = ModelOpportunityEvaluatorTests.Origin.AddMinutes(1), ObservedAt = ModelOpportunityEvaluatorTests.Origin.AddHours(1),
                SourceFingerprint = name, IsSubstantive = true, IsInScope = true, WasActedOn = true, IsSingleConcern = true,
                IsHumanConfirmed = true, ThreadResolved = true, TypeMembership = "bug", Qualifier = CodeInsightFindingQualifier.Incorrect,
                DimensionClassifierVersion = "type"
            };
            concern.CanonicalConcernId = concern.Id;
            this.Concerns.Add(concern);
            foreach (var model in new[] { "A", "B" })
            {
                var assessment = ModelOpportunityEvaluator.Assess(
                    ModelOpportunityEvaluatorTests.Snapshot(this.Proof), ModelOpportunityEvaluatorTests.Source,
                    concern.Anchor, model, "logical-" + model);
                this.Decisions.Add(
                    new()
                    {
                        Id = Guid.NewGuid(), HumanConcernId = concern.Id, ModelId = model, LogicalModelName = "logical-" + model,
                        SourceFingerprint = name, CandidateSetFingerprint = assessment.Fingerprint, ClassifierVersion = "match",
                        Decision = model == "B" && detectedByB ? "detected" : "missed", CandidateIds = model == "B" && detectedByB ? ["B-X"] : [],
                        ObservedAt = concern.ObservedAt
                    });
            }

            return concern;
        }

        internal ReviewerPerformanceModelEvidence Build(
            IReadOnlyList<ReviewerPerformanceDailyCount>? publications = null, ModelEvidenceEvaluationBudget? budget = null) => new(
            ModelOpportunityEvaluatorTests.Snapshot(this.Proof),
            new Dictionary<Guid, CodeInsightPullRequest>
            {
                [this.AggregateId] = new()
                {
                    Id = this.AggregateId,
                    ClientId = ModelOpportunityEvaluatorTests.Source.ClientId, RepositoryId = "repo", PullRequestId = 42
                }
            }, this.Concerns, this.Decisions,
            [
                new()
                {
                    Id = Guid.NewGuid(), CodeInsightPullRequestId = this.AggregateId, ProviderScope = this.Key.ProviderScope,
                    ModelConcernEnumerationComplete = true, ModelConcernObservedAt = ModelOpportunityEvaluatorTests.Origin.AddHours(1),
                    ModelConcernConnectionId = ModelOpportunityEvaluatorTests.Source.ConnectionId
                }
            ], publications, budget);

        internal ReviewerPerformanceViewSnapshot Snapshot(IReadOnlyList<ReviewerPerformanceDailyCount>? publications = null)
        {
            var aggregate = new CodeInsightPullRequest
                { Id = this.AggregateId, ClientId = ModelOpportunityEvaluatorTests.Source.ClientId, RepositoryId = "repo", PullRequestId = 42 };
            var rows = new List<ReviewerPerformanceDailyCount>
            {
                new()
                {
                    CodeInsightPullRequestId = aggregate.Id, ClientId = aggregate.ClientId, RepositoryId = "repo", PullRequestId = 42,
                    ProviderScope = this.Key.ProviderScope,
                    BucketDate = this.To, Outcome = "collectionComplete", Count = 1
                },
                new()
                {
                    CodeInsightPullRequestId = aggregate.Id, ClientId = aggregate.ClientId, RepositoryId = "repo", PullRequestId = 42,
                    ProviderScope = this.Key.ProviderScope,
                    BucketDate = this.To, Outcome = "missActed", IsMiss = true, TypeMembership = "bug", Qualifier = "Incorrect", IsClassified = true, Count = 7
                },
            };
            rows.AddRange(publications ?? []);
            var context = this.Build(rows);
            rows.AddRange(context.Markers(this.From, this.To));
            return new(
                rows, null, 0, new Dictionary<Guid, string>(), new Dictionary<(Guid, string), string?>(),
                new Dictionary<Guid, CodeInsightPullRequest> { [aggregate.Id] = aggregate }, new Dictionary<Guid, bool>(), ModelEvidence: context);
        }
    }
}
