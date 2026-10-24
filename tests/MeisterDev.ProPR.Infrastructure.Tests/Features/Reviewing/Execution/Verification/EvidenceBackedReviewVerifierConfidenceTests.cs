// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed partial class EvidenceBackedReviewVerifierTests
{
    private const string AllocationPath = "tests/ReviewerPerformanceAllocationPostgresTests.cs";

    private const string AllocationClaim = "The loop condition is only smallest > budget, so once an early run exceeds the budget, "
                                           + "the helper continues all the way to five runs even when one run stays within budget and performs unnecessary database queries.";

    private const string AllocationSource = """
                                            var before = GC.GetTotalAllocatedBytes(true);
                                            var result = await query();
                                            var smallest = GC.GetTotalAllocatedBytes(true) - before;
                                            var runs = 1;
                                            while (smallest > budget && runs < 5)
                                            {
                                                before = GC.GetTotalAllocatedBytes(true);
                                                await query();
                                                smallest = Math.Min(smallest, GC.GetTotalAllocatedBytes(true) - before);
                                                runs++;
                                            }
                                            return (result, smallest, runs);
                                            """;

    [Theory]
    [InlineData("{\"verdict\":\"not_confirmed\"}")]
    [InlineData("{\"verdict\":\"not_confirmed\",\"reason\":\"The source refutes it.\"}")]
    public async Task PrimaryRefutation_WithoutSourceEvidenceRemainsSummaryOnly(string answer)
    {
        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(AllocationPath, 5)], [],
                new ReviewVerificationContext(AllocationTools(AllocationSource), "source", JudgeAnswering(answer, 10, 10), "local-replay")));

        Assert.Equal(FinalGateDecision.SummaryOnlyDisposition, outcome.RecommendedDisposition);
        Assert.NotEqual(VerificationOutcome.ContradictedKind, outcome.OutcomeKind);
        Assert.DoesNotContain("source_counterevidence_refutes_claim", outcome.ReasonCodes);
        Assert.Contains("grounded counterevidence", outcome.EvidenceSummary!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("smallest = Math.Min(smallest, GC.GetTotalAllocatedBytes(true) - before);", true)]
    [InlineData("An assignment that does not occur in the source.", false)]
    [InlineData("s", false)]
    public async Task PrimaryRefutation_RequiresGroundedCounterevidence(string quote, bool grounded)
    {
        var answer = ConfidenceVerdict(
            "not_confirmed", "low", quote, "Allocations [120,80] update smallest to 80.",
            "The loop exits after two runs.", "Math.Min incorporates the current allocation.");
        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(AllocationPath, 5)], [],
                new ReviewVerificationContext(AllocationTools(AllocationSource), "source", JudgeAnswering(answer, 10, 10), "local-replay")));

        Assert.Equal(grounded ? FinalGateDecision.DropDisposition : FinalGateDecision.SummaryOnlyDisposition, outcome.RecommendedDisposition);
        Assert.Equal(grounded, outcome.BlocksPublication);
        Assert.Equal(grounded, outcome.ReasonCodes.Contains("source_counterevidence_refutes_claim", StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("return Count();", "r", false)]
    [InlineData("return Count();", ";", false)]
    [InlineData("{}", "{}", false)]
    [InlineData("return Count();", "Count", false)]
    [InlineData("return Count();", "return Count()", false)]
    [InlineData("return Count();", "return Count();", true)]
    [InlineData("x();", "x();", true)]
    [InlineData("x();", "x", false)]
    [InlineData("    x();\r\nreturn 1;\r\n", "x();\nreturn 1;", true)]
    [InlineData("while (ready)\n{\n    x();\n}", "while (ready)\n{\n    x();\n}", true)]
    public async Task ConfidenceGrounding_RequiresCompleteSubstantiveSourceLines(string source, string quote, bool grounded)
    {
        var answer = ConfidenceVerdict(
            "confirmed", "high", quote, "The caller invokes this source path.", "The returned count reaches the caller.",
            "The source contains no guard excluding the call.");
        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(AllocationPath, 1)], [],
                new ReviewVerificationContext(AllocationTools(source), "source", JudgeAnswering(answer, 10, 10), "local-replay")));

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        Assert.Equal(grounded, outcome.ConfidenceEvidence!.SourceGrounded);
        Assert.Equal(grounded ? FindingConfidence.High : FindingConfidence.Unknown, ReviewFindingConfidence.Resolve(null, [outcome])!.Confidence);
    }

    [Theory]
    [InlineData("confirmed", "start", false)]
    [InlineData("not_confirmed", "start", false)]
    [InlineData("confirmed", "end", false)]
    [InlineData("not_confirmed", "end", false)]
    [InlineData("confirmed", "interior", true)]
    [InlineData("not_confirmed", "interior", true)]
    public async Task CharacterBoundedAnchor_GroundsOnlyCompleteOriginalSourceLines(string verdict, string boundary, bool grounded)
    {
        var source = boundary == "end"
            ? "//" + new string('x', 7897) + "\n" + new string('e', 1000) + ";"
            : new string('a', 300) + "\nreturn Count();\n" + new string('b', 3882) + "\n" + new string('c', 5000) + ";";
        var quote = boundary switch
        {
            "start" => new string('a', 100),
            "end" => new string('e', 100),
            _ => "return Count();",
        };
        var answer = ConfidenceVerdict(
            verdict, "high", quote, "The caller invokes this source path.", "The returned count reaches the caller.",
            "The cited source determines whether the claimed result can occur.");
        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(AllocationPath, boundary == "end" ? 1 : 4)], [],
                new ReviewVerificationContext(AllocationTools(source), "source", JudgeAnswering(answer, 10, 10), "local-replay")));

        Assert.Equal(grounded, outcome.ConfidenceEvidence!.SourceGrounded);
        Assert.Equal(
            verdict == "confirmed" ? FinalGateDecision.PublishDisposition
            : grounded ? FinalGateDecision.DropDisposition : FinalGateDecision.SummaryOnlyDisposition, outcome.RecommendedDisposition);
        if (verdict == "confirmed")
        {
            Assert.Equal(grounded ? FindingConfidence.High : FindingConfidence.Unknown, ReviewFindingConfidence.Resolve(null, [outcome])!.Confidence);
        }
    }

    [Fact]
    public async Task RecordedFalseConfirmation_IsRejectedBySourceGroundedContradictionCheck()
    {
        var calls = new List<(string System, string User)>();
        var count = 0;
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var messages = call.Arg<IEnumerable<ChatMessage>>().ToArray();
                calls.Add((messages[0].Text!, messages[1].Text!));
                return new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, ++count == 1
                            ? """{"verdict":"confirmed","finding_kind":"improvement","reason":"The loop continues while smallest > budget and ignores the current run's allocation."}"""
                            : ConfidenceVerdict(
                                "not_confirmed", "high", "smallest = Math.Min(smallest, GC.GetTotalAllocatedBytes(true) - before);",
                                "For budget 100 and allocations [120,80], smallest becomes 80 on the second run.",
                                "The next condition is false; the loop stops after two runs.",
                                "The Math.Min assignment incorporates the current allocation and disproves the claim.")));
            });
        var tools = AllocationTools(AllocationSource);
        var item = CreateWorkItem(AllocationPath, 5, claimText: AllocationClaim) with
        {
            RequiresCertaintyCheck = true,
            SuggestedFix = new ReviewSuggestedFix(5, 5, "while (smallest > budget && runs < 5)", "while (smallest > budget && runs < 5)"),
        };

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [item], [],
                new ReviewVerificationContext(tools, "source", judge, "local-replay")));

        Assert.Equal(VerificationOutcome.ContradictedKind, outcome.OutcomeKind);
        Assert.Equal(FinalGateDecision.DropDisposition, outcome.RecommendedDisposition);
        Assert.Equal("refuted", outcome.ContradictionCheckStatus);
        Assert.Equal(2, count);
        Assert.Contains("Math.Min", calls[0].User);
        Assert.Contains("replacementText", calls[0].User);
        Assert.DoesNotContain("RequiresCertaintyCheck", calls[0].User);
        Assert.Equal("local-replay", outcome.Runtime!.ModelId);
        Assert.Equal("ReviewLowEffort", outcome.ContradictionCheckRuntime!.Purpose);
        await ExportPromptsAsync("correct-update", calls, "not_confirmed");
    }

    [Theory]
    [InlineData("missing-update", "")]
    [InlineData("wrong-update", "smallest = Math.Max(smallest, GC.GetTotalAllocatedBytes(true) - before);")]
    public async Task GenuineAllocationDefects_RetainConfirmedSeverityAndIndependentlyCheckedConfidence(string caseId, string update)
    {
        var source = AllocationSource.Replace("smallest = Math.Min(smallest, GC.GetTotalAllocatedBytes(true) - before);", update, StringComparison.Ordinal);
        var quote = string.IsNullOrEmpty(update) ? "await query();\n    \n    runs++;" : update;
        // The surrounding source proves an absent update; its exact indentation is retained in the quoted excerpt.
        if (string.IsNullOrEmpty(update))
        {
            quote = source[source.IndexOf("    await query();", StringComparison.Ordinal)..source.IndexOf("    runs++;", StringComparison.Ordinal)] +
                    "    runs++;";
        }

        var answer = ConfidenceVerdict(
            "confirmed", "certain", quote, "For budget 100 and allocations [120,80], smallest stays 120.",
            "All five queries execute despite a run below budget.", "The loop increments runs but no update can lower smallest in this source.");
        var calls = new List<(string System, string User)>();
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var messages = call.Arg<IEnumerable<ChatMessage>>().ToArray();
                calls.Add((messages[0].Text!, messages[1].Text!));
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, answer));
            });
        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(AllocationPath, 5, claimText: AllocationClaim) with { RequiresCertaintyCheck = true }], [],
                new ReviewVerificationContext(AllocationTools(source), "source", judge, "local-replay")));
        var risk = new FindingRiskAssessment(
            FindingImpact.Low, FindingLikelihood.Likely, "Extra queries.", "First run over budget.",
            "review_model", FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Certain, "The producer proposed certainty.");

        var final = ReviewFindingConfidence.Resolve(risk, [outcome]);

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        Assert.Equal("defect", outcome.VerifiedFindingKind);
        Assert.Equal(FindingConfidence.Certain, final!.Confidence);
        Assert.Equal("review_model", final.Source);
        Assert.Equal(ReviewFindingConfidence.VerificationSource, final.ConfidenceSource);
        Assert.Equal(2, calls.Count);
        await ExportPromptsAsync(caseId, calls, "confirmed");
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("failure")]
    [InlineData("unassessed")]
    public async Task FailedAdditionalCheck_PreservesSupportedFindingWithoutCertainty(string check)
    {
        var count = 0;
        var answer = ConfidenceVerdict(
            "confirmed", "certain", "while (smallest > budget && runs < 5)",
            "Reachable trigger.", "Bounded consequence.", "Relevant source updates checked.");
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++count == 1)
                {
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
                }

                if (check == "failure")
                {
                    throw new InvalidOperationException("test provider failure");
                }

                return Task.FromResult(
                    new ChatResponse(
                        new ChatMessage(
                            ChatRole.Assistant, check == "malformed"
                                ? "{}"
                                : ConfidenceVerdict(
                                    "confirmed", "unknown", "while (smallest > budget && runs < 5)",
                                    "A bounded witness.", "A bounded consequence.", "The code was examined but confidence remains unassessed."))));
            });

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(AllocationPath, 5)], [], new ReviewVerificationContext(AllocationTools(AllocationSource), "source", judge, "local-replay")));
        var risk = new FindingRiskAssessment(
            FindingImpact.Low, FindingLikelihood.Likely, "Impact.", "Scenario.", "review_model",
            FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Certain, "Generated certainty.");

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        var resolved = ReviewFindingConfidence.Resolve(risk, [outcome]);
        Assert.Equal(FindingConfidence.High, resolved!.Confidence);
        Assert.StartsWith("Certainty is unestablished;", resolved.ConfidenceReason);
    }

    // Judge models sometimes end an answer whose last source quote ends in a brace with a stray quotation mark or brace.
    // The verdict and the evidence before that end are complete, so the finding is published with them.
    [Theory]
    [InlineData("text after the object", CommentSeverity.Suggestion)]
    [InlineData("quotation mark at the close", CommentSeverity.Suggestion)]
    [InlineData("surplus brace", CommentSeverity.Suggestion)]
    [InlineData("quotation mark at the close", CommentSeverity.Warning)]
    public async Task AConfirmationWithADamagedEnd_IsPublishedWithItsEvidence(string damage, CommentSeverity severity)
    {
        const string source = "while (ready)\n{\n    x();\n}";
        var answer = ConfidenceVerdict(
            "confirmed", "high", "while (ready)\n{", "The loop runs while ready is set.", "x runs once per iteration.",
            "No statement in the loop clears ready.");
        answer = damage switch
        {
            "text after the object" => answer + "\"}",
            "quotation mark at the close" => answer[..^1] + "\"}",
            _ => answer + "}",
        };

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(AllocationPath, 1, severity: severity)], [],
                new ReviewVerificationContext(AllocationTools(source), "source", JudgeAnswering(answer, 10, 10), "local-replay")));

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        Assert.Equal(EvidenceJudgeVerdicts.Confirmed, outcome.JudgeVerdict);
        Assert.True(outcome.ConfidenceEvidence!.SourceGrounded);
        Assert.Equal(FindingConfidence.High, ReviewFindingConfidence.Resolve(null, [outcome])!.Confidence);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"level\":\"certain\"}")]
    public async Task MalformedOptionalEvidence_DoesNotSuppressConfirmedFindingOrInheritCertainty(string evidence)
    {
        var answer = "{\"verdict\":\"confirmed\",\"reason\":\"A bounded supported claim.\",\"confidence_evidence\":" + evidence + "}";
        var judge = JudgeAnswering(answer, 10, 10);
        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(AllocationPath, 5)], [],
                new ReviewVerificationContext(AllocationTools(AllocationSource), "source", judge, "local-replay")));

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        Assert.Null(outcome.ConfidenceEvidence);
        var producerRisk = new FindingRiskAssessment(
            FindingImpact.High, FindingLikelihood.Possible, "Data loss.", "Reachable input.", "review_model",
            FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Certain, "The producer asserted certainty.");
        var resolved = ReviewFindingConfidence.Resolve(producerRisk, [outcome]);
        Assert.Equal(FindingConfidence.Unknown, resolved!.Confidence);
        Assert.Equal(ReviewFindingConfidence.VerificationSource, resolved.ConfidenceSource);
        Assert.Contains("no complete confidence evidence", resolved.ConfidenceReason!);
    }

    [Theory]
    [InlineData("witness", "maximum", true)]
    [InlineData("witness", "oversized", false)]
    [InlineData("consequence", "whitespace", false)]
    [InlineData("counterevidence", "empty", false)]
    [InlineData("assumptions", "maximum", true)]
    [InlineData("assumptions", "oversized", false)]
    [InlineData("assumptions", "whitespace", false)]
    public async Task EvidenceBounds_OnlyAffectConfidenceAndRetainSupportedFinding(string field, string size, bool accepted)
    {
        var value = size switch
        {
            "maximum" => new string('x', VerificationConfidenceEvidence.MaxTextLength),
            "oversized" => new string('x', VerificationConfidenceEvidence.MaxTextLength + 1),
            "whitespace" => " \t ",
            _ => string.Empty,
        };
        var answer = System.Text.Json.Nodes.JsonNode.Parse(
            ConfidenceVerdict("confirmed", "high", "while (smallest > budget && runs < 5)", "Reachable input.", "Bounded effect.", "Source updates checked."))!;
        answer["confidence_evidence"]![field] = field == "assumptions"
            ? JsonSerializer.SerializeToNode(new[] { value })
            : System.Text.Json.Nodes.JsonValue.Create(value);

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(AllocationPath, 5)], [],
                new ReviewVerificationContext(AllocationTools(AllocationSource), "source", JudgeAnswering(answer.ToJsonString(), 10, 10), "local-replay")));

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        Assert.Equal(accepted, outcome.ConfidenceEvidence is not null);
    }

    [Fact]
    public async Task PreparedOptionalInputs_KeepFreshBoundariesAndLiteralPatchDataForEachFinding()
    {
        const string Replacement = "return \"{{template}} <tag> | `code`\";\r\n// next line";
        var inputs = new System.Collections.Concurrent.ConcurrentBag<string>();
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var messages = call.Arg<IEnumerable<ChatMessage>>().ToArray();
            inputs.Add(messages[1].Text!);
            Assert.Contains($"at most {VerificationConfidenceEvidence.MaxTextLength} characters", messages[0].Text);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"verdict":"confirmed","reason":"Supported."}"""));
        });
        var items = Enumerable.Range(0, 2).Select(index => CreateWorkItem(AllocationPath, 5, claimId: $"claim-{index}") with
        {
            SuggestedFix = new ReviewSuggestedFix(5, 5, "original", Replacement),
            Challenge = new FindingVerificationChallenge($"finding-{index}", "Check {{value}} literally.", "Source counterevidence."),
        }).ToArray();

        var outcomes = await new EvidenceBackedReviewVerifier().VerifyAsync(
            items, [], new ReviewVerificationContext(AllocationTools(AllocationSource), "source", judge, "local-replay"));

        Assert.All(outcomes, outcome => Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition));
        var boundaries = new List<string>();
        foreach (var input in inputs)
        {
            const string Begin = "\n\nBEGIN OPTIONAL VERIFICATION DATA ";
            var start = input.IndexOf(Begin, StringComparison.Ordinal) + Begin.Length;
            var end = input.IndexOf('\n', start);
            var boundary = input[start..end];
            boundaries.Add(boundary);
            var closing = "\nEND OPTIONAL VERIFICATION DATA " + boundary;
            Assert.EndsWith(closing, input);
            using var data = JsonDocument.Parse(input[(end + 1)..input.LastIndexOf(closing, StringComparison.Ordinal)]);
            Assert.Equal(Replacement, data.RootElement.GetProperty("suggestedFix").GetProperty("replacementText").GetString());
            Assert.Equal("Check {{value}} literally.", data.RootElement.GetProperty("synthesisChallenge").GetProperty("Reason").GetString());
        }

        Assert.Equal(2, boundaries.Distinct(StringComparer.Ordinal).Count());
    }

    private static IReviewContextTools AllocationTools(string source)
    {
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(source);
        return tools;
    }

    [Theory]
    [InlineData("source", true, false)]
    [InlineData("other-revision", false, false)]
    [InlineData("source", false, true)]
    public async Task ConfidenceGrounding_UsesSuccessfulJudgeLookupsFromTheReviewedBranch(string branch, bool grounded, bool failed)
    {
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(AllocationPath, "source", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns("return Count();");
        tools.GetFileContentAsync("src/Other.cs", branch, 1, 5, Arg.Any<CancellationToken>())
            .Returns(_ => failed ? throw new InvalidOperationException("test source failure") : "public int Count() => -1;");
        var judge = Substitute.For<IChatClient>();
        var answer = ConfidenceVerdict(
            "confirmed", "high", failed ? "test source failure" : "public int Count() => -1;", "Count returns a negative value.",
            "The caller receives an invalid count.", "No guard excludes this call.").Replace(AllocationPath, "src/Other.cs", StringComparison.Ordinal);
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                ToolCallResponse(
                    "read-count", "get_file_content", JsonSerializer.Serialize(
                        new
                            { path = "src/Other.cs", branch, startLine = 1, endLine = 5 })), new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem(AllocationPath, 1)], [],
                new ReviewVerificationContext(tools, "source", judge, "local-replay")));

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.RecommendedDisposition);
        Assert.Equal(grounded, outcome.ConfidenceEvidence!.SourceGrounded);
        Assert.Equal(grounded ? FindingConfidence.High : FindingConfidence.Unknown, ReviewFindingConfidence.Resolve(null, [outcome])!.Confidence);
    }

    // The tool binder reads a numeric string as the line number, so the lookup's line labels start at that line whichever
    // JSON type carries it.
    [Theory]
    [InlineData("3")]
    [InlineData("\"3\"")]
    public async Task AQuoteFromALookup_IsGroundedWhicheverJsonTypeCarriesTheStartLine(string startLine)
    {
        const string callerPath = "src/Caller.cs";
        const string callerLines = "var key = Read();\nreturn lookup(key);";
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync("src/Lookup.cs", "source", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns("return lookup(key);");
        tools.GetFileContentAsync(callerPath, "source", 3, 4, Arg.Any<CancellationToken>()).Returns(callerLines);
        var responses = new Queue<ChatResponse>(
        [
            ToolCallResponse("caller", "get_file_content", $$"""{"path":"{{callerPath}}","branch":"source","startLine":{{startLine}},"endLine":4}"""),
            new ChatResponse(
                new ChatMessage(
                    ChatRole.Assistant,
                    ConfidenceVerdict(
                        "confirmed", "high", callerLines, "A missing key reaches lookup through this call.", "The caller receives a null result.",
                        "No guard before the call excludes a missing key.", callerPath))),
        ]);
        var judge = Substitute.For<IChatClient>();
        judge.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => responses.Dequeue());

        var outcome = Assert.Single(
            await new EvidenceBackedReviewVerifier().VerifyAsync(
                [CreateWorkItem("src/Lookup.cs", 1)], [], new ReviewVerificationContext(tools, "source", judge, "judge-model")));

        Assert.Equal(FindingConfidence.High, ReviewFindingConfidence.Resolve(null, [outcome])!.Confidence);
    }

    private static string ConfidenceVerdict(
        string verdict, string level, string quote, string witness, string consequence, string counterevidence, string filePath = AllocationPath) =>
        JsonSerializer.Serialize(
            new
            {
                verdict, reason = counterevidence, finding_kind = "defect",
                confidence_evidence = new
                {
                    level, witness, consequence, counterevidence, assumptions = Array.Empty<string>(), all_claims_covered = true,
                    source_quotes = new[] { new { file_path = filePath, quote } }
                },
            });

    private static async Task ExportPromptsAsync(string caseId, IReadOnlyList<(string System, string User)> calls, string expectedVerdict)
    {
        if (Environment.GetEnvironmentVariable("PROPR_VERIFICATION_PROMPT_EXPORT_DIRECTORY") is not { Length: > 0 } directory)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var prompts = calls.Select((call, index) => new
        {
            caseId = caseId + (index == 0 ? "-primary" : "-contradiction"),
            system = call.System, user = call.User, maxOutputTokens = 1400, expectedVerdict
        });
        await File.WriteAllTextAsync(
            Path.Combine(directory, caseId + ".json"), JsonSerializer.Serialize(prompts, new JsonSerializerOptions { WriteIndented = true }));
    }
}
