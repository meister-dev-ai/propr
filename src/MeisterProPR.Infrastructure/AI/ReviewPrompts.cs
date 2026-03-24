using System.Text;
using MeisterProPR.Application.ValueObjects;
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
                                             { "file_path": "<relative path or null>", "line_number": <int or null>,
                                               "severity": "info"|"warning"|"error"|"suggestion", "message": "<text>" }
                                           ]
                                         }
                                         """;

    /// <summary>
    ///     Agentic loop guidance appended after <see cref="SystemPrompt" /> for all tool-aware reviews.
    ///     Describes available tools, review strategy, and the extended JSON schema that includes
    ///     <c>confidence_evaluations</c> and <c>loop_complete</c>.
    /// </summary>
    internal const string AgenticLoopGuidance = """
                                                 You have access to the following tools:
                                                 - get_changed_files: Get the list of all files changed in this pull request, including paths and change types.
                                                 - get_file_tree: Get the full file tree of the repository at a specified branch.
                                                 - get_file_content: Get a range of lines from a file at a specific branch (1-based, inclusive). Always specify a reasonable line range such as 1-100.

                                                 Review strategy:
                                                 1. Review all diffs provided in the initial context.
                                                 2. Use tools to fetch additional context when needed (e.g. full file content when only a partial diff is visible, or related files not in the diff).
                                                 3. After each investigation step, assess your confidence in the review across the concerns you identified.

                                                 ALWAYS respond with valid JSON ONLY — no markdown fences, no preamble, no text outside the JSON object. Schema:
                                                 {
                                                   "summary": "<overall review narrative>",
                                                   "comments": [
                                                     { "file_path": "<relative path or null>", "line_number": <int or null>,
                                                       "severity": "info"|"warning"|"error"|"suggestion", "message": "<text>" }
                                                   ],
                                                   "confidence_evaluations": [
                                                     { "concern": "<area such as 'security', 'code_quality', 'architecture', 'correctness'>", "confidence": <0-100> }
                                                   ],
                                                   "loop_complete": true|false
                                                 }

                                                 Set loop_complete to true when you have sufficient confidence in your review and no further investigation is needed.
                                                 Set loop_complete to false when you intend to call tools to gather more context before finalising.
                                                 """;

    /// <summary>
    ///     Builds the system prompt for the agentic review loop, incorporating client-level
    ///     customisations and repository instructions when present.
    ///     Always starts with <see cref="SystemPrompt" /> (the fixed reviewer-persona primer)
    ///     followed by <see cref="AgenticLoopGuidance" /> (tools, extended schema, loop instructions).
    /// </summary>
    /// <param name="context">
    ///     Optional review system context containing the client system message and repository
    ///     instructions. When <see langword="null" />, only the base prompts are returned.
    /// </param>
    internal static string BuildSystemPrompt(ReviewSystemContext? context)
    {
        var sb = new StringBuilder();

        // Fixed reviewer-persona primer — always included regardless of client configuration.
        sb.AppendLine(SystemPrompt);
        sb.AppendLine();

        // Agentic loop guidance — always included for all tool-aware reviews.
        sb.AppendLine(AgenticLoopGuidance);

        if (context is null)
        {
            return sb.ToString().TrimEnd();
        }

        if (!string.IsNullOrWhiteSpace(context.ClientSystemMessage))
        {
            sb.AppendLine();
            sb.AppendLine("## Client Instructions");
            sb.AppendLine(context.ClientSystemMessage);
        }

        if (context.RepositoryInstructions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Repository Instructions");
            sb.AppendLine("The following repository-specific instructions apply to this review:");

            foreach (var instruction in context.RepositoryInstructions)
            {
                sb.AppendLine();
                sb.AppendLine($"### {instruction.FileName}");
                sb.AppendLine($"**When to use:** {instruction.WhenToUse}");
                sb.AppendLine(instruction.Body);
            }
        }

        return sb.ToString().TrimEnd();
    }

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
