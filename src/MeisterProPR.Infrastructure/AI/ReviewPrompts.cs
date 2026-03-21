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
        var sb = new StringBuilder();

        if (pr.ChangedFiles.Count == 0)
        {
            sb.AppendLine("No files changed. Return summary stating no changes found; empty comments array.");
            AppendExistingThreads(sb, pr);
            return sb.ToString();
        }

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

        AppendExistingThreads(sb, pr);
        return sb.ToString();
    }

    private static void AppendExistingThreads(StringBuilder sb, PullRequest pr)
    {
        if (pr.ExistingThreads?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Existing Review Threads");
            sb.AppendLine(
                "The following threads already exist on this PR. Take them into account: " +
                "avoid re-flagging resolved issues, and consider developer responses.");

            foreach (var thread in pr.ExistingThreads)
            {
                var location = thread.FilePath is not null
                    ? $"{thread.FilePath}{(thread.LineNumber.HasValue ? $":L{thread.LineNumber}" : "")}"
                    : "(PR-level)";

                sb.AppendLine();
                sb.AppendLine($"### Thread at {location}");
                foreach (var comment in thread.Comments)
                {
                    sb.AppendLine($"  [{comment.AuthorName}]: {comment.Content}");
                }
            }
        }
    }
}
