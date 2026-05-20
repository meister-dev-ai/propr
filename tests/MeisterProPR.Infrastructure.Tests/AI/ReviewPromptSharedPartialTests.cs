// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class ReviewPromptSharedPartialTests
{
    [Fact]
    public void PlanningAndInvestigationUserTemplates_ReferenceSharedFileManifestPartial()
    {
        var provider = new PromptTemplateFileProvider(AppContext.BaseDirectory);

        var agenticPlanning = provider.ReadStageTemplate(PromptStageKeys.AgenticFilePlanningUser);
        var prWidePlanning = provider.ReadStageTemplate("pr_wide_planning_user");
        var agenticInvestigation = provider.ReadStageTemplate(PromptStageKeys.AgenticFileInvestigationUser);
        var prWideInvestigation = provider.ReadStageTemplate("pr_wide_investigation_user");

        Assert.Contains("{{> file-manifest", agenticPlanning, StringComparison.Ordinal);
        Assert.Contains("{{> file-manifest", prWidePlanning, StringComparison.Ordinal);
        Assert.Contains("{{> file-manifest", agenticInvestigation, StringComparison.Ordinal);
        Assert.Contains("{{> file-manifest", prWideInvestigation, StringComparison.Ordinal);
    }

    [Fact]
    public void PerFileAndPrWideTemplates_ReferenceSharedInvestigationResultsPartial()
    {
        var provider = new PromptTemplateFileProvider(AppContext.BaseDirectory);

        var perFileContext = provider.ReadStageTemplate(PromptStageKeys.PerFileContextSystem);
        var prWideSynthesis = provider.ReadStageTemplate("pr_wide_synthesis_user");

        Assert.Contains("{{> investigation-results", perFileContext, StringComparison.Ordinal);
        Assert.Contains("{{> investigation-results", prWideSynthesis, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalAndPerFileTemplates_ReferenceSharedInstructionPartials()
    {
        var provider = new PromptTemplateFileProvider(AppContext.BaseDirectory);

        var globalSystem = provider.ReadStageTemplate(PromptStageKeys.GlobalSystem);
        var perFileContext = provider.ReadStageTemplate(PromptStageKeys.PerFileContextSystem);
        var perFileUser = provider.ReadStageTemplate(PromptStageKeys.PerFileUser);

        Assert.Contains("{{> system-prompt", globalSystem, StringComparison.Ordinal);
        Assert.Contains("{{> client-instructions", globalSystem, StringComparison.Ordinal);
        Assert.Contains("{{> repository-instructions", globalSystem, StringComparison.Ordinal);
        Assert.Contains("{{> dismissed-patterns", globalSystem, StringComparison.Ordinal);
        Assert.Contains("{{> output-key-reminder", perFileContext, StringComparison.Ordinal);
        Assert.Contains("{{> existing-threads", perFileUser, StringComparison.Ordinal);
    }
}
