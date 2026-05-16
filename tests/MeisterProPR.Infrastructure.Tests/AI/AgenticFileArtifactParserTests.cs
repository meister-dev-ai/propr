// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class AgenticFileArtifactParserTests
{
    [Fact]
    public void TryParsePlan_ParsesAnchorFileTasksAndNoInvestigationReason()
    {
        const string json = """
                            {
                              "plan_id": "plan-001",
                              "anchor_file_path": "src/Foo.cs",
                              "concerns": ["Check DI registration changes."],
                              "changed_areas": ["src/Foo.cs", "src/Bar.cs"],
                              "investigation_tasks": [
                                {
                                  "id": "task-001",
                                  "task_type": "concern",
                                  "concern": "Check DI registration changes.",
                                  "seed_file_paths": ["src/Foo.cs", "src/Bar.cs"],
                                  "allowed_tools": ["get_file_content", "get_file_tree"],
                                  "max_tool_calls": 2
                                }
                              ],
                              "no_investigation_reason": null
                            }
                            """;

        var parsed = AgenticFileArtifactParser.TryParsePlan(json, out var plan);

        Assert.True(parsed);
        Assert.NotNull(plan);
        Assert.Equal("plan-001", plan!.PlanId);
        Assert.Equal("src/Foo.cs", plan.AnchorFilePath);
        Assert.Single(plan.InvestigationTasks);
        Assert.Equal("task-001", plan.InvestigationTasks[0].TaskId);
        Assert.Contains("src/Bar.cs", plan.InvestigationTasks[0].SeedFilePaths);
    }

    [Fact]
    public void TryParseInvestigationResult_ParsesEvidenceCandidatesAndToolUsage()
    {
        const string json = """
                            {
                              "task_id": "task-001",
                              "status": "completed",
                              "evidence": [
                                { "kind": "file_content", "summary": "Captured sibling registration file.", "source_id": "src/Bar.cs" }
                              ],
                              "candidate_findings": [
                                {
                                  "id": "candidate-001",
                                  "message": "Registration updates in src/Foo.cs rely on sibling wiring in src/Bar.cs.",
                                  "category": "per_file_comment",
                                  "severity": "warning",
                                  "confidence": { "concern": "correctness", "score": 84 },
                                  "supporting_files": ["src/Foo.cs", "src/Bar.cs"],
                                  "file_path": "src/Foo.cs",
                                  "line_number": 12,
                                  "candidate_summary_text": "Registration wiring changed across related files."
                                }
                              ],
                              "tool_usage": [
                                { "tool_name": "get_file_content", "status": "success", "target": "src/Bar.cs" }
                              ],
                              "degraded": false
                            }
                            """;

        var task = new AgenticFileInvestigationTask(
            "task-001",
            "concern",
            "bounded_sibling_context",
            "Check DI registration changes.",
            ["src/Foo.cs", "src/Bar.cs"],
            ["get_file_content"],
            2);

        var parsed = AgenticFileArtifactParser.TryParseInvestigationResult(json, task, out var result);

        Assert.True(parsed);
        Assert.NotNull(result);
        Assert.Equal("task-001", result!.TaskId);
        Assert.Single(result.Evidence);
        Assert.Single(result.CandidateFindings);
        Assert.Equal(CommentSeverity.Warning, result.CandidateFindings[0].Severity);
        Assert.Equal("src/Foo.cs", result.CandidateFindings[0].FilePath);
        Assert.Equal(12, result.CandidateFindings[0].LineNumber);
        Assert.Single(result.ToolUsage);
        Assert.Equal("get_file_content", result.ToolUsage[0].ToolName);
    }

    [Fact]
    public void TryParseInvestigationResult_InvalidJson_ReturnsFalse()
    {
        var task = new AgenticFileInvestigationTask(
            "task-001",
            "concern",
            "bounded_sibling_context",
            "Check DI registration changes.",
            ["src/Foo.cs"],
            ["get_file_content"],
            1);

        var parsed = AgenticFileArtifactParser.TryParseInvestigationResult("not-json", task, out var result);

        Assert.False(parsed);
        Assert.Null(result);
    }
}
