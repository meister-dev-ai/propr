// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Providers.Identity;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

/// <summary>Resolves unambiguous browser links from bounded retained jobs inside the measurement snapshot.</summary>
internal static class ReviewerPerformanceSourceUrls
{
    internal static async Task<IReadOnlyDictionary<ReviewerPerformanceSourceKey, string?>> LoadAsync(
        MeisterProPRDbContext db, List<ReviewerPerformanceDailyCount> cells, IScmProviderRegistry registry, CancellationToken ct)
    {
        var aggregateIds = cells.Select(cell => cell.CodeInsightPullRequestId).Distinct().ToArray();
        var clients = cells.Select(cell => cell.ClientId).Distinct().ToArray();
        var aggregates = db.CodeInsightPullRequests.AsNoTracking().Where(pr => aggregateIds.Contains(pr.Id) && clients.Contains(pr.ClientId));
        var jobs = await db.ReviewJobs.AsNoTracking()
            .Where(job => aggregates.Any(pr => pr.ClientId == job.ClientId && pr.RepositoryId == job.RepositoryId && pr.PullRequestId == job.PullRequestId))
            .Select(job => new
            {
                job.ClientId, job.Provider, job.OrganizationUrl, job.HostBaseUrl, job.ProjectId, job.RepositoryId,
                job.RepositoryProjectPath, job.PullRequestId,
            }).Distinct().Take(ReviewerPerformanceRangeReader.MaximumCells + 1).ToListAsync(ct);
        if (jobs.Count > ReviewerPerformanceRangeReader.MaximumCells)
        {
            throw new ArgumentException("The scope exceeds the retained-source limit. Narrow the dates or clients.");
        }

        // Azure DevOps addresses a repository by name. The aggregate keeps the name the provider last reported, so
        // jobs from before a rename still link to the current page. Aggregates that disagree on the name give no name.
        var repositoryNames = (await aggregates
                .Select(pr => new { pr.ClientId, pr.RepositoryId, pr.PullRequestId, pr.RepositoryName })
                .ToListAsync(ct))
            .GroupBy(pr => (pr.ClientId, pr.RepositoryId, pr.PullRequestId))
            .ToDictionary(
                group => group.Key,
                group => group.Select(pr => pr.RepositoryName).Where(name => name is not null).Distinct().ToArray() is [var name] ? name : null);

        var sources = cells.GroupBy(cell => (cell.ClientId, cell.RepositoryId, cell.PullRequestId))
            .ToDictionary(group => group.Key, group => group.Select(ReviewerPerformanceSourceKey.Of).Distinct().ToArray());
        var urls = new Dictionary<ReviewerPerformanceSourceKey, string?>();
        foreach (var job in jobs)
        {
            if (!sources.TryGetValue((job.ClientId, job.RepositoryId, (long)job.PullRequestId), out var keys))
            {
                continue;
            }

            var coordinates = new CapturedReviewUrlCoordinates(
                job.Provider, job.OrganizationUrl, job.HostBaseUrl, job.ProjectId,
                job.RepositoryId, job.RepositoryProjectPath, job.PullRequestId,
                repositoryNames.GetValueOrDefault((job.ClientId, job.RepositoryId, (long)job.PullRequestId)));
            string scope;
            string? link;
            try
            {
                var policy = registry.GetSourceIdentityPolicy(job.Provider);
                var selected = policy.SelectCapturedSource(job.OrganizationUrl, job.HostBaseUrl);
                if (!SafeUrl(selected, out var captured) || !SafeCoordinates(coordinates, captured))
                {
                    MarkUnavailable(urls, keys);
                    continue;
                }

                scope = ProviderSourceIdentity.FromReviewSource(job.Provider, selected, policy).Value;
                link = policy.CreateCapturedReviewUrl(coordinates);
                if (!SafeUrl(link, out var candidateDestination) ||
                    candidateDestination.GetLeftPart(UriPartial.Authority) != captured.GetLeftPart(UriPartial.Authority))
                {
                    link = null;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or NotSupportedException or KeyNotFoundException)
            {
                // Missing or malformed optional coordinates do not change retained measurement evidence.
                MarkUnavailable(urls, keys);
                continue;
            }

            if (!SafeUrl(link, out var destination) || !SafeUrl(scope[(scope.IndexOf(':') + 1)..], out var origin)
                                                    || destination.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority))
            {
                link = null;
            }

            foreach (var key in keys.Where(key => key.ProviderScope.Length != 0 && key.ProviderScope == scope))
            {
                if (urls.TryGetValue(key, out var existing))
                {
                    if (existing != link)
                    {
                        urls[key] = null;
                    }
                }
                else
                {
                    urls.Add(key, link);
                }
            }
        }

        return urls;
    }

    private static void MarkUnavailable(Dictionary<ReviewerPerformanceSourceKey, string?> urls, ReviewerPerformanceSourceKey[] keys)
    {
        foreach (var key in keys)
        {
            urls[key] = null;
        }
    }

    private static bool SafeCoordinates(CapturedReviewUrlCoordinates coordinates, Uri captured)
    {
        if (coordinates.HostBaseUrl is not null && (!SafeUrl(coordinates.HostBaseUrl, out var host)
                                                    || host.GetLeftPart(UriPartial.Authority) != captured.GetLeftPart(UriPartial.Authority)))
        {
            return false;
        }

        if (Uri.TryCreate(coordinates.SourcePath, UriKind.Absolute, out _))
        {
            return SafeUrl(coordinates.SourcePath, out var source)
                   && source.GetLeftPart(UriPartial.Authority) == captured.GetLeftPart(UriPartial.Authority);
        }

        return !string.IsNullOrWhiteSpace(coordinates.SourcePath) && !coordinates.SourcePath.Contains(':')
                                                                  && !coordinates.SourcePath.Contains('\\') && !Uri.UnescapeDataString(coordinates.SourcePath)
                                                                      .Any(char.IsControl)
                                                                  && !Uri.UnescapeDataString(coordinates.SourcePath).Split('/')
                                                                      .Any(part => part is "." or "..");
    }

    private static bool SafeUrl(string? value, out Uri uri)
    {
        uri = null!;
        return value is { Length: > 0 and <= 8192 } && !value.Any(char.IsControl) && !value.Contains('\\')
               && !Uri.UnescapeDataString(value).Any(char.IsControl) && !Uri.UnescapeDataString(value).Contains('\\')
               && Uri.TryCreate(value, UriKind.Absolute, out uri!) && uri.Scheme is "http" or "https"
               && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }
}
