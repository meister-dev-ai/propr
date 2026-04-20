// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Tests for the per-file and synthesis prompt builders added in feature 015.
/// </summary>
public class ReviewPromptsTests
{
    private static ChangedFile CreateFile(string path, string content = "code", string diff = "+code")
    {
        return new ChangedFile(path, ChangeType.Edit, content, diff);
    }

    private static IReadOnlyList<ChangedFileSummary> AsSummaries(params ChangedFile[] files)
    {
        return files.Select(f => new ChangedFileSummary(f.Path, f.ChangeType)).ToList().AsReadOnly();
    }

    private static PrCommentThread CreateThread(string? filePath)
    {
        return new PrCommentThread(
            1,
            filePath,
            filePath is null ? null : 1,
            new List<PrThreadComment>
            {
                new("Author", "comment text", Guid.NewGuid()),
            }.AsReadOnly());
    }

    // T035 — Per-file user message contains file-under-review header
    [Fact]
    public void BuildPerFileUserMessage_ContainsFileHeader()
    {
        var file = CreateFile("src/Foo.cs");
        var allFiles = AsSummaries(file, CreateFile("src/Bar.cs"));

        var msg = ReviewPrompts.BuildPerFileUserMessage(file, 1, 2, allFiles, [], "My PR", "feature/x", "main");

        Assert.Contains("Reviewing file 1 of 2: src/Foo.cs", msg);
        Assert.Contains("[CURRENT FILE]", msg);
    }

    // T035 — Per-file user message contains manifest with all changed files
    [Fact]
    public void BuildPerFileUserMessage_ContainsManifestForAllFiles()
    {
        var file = CreateFile("src/Foo.cs");
        var bar = CreateFile("src/Bar.cs");
        var allFiles = AsSummaries(file, bar);

        var msg = ReviewPrompts.BuildPerFileUserMessage(file, 1, 2, allFiles, [], "My PR", "feature/x", "main");

        Assert.Contains("src/Foo.cs", msg);
        Assert.Contains("src/Bar.cs", msg);
        Assert.Contains("use get_file_content", msg);
    }

    // T035 — Per-file user message only includes filtered threads
    [Fact]
    public void BuildPerFileUserMessage_OnlyRendersFilteredThreads()
    {
        var file = CreateFile("src/Foo.cs");
        var allFiles = AsSummaries(file);

        var fooThread = CreateThread("src/Foo.cs");
        var barThread = CreateThread("src/Bar.cs");
        var prThread = CreateThread(null);

        // Only fooThread and prThread should be in the message (barThread was pre-filtered out)
        var filteredThreads = new List<PrCommentThread> { fooThread, prThread }.AsReadOnly();

        var msg = ReviewPrompts.BuildPerFileUserMessage(
            file,
            1,
            1,
            allFiles,
            filteredThreads,
            "My PR",
            "feature/x",
            "main");

        Assert.Contains("src/Foo.cs", msg);
        Assert.Contains("(PR-level)", msg);
        // Verify bar thread is NOT present (it was filtered before calling the method)
        Assert.DoesNotContain("src/Bar.cs", msg.Split("--- FILE UNDER REVIEW ---")[0]);
    }

    // T035 — Per-file system prompt contains file framing instruction
    [Fact]
    public void BuildPerFileSystemPrompt_ContainsFileFramingInstruction()
    {
        var prompt = ReviewPrompts.BuildPerFileSystemPrompt(null, "src/Foo.cs", 2, 5);

        Assert.Contains("src/Foo.cs", prompt);
        Assert.Contains("2 of 5", prompt);
        Assert.Contains("get_file_content", prompt);
    }

    // T035 — Synthesis system prompt mentions no tool calls
    [Fact]
    public void BuildSynthesisSystemPrompt_ContainsNoToolsInstruction()
    {
        var prompt = ReviewPrompts.BuildSynthesisSystemPrompt(null);

        Assert.Contains("Do not call any tools", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("plain text", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // T035 — Synthesis user message contains all file summary headers
    [Fact]
    public void BuildSynthesisUserMessage_ContainsAllFileSummaryHeaders()
    {
        var summaries = new List<(string FilePath, string Summary)>
        {
            ("src/Foo.cs", "Found 2 issues"),
            ("src/Bar.cs", "Looks good"),
        }.AsReadOnly();

        var msg = ReviewPrompts.BuildSynthesisUserMessage(summaries, "My PR", null);

        Assert.Contains("## src/Foo.cs", msg);
        Assert.Contains("## src/Bar.cs", msg);
        Assert.Contains("Found 2 issues", msg);
        Assert.Contains("Looks good", msg);
    }

    // T035 — Synthesis user message includes PR title and description
    [Fact]
    public void BuildSynthesisUserMessage_IncludesPrTitleAndDescription()
    {
        var summaries = new List<(string FilePath, string Summary)>
        {
            ("src/Foo.cs", "Summary"),
        }.AsReadOnly();

        var msg = ReviewPrompts.BuildSynthesisUserMessage(summaries, "PR Title", "PR Description text");

        Assert.Contains("PR Title", msg);
        Assert.Contains("PR Description text", msg);
    }

    // T005 — Per-file user message does NOT include full file content block
    [Fact]
    public void BuildPerFileUserMessage_DoesNotContainFullContentBlock()
    {
        var file = CreateFile("src/Foo.cs", "UNIQUE_FILE_CONTENT_MARKER");
        var allFiles = AsSummaries(file);

        var msg = ReviewPrompts.BuildPerFileUserMessage(file, 1, 1, allFiles, [], "My PR", "feature/x", "main");

        Assert.DoesNotContain("--- FULL CONTENT ---", msg);
        Assert.DoesNotContain("UNIQUE_FILE_CONTENT_MARKER", msg);
    }

    // T005 — Per-file user message DOES include the diff
    [Fact]
    public void BuildPerFileUserMessage_ContainsDiff()
    {
        var file = CreateFile("src/Foo.cs", "code", "+UNIQUE_DIFF_MARKER");
        var allFiles = AsSummaries(file);

        var msg = ReviewPrompts.BuildPerFileUserMessage(file, 1, 1, allFiles, [], "My PR", "feature/x", "main");

        Assert.Contains("--- DIFF ---", msg);
        Assert.Contains("+UNIQUE_DIFF_MARKER", msg);
    }

    // T005 — Per-file user message includes fallback note about get_file_content
    [Fact]
    public void BuildPerFileUserMessage_ContainsFallbackNote()
    {
        var file = CreateFile("src/Foo.cs");
        var allFiles = AsSummaries(file);

        var msg = ReviewPrompts.BuildPerFileUserMessage(file, 1, 1, allFiles, [], "My PR", "feature/x", "main");

        Assert.Contains("get_file_content", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("diff is insufficient", msg, StringComparison.OrdinalIgnoreCase);
    }

    // T018 — BuildGlobalSystemPrompt returns identical content for identical context
    [Fact]
    public void BuildGlobalSystemPrompt_IdenticalContextProducesIdenticalOutput()
    {
        var context = new ReviewSystemContext("Custom message", [], null);

        var prompt1 = ReviewPrompts.BuildGlobalSystemPrompt(context);
        var prompt2 = ReviewPrompts.BuildGlobalSystemPrompt(context);

        Assert.Equal(prompt1, prompt2);
    }

    // T018 — BuildPerFileContextPrompt varies only by file identity
    [Fact]
    public void BuildPerFileContextPrompt_VariesByFilePath()
    {
        var prompt1 = ReviewPrompts.BuildPerFileContextPrompt(null, "src/Foo.cs", 1, 3);
        var prompt2 = ReviewPrompts.BuildPerFileContextPrompt(null, "src/Bar.cs", 2, 3);

        Assert.Contains("src/Foo.cs", prompt1);
        Assert.Contains("src/Bar.cs", prompt2);
        Assert.DoesNotContain("src/Bar.cs", prompt1);
        Assert.DoesNotContain("src/Foo.cs", prompt2);
    }

    // T018 — BuildGlobalSystemPrompt does NOT contain per-file framing
    [Fact]
    public void BuildGlobalSystemPrompt_DoesNotContainPerFileFraming()
    {
        var context = new ReviewSystemContext(null, [], null);
        var prompt = ReviewPrompts.BuildGlobalSystemPrompt(context);

        Assert.DoesNotContain("You are reviewing **", prompt);
    }

    // T018 — BuildPerFileContextPrompt does NOT contain client/repo instructions
    [Fact]
    public void BuildPerFileContextPrompt_DoesNotContainContextDependentContent()
    {
        var prompt = ReviewPrompts.BuildPerFileContextPrompt(null, "src/Foo.cs", 1, 2);

        Assert.DoesNotContain("Client Instructions", prompt);
        Assert.DoesNotContain("Repository Instructions", prompt);
    }

    // T019 — BuildPerFileContextPrompt includes CRITICAL OUTPUT RULE reminder with required keys
    [Fact]
    public void BuildPerFileContextPrompt_ContainsCriticalOutputRuleReminder()
    {
        var prompt = ReviewPrompts.BuildPerFileContextPrompt(null, "src/Foo.cs", 1, 3);

        Assert.Contains("CRITICAL OUTPUT RULE", prompt);
        Assert.Contains(ReviewPrompts.OutputKeyReminder, prompt);
        Assert.Contains("\"comments\" (array)", prompt);
        Assert.Contains("\"summary\" (string)", prompt);
        Assert.Contains("\"confidence_evaluations\" (array)", prompt);
        Assert.Contains("\"investigation_complete\" (bool)", prompt);
        Assert.Contains("\"loop_complete\" (bool)", prompt);
    }

    // T019 — BuildPerFileContextPrompt lists forbidden key names
    [Fact]
    public void BuildPerFileContextPrompt_ListsForbiddenKeyNames()
    {
        var prompt = ReviewPrompts.BuildPerFileContextPrompt(null, "src/Foo.cs", 1, 3);

        Assert.Contains("key_issues", prompt);
        Assert.Contains("verdict", prompt);
        Assert.Contains("suggested_changes", prompt);
    }

    // T019 — BuildPerFileContextPrompt includes mandatory investigation block for multi-file PRs
    [Fact]
    public void BuildPerFileContextPrompt_MultiFile_ContainsMandatoryInvestigationBlock()
    {
        var prompt = ReviewPrompts.BuildPerFileContextPrompt(null, "src/Foo.cs", 1, 3);

        Assert.Contains("Mandatory investigation requirement", prompt);
        Assert.Contains("investigation_complete", prompt);
    }

    // T019 — BuildPerFileContextPrompt omits mandatory investigation block for single-file PRs
    [Fact]
    public void BuildPerFileContextPrompt_SingleFile_OmitsMandatoryInvestigationBlock()
    {
        var prompt = ReviewPrompts.BuildPerFileContextPrompt(null, "src/Only.cs", 1, 1);

        Assert.DoesNotContain("Mandatory investigation requirement", prompt);
        Assert.Contains("only file changed", prompt);
    }

    // T012(a) — US2: AgenticLoopGuidance prohibits speculative/conditional-language findings
    //            (updated for T005 rewrite: "conditional language" → CERTAINTY GATE speculative-phrase list)
    [Fact]
    public void AgenticLoopGuidance_ContainsConditionalLanguageProhibition()
    {
        // The CERTAINTY GATE section prohibits speculative language — spot-check one canonical phrase
        Assert.Contains(
            "Omission is always preferable to speculation",
            ReviewPrompts.AgenticLoopGuidance,
            StringComparison.OrdinalIgnoreCase);
    }

    // T012(b) — US2: AgenticLoopGuidance instructs get_file_tree for config files
    [Fact]
    public void AgenticLoopGuidance_ContainsGetFileTreeForConfigFiles()
    {
        Assert.Contains("get_file_tree", ReviewPrompts.AgenticLoopGuidance, StringComparison.OrdinalIgnoreCase);
        var guidance = ReviewPrompts.AgenticLoopGuidance;
        Assert.True(
            guidance.Contains("tsconfig", StringComparison.OrdinalIgnoreCase) ||
            guidance.Contains("vite.config", StringComparison.OrdinalIgnoreCase) ||
            guidance.Contains("docker-compose", StringComparison.OrdinalIgnoreCase),
            "AgenticLoopGuidance must list canonical config file types for get_file_tree instruction.");
    }

    // T012(c) — US2: BuildPerFileContextPrompt strengthened
    [Fact]
    public void BuildPerFileContextPrompt_RequiresEveryConditionallyReferencedManifestFile()
    {
        var prompt = ReviewPrompts.BuildPerFileContextPrompt(null, "src/Foo.cs", 1, 3);
        Assert.DoesNotContain("at least one related file", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            prompt.Contains("every manifest file", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("conditionally", StringComparison.OrdinalIgnoreCase),
            "BuildPerFileContextPrompt must reference every conditionally-cited manifest file.");
    }

    // T017 — AgenticLoopGuidance contains the suggestion-block fence marker
    [Fact]
    public void AgenticLoopGuidance_ContainsSuggestionFenceMarker()
    {
        Assert.Contains("```suggestion", ReviewPrompts.AgenticLoopGuidance);
    }

    // T017 — AgenticLoopGuidance restricts suggestion blocks to single-file, single-hunk, code-only changes
    [Fact]
    public void AgenticLoopGuidance_ContainsSuggestionBlockRestrictions()
    {
        var guidance = ReviewPrompts.AgenticLoopGuidance;
        Assert.Contains("single", guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hunk", guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("multi-file", guidance, StringComparison.OrdinalIgnoreCase);
    }

    // PR64-5466/5467 — SystemPrompt schema must declare all five required output keys
    [Fact]
    public void SystemPrompt_SchemaDeclaresFiveRequiredOutputKeys()
    {
        var prompt = ReviewPrompts.SystemPrompt;

        Assert.Contains("\"summary\"", prompt);
        Assert.Contains("\"comments\"", prompt);
        Assert.Contains("\"confidence_evaluations\"", prompt);
        Assert.Contains("\"investigation_complete\"", prompt);
        Assert.Contains("\"loop_complete\"", prompt);
    }

    // PR64-5467 — SystemPrompt schema must not list 'info' as a valid comment severity
    [Fact]
    public void SystemPrompt_SchemaExcludesInfoSeverityFromComments()
    {
        // Extract just the schema block so we don't false-positive on explanatory text
        var prompt = ReviewPrompts.SystemPrompt;
        var schemaStart = prompt.IndexOf("Schema:", StringComparison.Ordinal);
        var schemaSection = schemaStart >= 0 ? prompt[schemaStart..] : prompt;

        // 'info' must not appear as a permitted severity value inside the schema
        Assert.DoesNotContain("\"info\"", schemaSection);
    }

    // PR64-5468 — BuildSynthesisSystemPrompt JSON mode must describe JSON output, not plain text
    [Fact]
    public void BuildSynthesisSystemPrompt_JsonMode_DescribesJsonOutput()
    {
        var prompt = ReviewPrompts.BuildSynthesisSystemPrompt(null, true);

        Assert.Contains("Do not call any tools", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plain text", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cross_cutting_concerns", prompt);
        Assert.Contains("\"summary\"", prompt);
    }

    // PR64-5468 — BuildSynthesisSystemPrompt plain-text mode must not describe JSON schema
    [Fact]
    public void BuildSynthesisSystemPrompt_PlainTextMode_DoesNotDescribeJson()
    {
        var prompt = ReviewPrompts.BuildSynthesisSystemPrompt(null);

        Assert.Contains("plain text", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cross_cutting_concerns", prompt);
    }

    // PromptOverrides — BuildSynthesisSystemPrompt uses override when present in context
    [Fact]
    public void BuildSynthesisSystemPrompt_WithOverride_ReturnsOverrideText()
    {
        var overrides = new Dictionary<string, string> { ["SynthesisSystemPrompt"] = "Custom synthesis override" };
        var context = new ReviewSystemContext(null, [], null) { PromptOverrides = overrides };

        var prompt = ReviewPrompts.BuildSynthesisSystemPrompt(context);

        Assert.Equal("Custom synthesis override", prompt);
    }

    // PromptOverrides — BuildQualityFilterSystemPrompt uses override when present in context
    [Fact]
    public void BuildQualityFilterSystemPrompt_WithOverride_ReturnsOverrideText()
    {
        var overrides = new Dictionary<string, string> { ["QualityFilterSystemPrompt"] = "Custom quality filter" };
        var context = new ReviewSystemContext(null, [], null) { PromptOverrides = overrides };

        var prompt = ReviewPrompts.BuildQualityFilterSystemPrompt(context);

        Assert.Equal("Custom quality filter", prompt);
    }

    // PromptOverrides — BuildQualityFilterSystemPrompt without override returns default rules text
    [Fact]
    public void BuildQualityFilterSystemPrompt_WithoutOverride_ReturnsDefaultText()
    {
        var prompt = ReviewPrompts.BuildQualityFilterSystemPrompt(null);

        Assert.Contains("senior code review editor", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DISCARD", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // PromptOverrides — BuildSystemPrompt applies SystemPrompt override from context
    [Fact]
    public void BuildSystemPrompt_WithSystemPromptOverride_UsesOverride()
    {
        var overrides = new Dictionary<string, string> { ["SystemPrompt"] = "Overridden system persona" };
        var context = new ReviewSystemContext(null, [], null) { PromptOverrides = overrides };

        var prompt = ReviewPrompts.BuildSystemPrompt(context);

        Assert.Contains("Overridden system persona", prompt);
        Assert.DoesNotContain(ReviewPrompts.SystemPrompt, prompt);
    }

    // PromptOverrides — BuildSystemPrompt applies AgenticLoopGuidance override from context
    [Fact]
    public void BuildSystemPrompt_WithAgenticLoopGuidanceOverride_UsesOverride()
    {
        var overrides = new Dictionary<string, string> { ["AgenticLoopGuidance"] = "Overridden agentic guidance" };
        var context = new ReviewSystemContext(null, [], null) { PromptOverrides = overrides };

        var prompt = ReviewPrompts.BuildSystemPrompt(context);

        Assert.Contains("Overridden agentic guidance", prompt);
        Assert.DoesNotContain(ReviewPrompts.AgenticLoopGuidance, prompt);
    }

    // PromptOverrides — BuildPerFileContextPrompt uses override when present in context
    [Fact]
    public void BuildPerFileContextPrompt_WithOverride_ReturnsOverrideText()
    {
        var overrides = new Dictionary<string, string> { ["PerFileContextPrompt"] = "Custom per-file instructions" };
        var context = new ReviewSystemContext(null, [], null) { PromptOverrides = overrides };

        var prompt = ReviewPrompts.BuildPerFileContextPrompt(context, "src/Foo.cs", 1, 3);

        Assert.Equal("Custom per-file instructions", prompt);
    }
}
