// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     A single comment from a linked item's discussion thread, retrieved on demand when the review
///     model asks for the item's discussion.
/// </summary>
/// <param name="Author">Display name or login of the comment author.</param>
/// <param name="CreatedAt">When the comment was posted, when the provider reports it.</param>
/// <param name="Text">The comment body.</param>
public sealed record LinkedItemComment(string Author, DateTimeOffset? CreatedAt, string Text);
