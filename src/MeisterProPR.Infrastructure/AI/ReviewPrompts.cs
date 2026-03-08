using System.Text;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.AI;

internal static class ReviewPrompts
{
    internal const string SystemPrompt = """
                                         You are an expert code reviewer specialising in .NET/C# and general
                                         software engineering best practices. Review pull requests for bugs, security
                                         vulnerabilities, code quality issues, performance problems, and maintainability.

                                         Respond with valid JSON ONLY — no markdown fences, no preamble, no text
                                         outside the JSON object. Schema:
                                         {
                                           "summary": "<overall narrative>",
                                           "comments": [
                                             { "filePath": "<relative path or null>", "lineNumber": <int or null>,
                                               "severity": "info"|"warning"|"error"|"suggestion", "message": "<text>" }
                                           ]
                                         }
                                         """;

    internal static string BuildUserMessage(PullRequest pr)
    {
        if (pr.ChangedFiles.Count == 0)
        {
            return "No files changed. Return summary stating no changes found; empty comments array.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Pull Request: {pr.Title}");
        sb.AppendLine($"{pr.SourceBranch} → {pr.TargetBranch}");
        if (!string.IsNullOrWhiteSpace(pr.Description))
        {
            sb.AppendLine($"Description: {pr.Description}");
        }

        sb.AppendLine();
        sb.AppendLine($"Changed Files ({pr.ChangedFiles.Count}):");

        foreach (var file in pr.ChangedFiles)
        {
            sb.AppendLine();
            sb.AppendLine($"=== {file.Path} [{file.ChangeType}] ===");
            sb.AppendLine("--- FULL CONTENT ---");
            sb.AppendLine(file.FullContent);
            sb.AppendLine("--- DIFF ---");
            sb.AppendLine(file.UnifiedDiff);
        }

        return sb.ToString();
    }
}