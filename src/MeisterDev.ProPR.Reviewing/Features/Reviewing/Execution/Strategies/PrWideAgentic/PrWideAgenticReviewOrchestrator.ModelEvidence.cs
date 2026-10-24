// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.PrWideAgentic;

public sealed partial class PrWideAgenticReviewOrchestrator
{
    private void AttachGenerationCapture(ReviewJob job, PullRequest pr, ReviewSystemContext context, int ordinal, bool shadow)
    {
        if (modelEvidenceStore is null)
        {
            return;
        }

        context.ModelGenerationObserver = async (stage, started, disposition, candidates, ct) =>
        {
            try
            {
                var previous = job.ModelEvidenceManifest;
                var proofs = (previous?.PullRequestPasses ?? []).ToDictionary(proof => proof.FilePath, StringComparer.Ordinal);
                foreach (var file in pr.ChangedFiles.DistinctBy(file => file.Path).Take(ModelReviewEvidence.MaximumPasses))
                {
                    var scope = ModelReviewCapture.Scope(job, file, context) with
                    {
                        InputComplete = false, UnavailableReason = "model-input-scope-unavailable",
                    };
                    var passContext = context.CloneForPass();
                    passContext.ActiveLens = "pr-wide-" + stage;
                    passContext.ModelEvidenceScope = scope;
                    var relevant = candidates.Where(candidate =>
                            candidate.FilePath == file.Path || candidate.RelatedFilePaths.Contains(file.Path, StringComparer.Ordinal))
                        .Select(candidate => new ReviewComment(candidate.FilePath ?? file.Path, candidate.LineNumber, candidate.Severity, candidate.Message)
                        {
                            RiskAssessment = candidate.RiskAssessment, FindingClassification = candidate.FindingClassification,
                            SuggestedFix = candidate.SuggestedFix
                        })
                        .ToArray();
                    var pass = ModelReviewCapture.Pass(passContext, ordinal, started, disposition, relevant, shadow, "pr-wide-generation");
                    var incoming = scope with { Passes = [pass] };
                    proofs[file.Path] = proofs.TryGetValue(file.Path, out var prior) ? prior.Merge(incoming) : incoming;
                }

                var manifest = new ModelReviewManifest
                {
                    JobId = job.Id, Attempt = job.RetryCount + 1,
                    LeaseGeneration = context.CapturedLeaseGeneration ?? (job.LeaseGeneration > 0 ? job.LeaseGeneration : null),
                    ProviderScope = context.ProviderScope ?? string.Empty, SourceConnectionId = context.SourceConnectionId,
                    Files = previous?.Files ?? pr.ChangedFiles.Select(file => file.Path).Distinct(StringComparer.Ordinal).Take(10_000).ToArray(),
                    InventoryComplete = previous?.InventoryComplete ?? pr.ChangedFiles.Count <= 10_000,
                    AllowedModels = previous?.AllowedModels ?? [], PullRequestPasses = proofs.Values.ToArray(), CapturedAt = DateTimeOffset.UtcNow,
                };
                if (!manifest.IsBounded)
                {
                    // Keep already retained independent passes when the next analytics capture exceeds its limit.
                    this._logger.LogWarning("Model generation capture limit exceeded for job {JobId}, stage {Stage}", job.Id, stage);
                    return;
                }

                job.RecordModelEvidenceManifest(manifest);
                await modelEvidenceStore.RecordModelManifestAsync(job.Id, job.RetryCount + 1, manifest, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                this._logger.LogWarning(
                    "Model generation retention unavailable for job {JobId}, stage {Stage}: {FailureType}", job.Id, stage, exception.GetType().Name);
            }
        };
    }

    private static Task ObserveGenerationAsync(
        ReviewSystemContext context, string stage, DateTimeOffset started,
        ModelGenerationDisposition disposition, IReadOnlyList<PrWideCandidateFinding> candidates, CancellationToken ct) =>
        context.ModelGenerationObserver?.Invoke(stage, started, disposition, candidates, ct) ?? Task.CompletedTask;

    private static bool IsCompleteCandidateArray(string? response, int parsedCount)
    {
        if (string.IsNullOrWhiteSpace(response) || response.Length > 1_000_000)
        {
            return false;
        }

        try
        {
            var text = response.Trim();
            if (text.StartsWith("```", StringComparison.Ordinal))
            {
                var firstLine = text.IndexOf('\n');
                text = firstLine >= 0 ? text[(firstLine + 1)..].TrimEnd('`', '\r', '\n', ' ') : string.Empty;
            }

            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("candidate_findings", out var candidates) && candidates.ValueKind == JsonValueKind.Array
                   && candidates.GetArrayLength() == parsedCount;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
