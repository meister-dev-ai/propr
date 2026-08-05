// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.ReviewArchive;

namespace MeisterDev.ProPR.Application.Features.ThreadOwnership;

/// <summary>
///     The one answer to whether a comment thread on a pull request is ProPR's own. Every part of the system
///     that asks the question asks this, so the threads ProPR handles and the threads it leaves alone are one
///     partition rather than several that disagree.
/// </summary>
/// <remarks>
///     <para>
///         A thread is ProPR's when the posted-comment provenance records ProPR posting its first comment, and
///         otherwise when that comment's author is the authenticated token identity. Nothing else is an input.
///         The configured reviewer identity in particular is not: it selects which pull requests to review and
///         need not be, and on many installations is not, the account whose token posts.
///     </para>
///     <para>
///         One instance serves a whole pass, which runs sequentially. It is built from a single provenance read
///         for the pull request and handed to every consumer, so the query count stays at one and the identity
///         is singular by construction rather than by convention. A provider adapter receives it and contributes
///         the identity through <see cref="ContributeIdentity" />, because no provider persists that identity
///         and only the adapter's own connection handshake can resolve it.
///     </para>
/// </remarks>
public sealed class ThreadOwnershipResolver
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<PostedCommentOriginRow>> NoOrigins =
        new Dictionary<string, IReadOnlyList<PostedCommentOriginRow>>(StringComparer.Ordinal);

    private readonly IReadOnlyDictionary<string, IReadOnlyList<PostedCommentOriginRow>> _originsByCommentId;
    private readonly ProviderCommentIdScope _commentIdScope;

    private ThreadOwnershipResolver(
        IReadOnlyDictionary<string, IReadOnlyList<PostedCommentOriginRow>> originsByCommentId,
        ThreadOwnerIdentity identity,
        ProviderCommentIdScope commentIdScope)
    {
        this._originsByCommentId = originsByCommentId;
        this._commentIdScope = commentIdScope;
        this.Identity = identity;
    }

    /// <summary>
    ///     No provenance and no identity, so nothing is owned. What a caller uses when the provenance store is
    ///     absent, which is a degraded but safe answer: an unrecognised thread is left to whoever handles the
    ///     threads ProPR does not own.
    /// </summary>
    /// <remarks>
    ///     A fresh instance each time rather than one shared singleton: an adapter contributes the identity it
    ///     resolved into the instance it was handed, and a shared one would carry a client's posting account
    ///     into the next pass, for a different client. The comment-id regime is immaterial with no rows to
    ///     match, so the stricter one is used.
    /// </remarks>
    public static ThreadOwnershipResolver None => new(NoOrigins, ThreadOwnerIdentity.None, ProviderCommentIdScope.Thread);

    /// <summary>The authenticated token identity this resolver falls back to, absent when none was resolvable.</summary>
    public ThreadOwnerIdentity Identity { get; private set; }

    /// <summary>
    ///     Builds the resolver for one pass from that pull request's provenance rows and the resolved identity.
    /// </summary>
    /// <param name="provenance">
    ///     Every retained provenance row for the pull request, from a single
    ///     <c>IPostedCommentOriginStore.GetJobIdsForPullRequestAsync</c> read.
    /// </param>
    /// <param name="identity">
    ///     The authenticated token identity, or <see cref="ThreadOwnerIdentity.None" /> when the caller cannot
    ///     resolve one. A provider adapter that can resolve one adds it later through
    ///     <see cref="ContributeIdentity" />.
    /// </param>
    /// <param name="commentIdScope">
    ///     How the provider this pass runs against numbers its comment ids, from
    ///     <see cref="ProviderCommentIdScopes.For" />. The caller states it because the caller knows the
    ///     provider; a resolver cannot tell the two regimes apart from the rows alone.
    /// </param>
    public static ThreadOwnershipResolver Create(
        IReadOnlyList<PostedCommentOriginRow> provenance,
        ThreadOwnerIdentity identity,
        ProviderCommentIdScope commentIdScope)
    {
        ArgumentNullException.ThrowIfNull(provenance);

        if (provenance.Count == 0)
        {
            return new ThreadOwnershipResolver(NoOrigins, identity, commentIdScope);
        }

        var originsByCommentId = provenance
            .GroupBy(row => row.ProviderCommentId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PostedCommentOriginRow>)group.ToList(),
                StringComparer.Ordinal);

        return new ThreadOwnershipResolver(originsByCommentId, identity, commentIdScope);
    }

    /// <summary>
    ///     Adds the identity only the provider's own connection handshake can resolve, to this instance, so
    ///     every later consumer of the pass reads the same answer. A pass is sequential, and the adapter that
    ///     contributes runs before the consumers that read.
    /// </summary>
    /// <param name="identity">
    ///     The account the connection authenticates as. <see cref="ThreadOwnerIdentity.None" /> is ignored
    ///     rather than stored: a handshake that resolved nothing has nothing to say, and must not erase an
    ///     identity the pass already had.
    /// </param>
    public void ContributeIdentity(ThreadOwnerIdentity identity)
    {
        if (identity == ThreadOwnerIdentity.None)
        {
            return;
        }

        this.Identity = identity;
    }

    /// <summary>
    ///     Whether the thread belongs to ProPR, decided by its first comment.
    /// </summary>
    /// <param name="firstComment">
    ///     The thread's first comment that still exists, which is the one that raised it. A later comment says
    ///     nothing about whose thread it is: ProPR replies on human threads and humans reply on ProPR's.
    /// </param>
    public bool OwnsThread(ThreadCommentRef firstComment)
    {
        return this.Owns(firstComment);
    }

    /// <summary>Whether this single comment is ProPR's own, the same question at comment granularity.</summary>
    public bool OwnsComment(ThreadCommentRef comment)
    {
        return this.Owns(comment);
    }

    /// <summary>
    ///     The job that posted this comment, or null when no provenance is retained for it.
    /// </summary>
    /// <remarks>
    ///     Which ids have to match is the provider's comment-id regime, declared when this resolver was built.
    ///     Where comment ids are unique within the pull request the comment id decides alone. Where they are
    ///     numbered per thread the thread id has to match too, or the one row recorded for a summary would
    ///     answer for every thread on the pull request whose first comment carries the same number, which on
    ///     Azure DevOps is all of them.
    /// </remarks>
    public Guid? ResolveOriginatingJobId(string? providerThreadId, string? providerCommentId)
    {
        if (string.IsNullOrEmpty(providerCommentId)
            || !this._originsByCommentId.TryGetValue(providerCommentId, out var matches)
            || matches.Count == 0)
        {
            return null;
        }

        if (this._commentIdScope == ProviderCommentIdScope.PullRequest)
        {
            return matches[0].JobId;
        }

        if (string.IsNullOrEmpty(providerThreadId))
        {
            // Half an identity is no identity here: without the thread, a thread-scoped comment number
            // matches nothing in particular.
            return null;
        }

        foreach (var match in matches)
        {
            if (string.Equals(match.ProviderThreadId, providerThreadId, StringComparison.Ordinal))
            {
                return match.JobId;
            }
        }

        return null;
    }

    private bool Owns(ThreadCommentRef comment)
    {
        return this.ResolveOriginatingJobId(comment.ProviderThreadId, comment.ProviderCommentId) is not null
               || this.Identity.Matches(comment.AuthorId, comment.AuthorLogin);
    }
}
