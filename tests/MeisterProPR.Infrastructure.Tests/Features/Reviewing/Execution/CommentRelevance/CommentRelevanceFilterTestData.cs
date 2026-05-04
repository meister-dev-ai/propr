// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.CommentRelevance;

internal static class CommentRelevanceFilterTestData
{
    public const string DefaultFilePath = "src/Foo.cs";

    public static ReviewComment CreateComment(
        string message,
        CommentSeverity severity = CommentSeverity.Warning,
        int? lineNumber = null,
        string filePath = DefaultFilePath)
    {
        return new ReviewComment(filePath, lineNumber, severity, message);
    }

    public static ChangedFile CreateFile(
        string filePath = DefaultFilePath,
        int lineCount = 12,
        string? fullContent = null,
        string? diff = null)
    {
        fullContent ??= BuildContent(lineCount);
        diff ??= $"@@ -1,{lineCount} +1,{lineCount} @@";
        return new ChangedFile(filePath, ChangeType.Edit, fullContent, diff);
    }

    public static PullRequest CreatePullRequest(ChangedFile? file = null)
    {
        file ??= CreateFile();

        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "Test PR",
            null,
            "feature/x",
            "main",
            [file]);
    }

    public static CommentRelevanceFilterRequest CreateRequest(
        IReadOnlyList<ReviewComment>? comments = null,
        string implementationId = "heuristic-v1",
        string filePath = DefaultFilePath,
        int lineCount = 12,
        string? fullContent = null,
        Guid? jobId = null,
        Guid? fileResultId = null,
        Guid? protocolId = null)
    {
        var file = CreateFile(filePath, lineCount, fullContent);
        var pullRequest = CreatePullRequest(file);

        return new CommentRelevanceFilterRequest(
            jobId ?? Guid.NewGuid(),
            fileResultId ?? Guid.NewGuid(),
            implementationId,
            file.Path,
            file,
            pullRequest,
            comments ?? Array.Empty<ReviewComment>(),
            new ReviewSystemContext(null, [], null),
            protocolId ?? Guid.NewGuid());
    }

    private static string BuildContent(int lineCount)
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= lineCount; i++)
        {
            if (i > 1)
            {
                sb.Append('\n');
            }

            sb.Append("line").Append(i);
        }

        return sb.ToString();
    }
}
