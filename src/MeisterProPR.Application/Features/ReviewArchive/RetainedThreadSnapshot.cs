// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.ReviewArchive;

/// <summary>
///     A full snapshot of a retained pull-request thread together with all of its comments. Upserting
///     a snapshot replaces the stored comment set so the persisted thread mirrors the snapshot exactly.
/// </summary>
/// <param name="ThreadId">Provider thread identifier (unique within the pull request).</param>
/// <param name="FilePath">File path the thread is anchored to, or null for pull-request-level threads.</param>
/// <param name="Line">Line the thread is anchored to, or null for pull-request-level threads.</param>
/// <param name="Status">Last-known thread status.</param>
/// <param name="UpdatedAt">UTC timestamp of the snapshot.</param>
/// <param name="Comments">Comments belonging to the thread, in publication order.</param>
public sealed record RetainedThreadSnapshot(
    string ThreadId,
    string? FilePath,
    int? Line,
    string Status,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<RetainedCommentSnapshot> Comments);

/// <summary>
///     A single comment within a <see cref="RetainedThreadSnapshot" />. The comment <paramref name="Text" />
///     is supplied in plaintext and is encrypted by the store on write.
/// </summary>
/// <param name="CommentId">Provider comment identifier.</param>
/// <param name="AuthorIdentity">Provider-neutral author identity.</param>
/// <param name="IsAiAuthored">Whether the comment was authored by the AI reviewer.</param>
/// <param name="PublishedAt">UTC timestamp the comment was published on the provider.</param>
/// <param name="Text">The plaintext comment body (encrypted at rest by the store).</param>
/// <param name="OriginatingJobId">The review job that produced this comment, when its provenance is retained; null otherwise.</param>
public sealed record RetainedCommentSnapshot(
    string CommentId,
    string AuthorIdentity,
    bool IsAiAuthored,
    DateTimeOffset PublishedAt,
    string Text,
    Guid? OriginatingJobId = null);
