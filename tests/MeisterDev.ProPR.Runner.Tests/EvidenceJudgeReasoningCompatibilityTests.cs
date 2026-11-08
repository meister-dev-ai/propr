// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using MeisterDev.ProPR.Runner.Contracts;
using MeisterDev.ProPR.Runner.Execution;
using NSubstitute;

namespace MeisterDev.ProPR.Runner.Tests;

public sealed class EvidenceJudgeReasoningCompatibilityTests
{
    [Theory]
    [InlineData("gpt-5", true)]
    [InlineData("gpt-4o", false)]
    [InlineData("gpt-5-pro", false)]
    [InlineData("gateway-alias", false)]
    [InlineData("gpt-5", false)]
    public async Task TheRunnerJudge_UsesOnlyTheEffortItsResolvedBindingSupports(string modelId, bool supportsLow)
    {
        var manifest = RunnerManifests.Sample() with
        {
            DefaultModel = RunnerManifests.Model("reviewer-default", "None") with
            {
                RemoteModelId = modelId,
                SupportsLowReasoningEffort = supportsLow,
            },
        };
        using var handler = new JudgeRelayEndpoint(supportsLow);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://control-plane.invalid/runners/execution/") };
        var chat = new RelayChatClient(http, manifest.JobId, manifest.LeaseGeneration, manifest.DefaultModel.LogicalModelName);
        var resolver = new RelayAiRuntimeResolver(manifest, _ => chat);
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(Arg.Any<string>(), "source", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns("return lookup(key);");

        var outcomes = await new EvidenceBackedReviewVerifier().VerifyAsync(
            [WorkItem()], [], new ReviewVerificationContext(tools, "source", chat, modelId, Guid.NewGuid(), resolver));

        Assert.Equal(FinalGateDecision.PublishDisposition, Assert.Single(outcomes).RecommendedDisposition);
        // The claim is a Warning, so the independent check follows the judge on the same relay. The confirmation carries no
        // confidence evidence, so the escalation check follows as well. Both checks request the provider's default effort.
        Assert.Equal(3, handler.Requests.Count);
        using var judgeRequest = JsonDocument.Parse(handler.Requests[0]);
        var options = judgeRequest.RootElement.GetProperty("options");
        Assert.Equal(supportsLow ? "low" : null, options.GetProperty("reasoningEffort").GetString());
        Assert.Equal(4000, options.GetProperty("maxOutputTokens").GetInt32());
        Assert.All(
            handler.Requests.Skip(1),
            request =>
            {
                using var check = JsonDocument.Parse(request);
                Assert.Null(check.RootElement.GetProperty("options").GetProperty("reasoningEffort").GetString());
            });
    }

    // Where the judge has no low effort to ask for, it runs at the default level of the model, and both checks ask for
    // the level none from a model whose levels hold it. On a model the manifest lists with the level none and with none
    // as its default level, every one of these calls then names none on the relay.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheRunnerJudgeAndChecks_SendNoneToAModelThatAcceptsIt(bool supportsLow)
    {
        var manifest = RunnerManifests.Sample() with
        {
            DefaultModel = RunnerManifests.Model("reviewer-default", "") with
            {
                RemoteModelId = "gpt-6-luna",
                SupportsLowReasoningEffort = supportsLow,
                SupportsReasoningEffortNone = true,
                ReasoningLevels = ["none", "low", "medium", "high"],
                DefaultReasoningLevel = "none",
            },
        };
        using var handler = new JudgeRelayEndpoint(supportsLow, acceptsNone: true);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://control-plane.invalid/runners/execution/") };
        var chat = new RelayChatClient(http, manifest.JobId, manifest.LeaseGeneration, manifest.DefaultModel.LogicalModelName);
        var resolver = new RelayAiRuntimeResolver(manifest, _ => chat);
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync(Arg.Any<string>(), "source", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns("return lookup(key);");

        var outcomes = await new EvidenceBackedReviewVerifier().VerifyAsync(
            [WorkItem()], [], new ReviewVerificationContext(tools, "source", chat, "gpt-6-luna", Guid.NewGuid(), resolver));

        Assert.Equal(FinalGateDecision.PublishDisposition, Assert.Single(outcomes).RecommendedDisposition);
        var efforts = handler.Requests
            .Select(request =>
            {
                using var sent = JsonDocument.Parse(request);
                return sent.RootElement.GetProperty("options").GetProperty("reasoningEffort").GetString();
            })
            .ToList();
        Assert.Equal([supportsLow ? "low" : "none", "none", "none"], efforts);
    }

    [Fact]
    public void AManifestWithoutTheOptionalCapability_KeepsProviderDefaults()
    {
        var binding = JsonSerializer.Deserialize<RunnerModelBinding>(
            """{"logicalModelName":"reviewer-default","remoteModelId":"gpt-5","providerKind":"meisterdev/openAi","reasoningEffort":"None","supportsPromptCaching":true,"supportsToolUse":true,"supportsStructuredOutput":true}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.False(binding!.SupportsLowReasoningEffort);
    }

    private static VerificationWorkItem WorkItem()
    {
        var claim = new ClaimDescriptor(
            "claim-1", "finding-1", ClaimDescriptor.LocalStage,
            CandidateReviewFinding.ReviewCommentMessageNullableClaimKind,
            "The lookup result may be null.", CommentSeverity.Warning,
            ClaimDescriptor.DeterministicOnlyMode, ClaimDescriptor.ApiOrSymbolUsageFamily,
            subjectIdentifier: "lookup", anchorFilePath: "src/Lookup.cs", anchorLineNumber: 1, requiresSymbolEvidence: true);
        return new VerificationWorkItem(
            claim, new CandidateFindingProvenance(CandidateFindingProvenance.PerFileCommentOrigin, "per_file_review"),
            ClaimDescriptor.LocalStage, VerificationWorkItem.AnchorOnlyScope, false);
    }

    // Refuses an effort the model does not accept, as a provider answers a reasoning parameter it does not know.
    private sealed class JudgeRelayEndpoint(bool supportsLow, bool acceptsNone = false) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            using var sent = JsonDocument.Parse(this.Requests[^1]);
            var effort = sent.RootElement.GetProperty("options").GetProperty("reasoningEffort").GetString();
            var refused = effort switch
            {
                null => false,
                "none" => !acceptsNone,
                _ => !supportsLow,
            };
            if (refused)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("Unsupported reasoning parameter", Encoding.UTF8, "text/plain"),
                };
            }

            var body = JsonSerializer.Serialize(
                new
                {
                    response = new
                    {
                        messages = new[]
                        {
                            new
                            {
                                role = "assistant",
                                contents = new[]
                                {
                                    new Dictionary<string, string>
                                    {
                                        ["$type"] = "text",
                                        ["text"] = """{"verdict":"confirmed","reason":"The lookup may return null."}""",
                                    },
                                },
                            },
                        },
                    },
                    usage = new { inputTokens = 100, outputTokens = 20, isEstimated = false },
                    softCapReached = false,
                    replayed = false,
                });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
