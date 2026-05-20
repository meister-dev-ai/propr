// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class ReviewPromptsTemplateBackedPrWideTests
{
    [Fact]
    public void BuildPrWidePlanningSystemPrompt_UsesTemplateBackedDefault()
    {
        var prompt = ReviewPrompts.BuildPrWidePlanningSystemPrompt(null);

        Assert.Contains("Stage A of a PR-wide agentic review workflow", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("investigation_tasks", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrWidePlanningUserMessage_UsesTemplateBackedDefault()
    {
        var pr = CreatePullRequest();

        var message = ReviewPrompts.BuildPrWidePlanningUserMessage(pr);

        Assert.Contains("Changed file manifest:", message, StringComparison.Ordinal);
        Assert.Contains("Diff excerpts:", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrWideInvestigationSystemPrompt_UsesTemplateBackedDefault()
    {
        var prompt = ReviewPrompts.BuildPrWideInvestigationSystemPrompt(null);

        Assert.Contains("Do not expand scope beyond the assigned seed files", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("candidate_findings", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrWideInvestigationUserMessage_UsesTemplateBackedDefault()
    {
        var plan = new PrWideReviewPlan(
            "plan-001", ["Check DI"], ["src/Foo.cs"],
            [new PrWideInvestigationTask("task-001", "concern", "Check DI", ["src/Foo.cs"], ["get_file_content"], 1)]);
        var task = plan.InvestigationTasks[0];
        var pr = CreatePullRequest();

        var message = ReviewPrompts.BuildPrWideInvestigationUserMessage(plan, task, pr);

        Assert.Contains("Task ID: task-001", message, StringComparison.Ordinal);
        Assert.Contains("Seed files:", message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrWideSynthesisSystemPrompt_UsesTemplateBackedDefault()
    {
        var prompt = ReviewPrompts.BuildPrWideSynthesisSystemPrompt(null);

        Assert.Contains("Stage C of a PR-wide agentic review workflow", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("candidate_summary_text", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPrWideSynthesisUserMessage_UsesTemplateBackedDefault()
    {
        var plan = new PrWideReviewPlan("plan-001", ["Check DI"], ["src/Foo.cs"], []);
        var investigations = new List<PrWideInvestigationResult>
        {
            new(
                "task-001",
                "completed",
                [new EvidenceItem("file_content", "Fetched Program.cs", "src/Program.cs")],
                [
                    new PrWideCandidateFinding(
                        "candidate-001", "Missing test coverage.", CandidateReviewFinding.CrossCuttingCategory, new ConfidenceScore("tests", 82),
                        new EvidenceReference([], ["src/Program.cs"], EvidenceReference.ResolvedState, "pr_wide_synthesis"), ["src/Program.cs"]),
                ],
                [],
                false),
        }.AsReadOnly();

        var message = ReviewPrompts.BuildPrWideSynthesisUserMessage(plan, investigations);

        Assert.Contains("Investigation outputs:", message, StringComparison.Ordinal);
        Assert.Contains("candidate-001", message, StringComparison.Ordinal);
    }

    private static PullRequest CreatePullRequest()
    {
        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            42,
            7,
            "Refactor registration",
            "Touches startup and tests.",
            "feature/foo",
            "main",
            [
                new ChangedFile("src/Foo.cs", ChangeType.Edit, "code", "+services.AddFoo();"),
                new ChangedFile("tests/FooTests.cs", ChangeType.Add, "code", "+[Fact]"),
            ]);
    }
}
