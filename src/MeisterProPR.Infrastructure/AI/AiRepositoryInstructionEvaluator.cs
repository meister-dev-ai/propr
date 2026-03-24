using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     AI-powered implementation of <see cref="IRepositoryInstructionEvaluator" />.
///     Uses a dedicated evaluator <see cref="IChatClient" /> (keyed <c>"evaluator"</c>) to determine
///     which repository instructions are relevant for the changed files in a pull request.
/// </summary>
public sealed partial class AiRepositoryInstructionEvaluator(
    [FromKeyedServices("evaluator")] IChatClient chatClient,
    ILogger<AiRepositoryInstructionEvaluator> logger) : IRepositoryInstructionEvaluator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<RepositoryInstruction>> EvaluateRelevanceAsync(
        IReadOnlyList<RepositoryInstruction> instructions,
        IReadOnlyList<string> changedFilePaths,
        CancellationToken cancellationToken)
    {
        if (instructions.Count == 0)
        {
            return [];
        }

        LogEvaluationStarted(logger, instructions.Count);

        var prompt = BuildEvaluationPrompt(instructions, changedFilePaths);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a relevance evaluator. Determine which repository instructions are applicable to the provided pull request files."),
            new(ChatRole.User, prompt),
        };

        var response = await chatClient.GetResponseAsync(messages, null, cancellationToken);
        var responseText = response.Text ?? "";

        var relevant = ParseRelevantInstructions(responseText, instructions, logger);

        LogEvaluationCompleted(logger, instructions.Count, relevant.Count);
        return relevant;
    }

    private static string BuildEvaluationPrompt(IReadOnlyList<RepositoryInstruction> instructions, IReadOnlyList<string> changedFilePaths)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Changed Files in Pull Request");
        foreach (var path in changedFilePaths)
        {
            sb.AppendLine($"- {path}");
        }

        sb.AppendLine();
        sb.AppendLine("## Available Repository Instructions");
        foreach (var instruction in instructions)
        {
            sb.AppendLine($"### {instruction.FileName}");
            sb.AppendLine($"Description: {instruction.Description}");
            sb.AppendLine($"When to use: {instruction.WhenToUse}");
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("Respond ONLY with a JSON object containing the file names of the relevant instructions:");
        sb.AppendLine("""{"relevant_instructions": ["instructions-foo.md", "instructions-bar.md"]}""");
        sb.AppendLine("Return an empty array if no instructions are relevant.");

        return sb.ToString();
    }

    private static IReadOnlyList<RepositoryInstruction> ParseRelevantInstructions(
        string responseText,
        IReadOnlyList<RepositoryInstruction> allInstructions,
        ILogger logger)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<RelevanceResponse>(responseText, JsonOptions);
            if (parsed?.RelevantInstructions is null || parsed.RelevantInstructions.Length == 0)
            {
                return [];
            }

            var relevantSet = new HashSet<string>(parsed.RelevantInstructions, StringComparer.OrdinalIgnoreCase);
            return allInstructions
                .Where(i => relevantSet.Contains(i.FileName))
                .ToList()
                .AsReadOnly();
        }
        catch (JsonException ex)
        {
            LogEvaluatorParseWarning(logger, ex);
            return [];
        }
    }

    [LoggerMessage(EventId = 4010, Level = LogLevel.Debug, Message = "Evaluating relevance for {InstructionCount} repository instruction(s)")]
    private static partial void LogEvaluationStarted(ILogger logger, int instructionCount);

    [LoggerMessage(EventId = 4011, Level = LogLevel.Debug, Message = "Relevance evaluation complete: {InputCount} → {OutputCount} instruction(s) retained")]
    private static partial void LogEvaluationCompleted(ILogger logger, int inputCount, int outputCount);

    [LoggerMessage(EventId = 4012, Level = LogLevel.Warning, Message = "Evaluator returned non-JSON or schema-mismatched response; returning empty instruction list")]
    private static partial void LogEvaluatorParseWarning(ILogger logger, Exception ex);

    private sealed class RelevanceResponse
    {
        /// <summary>Gets or sets the file names of instructions deemed relevant by the evaluator.</summary>
        [JsonPropertyName("relevant_instructions")]
        public string[]? RelevantInstructions { get; set; }
    }
}
