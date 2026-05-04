// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Portable result artifact produced by one offline review execution.
/// </summary>
public sealed record EvaluationArtifact(
    EvaluationRunMetadata Run,
    EvaluationFixtureMetadata Fixture,
    EvaluationConfigurationMetadata Configuration,
    ReviewResult FinalResult,
    IReadOnlyList<StageEvidenceRecord> Stages,
    EvaluationTokenUsage TokenUsage,
    IReadOnlyList<string> Warnings,
    EvaluationVerificationResult? Verification = null);

/// <summary>
///     Run-level metadata for one artifact.
/// </summary>
public sealed record EvaluationRunMetadata(
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Outcome,
    string ProtectedResolutionStatus);

/// <summary>
///     Fixture identity metadata stored in the artifact.
/// </summary>
public sealed record EvaluationFixtureMetadata(
    string FixtureId,
    string FixtureVersion,
    string Provenance);

/// <summary>
///     Configuration identity metadata stored in the artifact.
/// </summary>
public sealed record EvaluationConfigurationMetadata(
    string ConfigurationId,
    string ModelId,
    string DetailMode);

/// <summary>
///     Structured verification outcome for comparing generated review output against fixture expectations.
/// </summary>
public sealed record EvaluationVerificationResult(
    string Status,
    string Summary,
    IReadOnlyList<EvaluationVerificationCheck> PositiveExamples,
    IReadOnlyList<EvaluationVerificationCheck> NegativeExamples,
    IReadOnlyList<EvaluationVerificationCheck> InfoExamples,
    IReadOnlyList<string> Notes);

/// <summary>
///     One expectation verification decision.
/// </summary>
public sealed record EvaluationVerificationCheck(
    string Key,
    string Description,
    bool Fulfilled,
    string Rationale);
