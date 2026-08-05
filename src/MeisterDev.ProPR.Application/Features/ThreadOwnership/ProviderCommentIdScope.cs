// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.ThreadOwnership;

/// <summary>
///     How far a provider's comment ids reach before they repeat, which is what decides whether the thread id
///     is part of a comment's identity when a provenance row is matched to a comment.
/// </summary>
public enum ProviderCommentIdScope
{
    /// <summary>
    ///     Comment ids are unique within the whole pull request, so the comment id alone identifies the row.
    ///     The thread id is then not evidence of anything: what these providers record at publish time is the
    ///     review or discussion the comment went out with, which is not the thread id the crawl reports back.
    /// </summary>
    PullRequest = 0,

    /// <summary>
    ///     Comment ids are numbered within their thread, so the first comment of every thread carries the same
    ///     id and only the (thread, comment) pair identifies the row. Matching on the comment id alone would
    ///     let one recorded comment claim every thread on the pull request.
    /// </summary>
    Thread = 1,
}

/// <summary>
///     Which comment-id regime each provider family uses. Stated once here rather than inferred per call site,
///     because getting it wrong in either direction is silent: too loose and ProPR claims a stranger's thread,
///     too strict and it stops recognising its own.
/// </summary>
public static class ProviderCommentIdScopes
{
    /// <summary>The regime <paramref name="provider" /> numbers its comments under.</summary>
    public static ProviderCommentIdScope For(ScmProvider provider)
    {
        return provider switch
        {
            ScmProvider.GitHub or ScmProvider.GitLab or ScmProvider.Forgejo => ProviderCommentIdScope.PullRequest,

            // Azure DevOps, and anything not yet classified. Requiring the thread id to match is the side that
            // errs towards not claiming a thread, which costs a missed reply rather than a reply posted into
            // someone else's conversation.
            _ => ProviderCommentIdScope.Thread,
        };
    }
}
