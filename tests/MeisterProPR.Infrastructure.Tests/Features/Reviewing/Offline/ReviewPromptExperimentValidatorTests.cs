// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Infrastructure.Features.Reviewing.Offline;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Offline;

public sealed class ReviewPromptExperimentValidatorTests
{
    [Fact]
    public async Task ValidateAsync_WhenStageKeyUnknown_ThrowsInvalidOperationException()
    {
        var batch = new PromptExperimentBatch(
            "batch-001",
            "fixture-001",
            null,
            "config-a",
            [
                new PromptExperimentRunRequest(
                    "run-variant",
                    "variant-a",
                    "artifacts/variant-a.json",
                    [new StagePromptVariant("unknown_stage", PromptStageRole.User, PromptCompositionMode.Replace, "text")]),
            ]);

        var sut = new ReviewPromptExperimentValidator();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ValidateAsync(batch, CancellationToken.None));

        Assert.Contains("unknown_stage", ex.Message);
    }

    [Fact]
    public async Task ValidateAsync_WhenPromptRoleDoesNotMatchStageDefinition_ThrowsInvalidOperationException()
    {
        var batch = new PromptExperimentBatch(
            "batch-001",
            "fixture-001",
            null,
            "config-a",
            [
                new PromptExperimentRunRequest(
                    "run-variant",
                    "variant-a",
                    "artifacts/variant-a.json",
                    [new StagePromptVariant(PromptStageKeys.PerFileUser, PromptStageRole.System, PromptCompositionMode.Replace, "text")]),
            ]);

        var sut = new ReviewPromptExperimentValidator();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ValidateAsync(batch, CancellationToken.None));

        Assert.Contains(PromptStageKeys.PerFileUser, ex.Message);
        Assert.Contains("user", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_WhenRunContainsDuplicateStageRoleCombination_ThrowsInvalidOperationException()
    {
        var batch = new PromptExperimentBatch(
            "batch-001",
            "fixture-001",
            null,
            "config-a",
            [
                new PromptExperimentRunRequest(
                    "run-variant",
                    "variant-a",
                    "artifacts/variant-a.json",
                    [
                        new StagePromptVariant(PromptStageKeys.PerFileUser, PromptStageRole.User, PromptCompositionMode.Replace, "text-a"),
                        new StagePromptVariant(PromptStageKeys.PerFileUser, PromptStageRole.User, PromptCompositionMode.Append, "text-b"),
                    ]),
            ]);

        var sut = new ReviewPromptExperimentValidator();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ValidateAsync(batch, CancellationToken.None));

        Assert.Contains(PromptStageKeys.PerFileUser, ex.Message);
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_WithSupportedBatch_CompletesSuccessfully()
    {
        var batch = new PromptExperimentBatch(
            "batch-001",
            "fixture-001",
            null,
            "config-a",
            [
                new PromptExperimentRunRequest("run-baseline", "baseline", "artifacts/baseline.json"),
                new PromptExperimentRunRequest(
                    "run-variant",
                    "variant-a",
                    "artifacts/variant-a.json",
                    [new StagePromptVariant(PromptStageKeys.SynthesisSystem, PromptStageRole.System, PromptCompositionMode.Append, "extra synthesis rule")]),
            ]);

        var sut = new ReviewPromptExperimentValidator();

        await sut.ValidateAsync(batch, CancellationToken.None);
    }

    [Fact]
    public async Task ValidateAsync_WithPrWideStageVariant_CompletesSuccessfully()
    {
        var batch = new PromptExperimentBatch(
            "batch-001",
            "fixture-001",
            null,
            "config-a",
            [
                new PromptExperimentRunRequest(
                    "run-variant",
                    "variant-a",
                    "artifacts/variant-a.json",
                    [new StagePromptVariant("pr_wide_synthesis_user", PromptStageRole.User, PromptCompositionMode.Append, "extra pr-wide synthesis guidance")]),
            ]);

        var sut = new ReviewPromptExperimentValidator();

        await sut.ValidateAsync(batch, CancellationToken.None);
    }

    [Fact]
    public async Task ValidateAsync_WhenRunContainsDuplicateSkippedStep_ThrowsInvalidOperationException()
    {
        var batch = new PromptExperimentBatch(
            "batch-001",
            "fixture-001",
            null,
            "config-a",
            [
                new PromptExperimentRunRequest(
                    "run-variant",
                    "variant-a",
                    "artifacts/variant-a.json",
                    null,
                    null,
                    [FileByFileReviewStepIds.PrVerification, FileByFileReviewStepIds.PrVerification]),
            ]);

        var sut = new ReviewPromptExperimentValidator();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ValidateAsync(batch, CancellationToken.None));

        Assert.Contains(FileByFileReviewStepIds.PrVerification, ex.Message);
        Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
