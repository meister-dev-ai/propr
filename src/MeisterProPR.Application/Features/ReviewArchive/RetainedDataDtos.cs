// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.ReviewArchive;

/// <summary>A retained discussion thread for a pull request, with its comments, for the in-app PR view.</summary>
/// <param name="ThreadId">Provider thread identifier.</param>
/// <param name="FilePath">File path the thread is anchored to, or null for pull-request-level threads.</param>
/// <param name="Line">Line the thread is anchored to, or null for pull-request-level threads.</param>
/// <param name="Status">Last-known thread status.</param>
/// <param name="UpdatedAt">UTC timestamp of the latest snapshot.</param>
/// <param name="Comments">Comments belonging to the thread, in publication order.</param>
public sealed record RetainedThreadDto(
    string ThreadId,
    string? FilePath,
    int? Line,
    string Status,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<RetainedCommentDto> Comments);

/// <summary>A retained comment within a <see cref="RetainedThreadDto" />.</summary>
/// <param name="CommentId">Provider comment identifier.</param>
/// <param name="AuthorIdentity">Provider-neutral author identity.</param>
/// <param name="IsAiAuthored">Whether the comment was authored by the AI reviewer.</param>
/// <param name="PublishedAt">UTC timestamp the comment was published on the provider.</param>
/// <param name="Body">The comment body.</param>
/// <param name="OriginatingJobId">The review job that produced this comment, when its provenance is retained; null otherwise.</param>
public sealed record RetainedCommentDto(
    string CommentId,
    string AuthorIdentity,
    bool IsAiAuthored,
    DateTimeOffset PublishedAt,
    string Body,
    Guid? OriginatingJobId = null);

/// <summary>A retained file entry for a pull request (without diff text), for the in-app PR view.</summary>
/// <param name="FilePath">The file path the diff applies to.</param>
/// <param name="RevisionKey">The newest retained review increment the file belongs to.</param>
/// <param name="ChangeType">The kind of change.</param>
/// <param name="IsBinary">Whether the file is binary (and therefore has no renderable diff).</param>
/// <param name="CreatedAt">UTC timestamp the newest retained diff for the file was captured.</param>
public sealed record RetainedFileDto(
    string FilePath,
    string RevisionKey,
    string ChangeType,
    bool IsBinary,
    DateTimeOffset CreatedAt);

/// <summary>A single retained file's stored diff for a pull request, for the in-app PR view.</summary>
/// <param name="FilePath">The file path the diff applies to.</param>
/// <param name="RevisionKey">The review increment the diff belongs to.</param>
/// <param name="ChangeType">The kind of change.</param>
/// <param name="IsBinary">Whether the file is binary (and therefore has no renderable diff).</param>
/// <param name="UnifiedDiff">The canonical unified diff (empty when the file is binary).</param>
/// <param name="CreatedAt">UTC timestamp the diff was retained.</param>
public sealed record RetainedFileDiffDto(
    string FilePath,
    string RevisionKey,
    string ChangeType,
    bool IsBinary,
    string UnifiedDiff,
    DateTimeOffset CreatedAt);
