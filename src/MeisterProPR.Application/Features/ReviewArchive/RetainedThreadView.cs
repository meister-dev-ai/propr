// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.ReviewArchive;

/// <summary>
///     A retained thread as returned from the store, with its comment bodies already decrypted.
/// </summary>
/// <param name="ThreadId">Provider thread identifier.</param>
/// <param name="FilePath">File path the thread is anchored to, or null for pull-request-level threads.</param>
/// <param name="Line">Line the thread is anchored to, or null for pull-request-level threads.</param>
/// <param name="Status">Last-known thread status.</param>
/// <param name="UpdatedAt">UTC timestamp of the latest snapshot.</param>
/// <param name="Comments">Decrypted comments belonging to the thread, in publication order.</param>
public sealed record RetainedThreadView(
    string ThreadId,
    string? FilePath,
    int? Line,
    string Status,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<RetainedCommentView> Comments);

/// <summary>
///     A retained comment as returned from the store, with its body decrypted.
/// </summary>
/// <param name="CommentId">Provider comment identifier.</param>
/// <param name="AuthorIdentity">Provider-neutral author identity.</param>
/// <param name="IsAiAuthored">Whether the comment was authored by the AI reviewer.</param>
/// <param name="PublishedAt">UTC timestamp the comment was published on the provider.</param>
/// <param name="Text">The decrypted comment body.</param>
/// <param name="OriginatingJobId">The review job that produced this comment, when its provenance is retained; null otherwise.</param>
public sealed record RetainedCommentView(
    string CommentId,
    string AuthorIdentity,
    bool IsAiAuthored,
    DateTimeOffset PublishedAt,
    string Text,
    Guid? OriginatingJobId = null);
