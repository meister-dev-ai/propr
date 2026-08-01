// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Queries.ResolvePullRequest;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Features.Reviewing.Intake.Controllers;

/// <summary>Resolves a pull request's web address to the clients and coordinates that can act on it.</summary>
[ApiController]
[Route("pull-requests")]
public sealed class PullRequestResolutionController(ResolvePullRequestHandler resolvePullRequestHandler)
    : ControllerBase
{
    /// <summary>
    ///     Resolves which of the caller's clients cover a pull request, and under which coordinates.
    /// </summary>
    /// <remarks>
    ///     Review-scoped endpoints are addressed by scope path, project key, repository identity, and number,
    ///     but a pull request's web address carries only a host, an owner segment, a repository name, and a
    ///     number. For Azure DevOps and Forgejo the remaining two are opaque identifiers that appear nowhere
    ///     in the address, so a client that only knows the address cannot construct a request without this
    ///     endpoint. Resolution reads persisted crawl configuration and calls no provider, so it uses no
    ///     source-control credential and returns the same stored coordinates a review job carries.
    /// </remarks>
    /// <param name="query">Address components taken from the pull request page.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">
    ///     Zero or more matches. An empty list means no client the caller can see covers the repository, which
    ///     is a normal answer; more than one means the caller must choose.
    /// </response>
    /// <response code="400">Missing or invalid address components.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    [HttpGet("resolve")]
    [ProducesResponseType(typeof(ResolvePullRequestResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ResolvePullRequest(
        [FromQuery] ResolvePullRequestRequest query,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        if (string.IsNullOrWhiteSpace(query.HostBaseUrl) ||
            string.IsNullOrWhiteSpace(query.RepositoryName) ||
            query.PullRequestNumber is null or < 1)
        {
            return this.BadRequest(
                new
                {
                    error = "hostBaseUrl, repositoryName and pullRequestNumber are required.",
                });
        }

        // A platform administrator resolves across every client; everyone else resolves only across the
        // clients they may read, so resolution can never reveal a client the caller cannot already see.
        var accessibleClientIds = AuthHelpers.IsAdmin(this.HttpContext)
            ? null
            : AuthHelpers.GetClientRoles(this.HttpContext)
                .Where(role => role.Value >= ClientRole.ClientUser)
                .Select(role => role.Key)
                .ToList();

        var result = await resolvePullRequestHandler.HandleAsync(
            new ResolvePullRequestQuery(
                accessibleClientIds,
                query.HostBaseUrl,
                query.ScopePath ?? string.Empty,
                query.RepositoryName,
                query.PullRequestNumber.Value),
            ct);

        return this.Ok(result);
    }
}

/// <summary>Query-string parameters identifying a pull request by its web address.</summary>
/// <param name="HostBaseUrl">
///     Host as it appears in the address, for example <c>https://dev.azure.com</c> or
///     <c>http://forgejo.internal:3000</c>. Only the scheme and authority are significant.
/// </param>
/// <param name="ScopePath">
///     Owner, namespace, or organization segment from the address, for example <c>local_admin</c> or
///     <c>meister-dev</c>. Omitting it widens the match to every configured scope on the host.
/// </param>
/// <param name="RepositoryName">Repository name as it appears in the address.</param>
/// <param name="PullRequestNumber">Pull request number as it appears in the address.</param>
public sealed record ResolvePullRequestRequest(
    string? HostBaseUrl,
    string? ScopePath,
    string? RepositoryName,
    int? PullRequestNumber);
