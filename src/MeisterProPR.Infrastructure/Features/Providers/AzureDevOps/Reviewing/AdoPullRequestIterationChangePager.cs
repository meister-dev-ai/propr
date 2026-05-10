// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

internal static class AdoPullRequestIterationChangePager
{
    internal const int MaxPageSize = 2000;

    internal static async Task<IReadOnlyList<GitPullRequestChange>> LoadAllAsync(
        Func<int, int, CancellationToken, Task<GitPullRequestIterationChanges>> loadPageAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loadPageAsync);

        var changes = new List<GitPullRequestChange>();
        var top = MaxPageSize;
        var skip = 0;

        bool hasMore;
        do
        {
            var page = await loadPageAsync(top, skip, cancellationToken);

            var pageChanges = page.ChangeEntries?.ToList();

            if (pageChanges is { Count: > 0 })
            {
                changes.AddRange(pageChanges);
            }

            if (page.NextTop > 0)
            {
                top = page.NextTop;
            }

            hasMore = page.NextSkip > 0 && page.NextSkip != skip;

            if (hasMore)
            {
                skip = page.NextSkip;
            }
        } while (hasMore);

        return changes.AsReadOnly();
    }
}
