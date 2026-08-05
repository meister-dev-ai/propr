// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.ThreadOwnership;

/// <summary>
///     One comment as an ownership question: how the provider identifies it, and who it says wrote it.
/// </summary>
/// <param name="ProviderThreadId">
///     Provider thread the comment sits on, or null when the provider exposes none. Load-bearing wherever the
///     provider numbers comment ids within their thread, because there the pair is the comment's identity;
///     ignored where a comment id is unique across the pull request. Which of the two applies is the
///     <see cref="ProviderCommentIdScope" /> the resolver was built with, not something read off this value.
/// </param>
/// <param name="ProviderCommentId">Provider-native comment identifier, or null when the provider exposes none.</param>
/// <param name="AuthorId">Author identity GUID, when the provider names authors that way.</param>
/// <param name="AuthorLogin">Author login or display name, when the provider names authors that way.</param>
public readonly record struct ThreadCommentRef(
    string? ProviderThreadId,
    string? ProviderCommentId,
    Guid? AuthorId = null,
    string? AuthorLogin = null);
