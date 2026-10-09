// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Providers;

public sealed class ReviewOverviewProviderTests
{
    [Theory]
    [InlineData("FORBIDDEN", false)]
    [InlineData("FORBIDDEN", true)]
    [InlineData("RATE_LIMITED", false)]
    [InlineData("RATE_LIMITED", true)]
    public async Task GitHubOverview_StructuredErrorsPreserveTypedOutcomes(string type, bool partialData)
    {
        var fixture = new Fixture(ScmProvider.GitHub);
        fixture.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/graphql", StringComparison.Ordinal)
            ? GraphQlErrors(new[] { new { type, message = "private provider details fixture-token" } }, partialData)
            : null;
        if (type == "RATE_LIMITED")
        {
            var error = await Assert.ThrowsAsync<ProviderThrottledException>(() => fixture.Provider.GetOverviewAsync(
                fixture.ClientId, fixture.Review, fixture.Context));
            Assert.DoesNotContain("private", error.Message);
            Assert.DoesNotContain("fixture-token", error.Message);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ProviderAccessDeniedException>(() => fixture.Provider.GetOverviewAsync(
                fixture.ClientId, fixture.Review, fixture.Context));
            Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
            Assert.False(error.ConnectionWide);
            Assert.DoesNotContain("private", error.Message);
            Assert.DoesNotContain("fixture-token", error.Message);
        }
    }

    [Theory]
    [InlineData("FORBIDDEN")]
    [InlineData("RATE_LIMITED")]
    public async Task GitHubOverview_RecognizedErrorsAfterMalformedItemsPreserveTypedOutcomes(string type)
    {
        var fixture = new Fixture(ScmProvider.GitHub);
        fixture.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/graphql", StringComparison.Ordinal)
            ? GraphQlErrors(new object?[] { null, new { type = 12 }, new { type = "UNKNOWN" }, new { type } }, true)
            : null;
        if (type == "FORBIDDEN")
        {
            await Assert.ThrowsAsync<ProviderAccessDeniedException>(() => fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context));
        }
        else
        {
            await Assert.ThrowsAsync<ProviderThrottledException>(() => fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitHubOverview_ExplicitDenialTakesPrecedenceAcrossStructuredErrors(bool denialFirst)
    {
        var fixture = new Fixture(ScmProvider.GitHub);
        fixture.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/graphql", StringComparison.Ordinal)
            ? GraphQlErrors(
                new[] { new { type = denialFirst ? "FORBIDDEN" : "RATE_LIMITED" }, new { type = denialFirst ? "RATE_LIMITED" : "FORBIDDEN" } }, true)
            : null;
        var error = await Assert.ThrowsAsync<ProviderAccessDeniedException>(() => fixture.Provider.GetOverviewAsync(
            fixture.ClientId, fixture.Review, fixture.Context));
        Assert.False(error.ConnectionWide);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"FORBIDDEN\"")]
    [InlineData("true")]
    [InlineData("[null]")]
    [InlineData("[12]")]
    [InlineData("[{}]")]
    [InlineData("[{\"type\":12}]")]
    [InlineData("[{\"type\":\"UNKNOWN\"}]")]
    [InlineData("[{\"message\":\"forbidden rate limit\"}]")]
    public async Task GitHubOverview_UnknownOrMalformedErrorsRemainIncomplete(string errorsJson)
    {
        using var errors = JsonDocument.Parse(errorsJson);
        var fixture = new Fixture(ScmProvider.GitHub);
        fixture.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/graphql", StringComparison.Ordinal)
            ? GraphQlErrors(errors.RootElement, true)
            : null;
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Equal(5, overview.TotalComments);
        Assert.Null(overview.ResolvedDiscussions);
        Assert.Null(overview.UnresolvedDiscussions);
        Assert.False(overview.IsComplete);
    }

    [Fact]
    public async Task GitHubOverview_EmptyErrorsRetainCompleteMetadata()
    {
        var fixture = new Fixture(ScmProvider.GitHub);
        fixture.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/graphql", StringComparison.Ordinal)
            ? GraphQlErrors(Array.Empty<object>(), true)
            : null;
        Assert.True((await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context)).IsComplete);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/installed")]
    [InlineData("/installed/")]
    [InlineData("/api")]
    public async Task ForgejoSelectedScope_PrefixesVerificationAndMetadataRequests(string prefix)
    {
        var fixture = new Fixture(ScmProvider.Forgejo, prefix);
        var paths = new List<string>();
        fixture.Override = request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            Assert.StartsWith(prefix.TrimEnd('/') + "/api/v1/", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
            return null;
        };
        Assert.True((await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context)).IsComplete);
        Assert.Equal(prefix.TrimEnd('/') + "/api/v1/user", paths[0]);
        Assert.True(paths.Count > 1);
    }

    [Fact]
    public async Task ForgejoDiscovery_VerifiesTheSelectedDeploymentScope()
    {
        var fixture = new Fixture(ScmProvider.Forgejo, "/installed");
        var paths = new List<string>();
        fixture.Override = request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath.EndsWith("/user", StringComparison.Ordinal)
                ? null
                : Json(Array.Empty<object>());
        };

        await fixture.Discovery.ListOpenReviewsAsync(fixture.ClientId, fixture.Review.Repository, null, context: fixture.Context);

        Assert.Equal("/installed/api/v1/user", paths[0]);
        Assert.Equal(2, paths.Count);
    }

    private static HttpResponseMessage GraphQlErrors<T>(T errors, bool partialData) => Json(
        new
        {
            errors,
            data = partialData
                ? new
                {
                    repository = new { pullRequest = new { reviewThreads = new { nodes = Array.Empty<object>(), pageInfo = new { hasNextPage = false } } } }
                }
                : null,
        });

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task DiscoveryThrottleSignalsDoNotBecomeAccessDenials(ScmProvider provider)
    {
        foreach (var throttle in new[] { "retry", "remaining", "429" })
        {
            var fixture = new Fixture(provider)
            {
                Override = request =>
                {
                    if (request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal))
                    {
                        return null;
                    }

                    var response = new HttpResponseMessage(throttle == "429" ? HttpStatusCode.TooManyRequests : HttpStatusCode.Forbidden);
                    if (throttle == "retry")
                    {
                        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                    }

                    if (throttle == "remaining")
                    {
                        response.Headers.Add("x-ratelimit-remaining", "0");
                    }

                    return response;
                },
            };
            await Assert.ThrowsAsync<ProviderThrottledException>(() => fixture.Discovery.ListOpenReviewsAsync(
                fixture.ClientId, fixture.Review.Repository, null, context: fixture.Context));
        }
    }

    [Theory]
    [InlineData(ScmProvider.GitHub, true, false)]
    [InlineData(ScmProvider.GitHub, false, false)]
    [InlineData(ScmProvider.GitHub, false, true)]
    [InlineData(ScmProvider.GitLab, true, false)]
    [InlineData(ScmProvider.GitLab, false, false)]
    [InlineData(ScmProvider.GitLab, false, true)]
    [InlineData(ScmProvider.Forgejo, true, false)]
    [InlineData(ScmProvider.Forgejo, false, false)]
    [InlineData(ScmProvider.Forgejo, false, true)]
    public async Task ProviderDenialsPreserveTypedStatusAcrossVerificationDiscoveryAndMetadata(ScmProvider provider, bool verification, bool discovery)
    {
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
        {
            var fixture = new Fixture(provider)
            {
                Override = request => request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal) == verification
                    ? new HttpResponseMessage(status)
                    : null,
            };
            var error = await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
            {
                if (discovery)
                {
                    await fixture.Discovery.ListOpenReviewsAsync(fixture.ClientId, fixture.Review.Repository, null, context: fixture.Context);
                }
                else
                {
                    await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
                }
            });
            Assert.Equal(status, error.StatusCode);
            Assert.DoesNotContain("private", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(ScmProvider.GitHub, true)]
    [InlineData(ScmProvider.GitHub, false)]
    [InlineData(ScmProvider.GitLab, true)]
    [InlineData(ScmProvider.GitLab, false)]
    [InlineData(ScmProvider.Forgejo, true)]
    [InlineData(ScmProvider.Forgejo, false)]
    public async Task ProviderThrottleSignalsDoNotBecomeAccessDenials(ScmProvider provider, bool verification)
    {
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
        {
            var fixture = new Fixture(provider)
            {
                Override = request =>
                {
                    if (request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal) != verification)
                    {
                        return null;
                    }

                    var response = new HttpResponseMessage(status);
                    response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                    return response;
                },
            };
            await Assert.ThrowsAsync<ProviderThrottledException>(() => fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context));
        }
    }

    [Theory]
    [InlineData("/api/v3/repos/team/repo/pulls/42/comments", "/installed/api/v3/repos/team/repo/pulls/42/comments")]
    [InlineData("/installed/api/v3/repos/team/repo/pulls/42/comments", "/installed/api/v3/repos/team/repo/pulls/42/comments")]
    [InlineData("/api/graphql", "/installed/api/graphql")]
    [InlineData("/installed/api/graphql", "/installed/api/graphql")]
    [InlineData("/api/v4/projects/101/merge_requests/42/discussions", "/installed/api/v4/projects/101/merge_requests/42/discussions")]
    [InlineData("/installed/api/v4/projects/101/merge_requests/42/discussions", "/installed/api/v4/projects/101/merge_requests/42/discussions")]
    [InlineData("/api/v1/repos/team/repo/issues/42/comments", "/installed/api/v1/repos/team/repo/issues/42/comments")]
    [InlineData("/installed/api/v1/repos/team/repo/issues/42/comments", "/installed/api/v1/repos/team/repo/issues/42/comments")]
    [InlineData("/installed-other/api/v1/comments", "/installed/installed-other/api/v1/comments")]
    public void ScopedUri_AppliesTheSelectedBasePathExactlyOnce(string input, string expected)
    {
        var builderBase = input.StartsWith("/installed/", StringComparison.Ordinal)
            ? "https://scm.example.test/installed"
            : "https://scm.example.test";
        var result = ReviewOverviewReadSession.ScopedUri(
            new Uri("https://scm.example.test" + input + "?page=2"),
            "https://scm.example.test/installed/", builderBase);
        Assert.Equal(expected, result.AbsolutePath);
        Assert.Equal("?page=2", result.Query);
    }

    [Theory]
    [InlineData("/api/v3/repos/team/repo/pulls", "https://scm.example.test", "/api/api/v3/repos/team/repo/pulls")]
    [InlineData("/api/api/v3/repos/team/repo/pulls", "https://scm.example.test/api", "/api/api/v3/repos/team/repo/pulls")]
    [InlineData("/api/graphql", "https://scm.example.test", "/api/api/graphql")]
    [InlineData("/api/api/graphql", "https://scm.example.test/api", "/api/api/graphql")]
    public void ScopedUri_DistinguishesAConfiguredApiBaseFromTheNativeApiPath(string input, string builderBase, string expected)
    {
        var result = ReviewOverviewReadSession.ScopedUri(
            new Uri("https://scm.example.test" + input),
            "https://scm.example.test/api", builderBase);
        Assert.Equal(expected, result.AbsolutePath);
    }

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task Overview_ExactSelfHostedPathsUseRootRepositoryHostAndSelectedScope(ScmProvider provider)
    {
        var fixture = new Fixture(provider, "/installed");
        Assert.Equal("https://scm.example.test", fixture.Review.Repository.Host.HostBaseUrl);
        var paths = new List<string>();
        fixture.Override = request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal))
            {
                paths.Add(request.RequestUri.AbsolutePath);
            }

            return null;
        };
        Assert.True((await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context)).IsComplete);
        Assert.Equal(
            provider switch
            {
                ScmProvider.GitHub => new[]
                {
                    "/installed/api/v3/repos/team/repo/issues/42/comments",
                    "/installed/api/v3/repos/team/repo/pulls/42/comments", "/installed/api/v3/repos/team/repo/pulls/42/reviews", "/installed/api/graphql"
                },
                ScmProvider.GitLab => new[] { "/installed/api/v4/projects/101/merge_requests/42/discussions" },
                _ => new[]
                {
                    "/installed/api/v1/repos/team/repo/issues/42/comments",
                    "/installed/api/v1/repos/team/repo/pulls/42/reviews", "/installed/api/v1/repos/team/repo/pulls/42/reviews/7/comments"
                },
            }, paths);
    }

    [Theory]
    [InlineData(ScmProvider.GitHub, 6)]
    [InlineData(ScmProvider.Forgejo, 4)]
    public async Task Overview_IncludesPublishedReviewBodiesWithoutBlankOrPendingDrafts(ScmProvider provider, int expected)
    {
        var fixture = new Fixture(provider);
        fixture.Override = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/reviews", StringComparison.Ordinal))
            {
                return Json(
                    new object[]
                    {
                        new
                        {
                            id = 7, body = "Published summary", state = provider == ScmProvider.GitHub ? "COMMENTED" : "COMMENT",
                            submitted_at = "2026-10-03T12:00:00Z"
                        },
                        new { id = 8, body = "", state = "APPROVED", submitted_at = "2026-10-03T12:00:00Z" },
                        new { id = 9, body = "  ", state = "APPROVED", submitted_at = "2026-10-03T12:00:00Z" },
                        new { id = 10, body = "Private draft", state = "PENDING" },
                    });
            }

            if (path.Contains("/reviews/", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("/reviews/10/", path, StringComparison.Ordinal);
                return path.Contains("/reviews/7/", StringComparison.Ordinal) ? Json(new[] { new { id = 100 } }) : Json(Array.Empty<object>());
            }

            return null;
        };
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Equal(expected, overview.TotalComments);
        Assert.True(overview.IsComplete);
        Assert.InRange(fixture.Requests, 2, 13);
    }

    [Theory]
    [InlineData(ScmProvider.GitHub, "body")]
    [InlineData(ScmProvider.GitHub, "state")]
    [InlineData(ScmProvider.GitHub, "submitted_at")]
    [InlineData(ScmProvider.Forgejo, "body")]
    [InlineData(ScmProvider.Forgejo, "state")]
    [InlineData(ScmProvider.Forgejo, "submitted_at")]
    public async Task Overview_MissingPublishedReviewFieldsDoNotBecomeKnownZero(ScmProvider provider, string missing)
    {
        var fields = new Dictionary<string, object> { ["id"] = 7, ["body"] = "", ["state"] = "APPROVED", ["submitted_at"] = "2026-10-03T12:00:00Z" };
        fields.Remove(missing);
        var fixture = new Fixture(provider)
        {
            Override = request => request.RequestUri!.AbsolutePath.EndsWith("/reviews", StringComparison.Ordinal) ? Json(new[] { fields }) : null,
        };
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Null(overview.TotalComments);
        Assert.False(overview.IsComplete);
    }

    [Theory]
    [InlineData(ScmProvider.GitHub, "body")]
    [InlineData(ScmProvider.GitHub, "nullBody")]
    [InlineData(ScmProvider.GitHub, "state")]
    [InlineData(ScmProvider.GitHub, "submitted_at")]
    [InlineData(ScmProvider.Forgejo, "body")]
    [InlineData(ScmProvider.Forgejo, "nullBody")]
    [InlineData(ScmProvider.Forgejo, "state")]
    [InlineData(ScmProvider.Forgejo, "submitted_at")]
    public async Task Overview_MalformedPublishedReviewFieldsAreUnavailable(ScmProvider provider, string malformed)
    {
        var fields = new Dictionary<string, object?> { ["id"] = 7, ["body"] = "Summary", ["state"] = "APPROVED", ["submitted_at"] = "2026-10-03T12:00:00Z" };
        if (malformed == "nullBody")
        {
            fields["body"] = null;
        }
        else
        {
            fields[malformed] = malformed == "body" ? 12 : "invalid";
        }

        var fixture = new Fixture(provider)
        {
            Override = request => request.RequestUri!.AbsolutePath.EndsWith("/reviews", StringComparison.Ordinal) ? Json(new[] { fields }) : null,
        };
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Null(overview.TotalComments);
        Assert.False(overview.IsComplete);
    }

    [Fact]
    public async Task ForgejoOverview_ReviewRequestsAndPendingDraftsAreNotPublishedMessages()
    {
        var fixture = new Fixture(ScmProvider.Forgejo);
        fixture.Override = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/reviews", StringComparison.Ordinal))
            {
                return Json(new[] { new { id = 7, state = "REQUEST_REVIEW" }, new { id = 8, state = "PENDING" } });
            }

            Assert.DoesNotContain("/reviews/", path, StringComparison.Ordinal);
            return null;
        };
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Equal(2, overview.TotalComments);
        Assert.True(overview.IsComplete);
        Assert.False(overview.ResolutionSupported);
    }

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task Overview_OverlappingPagesAreIncompleteEvenWithNewIds(ScmProvider provider)
    {
        var fixture = new Fixture(provider);
        var page = 0;
        fixture.Override = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (!(path.Contains("/issues/", StringComparison.Ordinal) || path.EndsWith("/discussions", StringComparison.Ordinal)))
            {
                return null;
            }

            return Json(
                ++page == 1
                    ? Enumerable.Range(1, 100).Select(id => Message(provider, id)).ToArray()
                    : new[] { Message(provider, 100), Message(provider, 101) });
        };
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Null(overview.TotalComments);
        Assert.False(overview.IsComplete);
    }

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task Overview_DuplicateIdsWithinPageAreIncomplete(ScmProvider provider)
    {
        var fixture = new Fixture(provider)
        {
            Override = request => request.RequestUri!.AbsolutePath.Contains("/issues/", StringComparison.Ordinal) ||
                                  request.RequestUri.AbsolutePath.EndsWith("/discussions", StringComparison.Ordinal)
                ? Json(new[] { Message(provider, 1), Message(provider, 1), Message(provider, 2) })
                : null,
        };
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Null(overview.TotalComments);
        Assert.False(overview.IsComplete);
    }

    private static object Message(ScmProvider provider, int id) => provider == ScmProvider.GitLab
        ? new { id, individual_note = true, notes = new[] { new { id, system = false, resolvable = false } } }
        : new { id };

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task Overview_ProviderTimeoutWithoutCallerCancellationIsFixedFailure(ScmProvider provider)
    {
        var fixture = new Fixture(provider)
        {
            Override = request => request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal)
                ? null
                : throw new TaskCanceledException("private transport detail"),
        };
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context));
        Assert.Equal("The provider could not return pull request metadata.", failure.Message);
    }

    [Fact]
    public void Registry_ReturnsProviderOverviewAndAdvertisesItsCapability()
    {
        var adapters = new[] { ScmProvider.AzureDevOps, ScmProvider.GitHub, ScmProvider.GitLab, ScmProvider.Forgejo }
            .Select(provider =>
            {
                var adapter = Substitute.For<IReviewOverviewProvider>();
                adapter.Provider.Returns(provider);
                return adapter;
            }).ToList();
        var registry = new ScmProviderRegistry([], [], [], [], [], [], [], [], [], [], [], [], adapters);
        foreach (var adapter in adapters)
        {
            Assert.Same(adapter, registry.GetReviewOverviewProvider(adapter.Provider));
            Assert.Contains("reviewOverview", registry.GetRegisteredCapabilities(adapter.Provider));
        }

        var absent = new ScmProviderRegistry([], [], [], [], [], [], [], [], [], [], [], []);
        Assert.Throws<InvalidOperationException>(() => absent.GetReviewOverviewProvider(ScmProvider.GitHub));
    }

    [Fact]
    public async Task GitHubOverview_FollowsNativeThreadCursor()
    {
        var fixture = new Fixture(ScmProvider.GitHub);
        var pages = 0;
        fixture.Override = request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/graphql", StringComparison.Ordinal))
            {
                return null;
            }

            pages++;
            return Json(
                new
                {
                    data = new
                    {
                        repository = new
                        {
                            pullRequest = new
                            {
                                reviewThreads = new
                                {
                                    nodes = new[] { new { id = $"thread-{pages}", isResolved = pages == 1 } },
                                    pageInfo = new { hasNextPage = pages == 1, endCursor = "cursor-1" },
                                }
                            }
                        }
                    }
                });
        };
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Equal(2, pages);
        Assert.Equal(1, overview.ResolvedDiscussions);
        Assert.Equal(1, overview.UnresolvedDiscussions);
        Assert.True(overview.IsComplete);
    }

    [Fact]
    public async Task GitLabOverview_FollowsNextPageHeader()
    {
        var fixture = new Fixture(ScmProvider.GitLab);
        var pages = 0;
        fixture.Override = request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/discussions", StringComparison.Ordinal))
            {
                return null;
            }

            pages++;
            var response = Json(
                new[]
                {
                    new
                    {
                        id = $"discussion-{pages}", individual_note = false,
                        notes = new[] { new { id = pages, system = false, resolvable = true, resolved = pages == 1 } }
                    }
                });
            response.Headers.Add("X-Next-Page", pages == 1 ? "2" : "");
            return response;
        };
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Equal(2, pages);
        Assert.Equal(2, overview.TotalComments);
        Assert.Equal(1, overview.ResolvedDiscussions);
        Assert.Equal(1, overview.UnresolvedDiscussions);
        Assert.True(overview.IsComplete);
    }

    [Fact]
    public async Task ForgejoOverview_FollowsTotalCountHeader()
    {
        var fixture = new Fixture(ScmProvider.Forgejo);
        var pages = 0;
        fixture.Override = request =>
        {
            if (!request.RequestUri!.AbsolutePath.Contains("/issues/", StringComparison.Ordinal))
            {
                return null;
            }

            pages++;
            var response = Json(new[] { new { id = pages } });
            response.Headers.Add("X-Total-Count", "2");
            return response;
        };
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Equal(2, pages);
        Assert.Equal(3, overview.TotalComments);
        Assert.Null(overview.ResolvedDiscussions);
        Assert.True(overview.IsComplete);
    }

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task Overview_OversizedPayloadIsUnavailable(ScmProvider provider)
    {
        var fixture = new Fixture(provider)
        {
            Override = request => request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal)
                ? null
                : Json(new[] { new { id = 1, body = new string('x', 2 * 1024 * 1024) } }),
        };
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Null(overview.TotalComments);
        Assert.False(overview.IsComplete);
    }

    [Fact]
    public async Task GitLabOverview_OversizedDiscussionDoesNotReturnTruncatedCounts()
    {
        var fixture = new Fixture(ScmProvider.GitLab)
        {
            Override = request => request.RequestUri!.AbsolutePath.EndsWith("/discussions", StringComparison.Ordinal)
                ? Json(
                    new[]
                    {
                        new
                        {
                            id = "discussion", individual_note = false,
                            notes = Enumerable.Range(1, 3001).Select(id => new { id, system = false, resolvable = true, resolved = false }).ToArray()
                        }
                    })
                : null,
        };
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Null(overview.TotalComments);
        Assert.Null(overview.UnresolvedDiscussions);
        Assert.False(overview.IsComplete);
    }

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task Discovery_MapsNativeAuthorBranchesAndRevisionWithoutMetadataCalls(ScmProvider provider)
    {
        var fixture = new Fixture(provider);
        fixture.Override = request => request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal)
            ? null
            : Json(
                new[]
                {
                    new
                    {
                        id = 42, number = 42, iid = 42, title = "Native pull request", state = provider == ScmProvider.GitLab ? "opened" : "open",
                        draft = true, html_url = "https://scm.example.test/pull/42", web_url = "https://scm.example.test/merge_requests/42",
                        head = new { @ref = "feature/overview", sha = "head-sha" }, @base = new { @ref = "main", sha = "base-sha" },
                        source_branch = "feature/overview", target_branch = "main", sha = "head-sha",
                        user = new { id = 99, login = "native-login", name = "Native Author", full_name = "Native Author" },
                        author = new { id = 99, username = "native-login", name = "Native Author" },
                    }
                });
        var result = await fixture.Discovery.ListOpenReviewsAsync(
            fixture.ClientId, fixture.Review.Repository, null,
            context: fixture.Context);
        var item = Assert.Single(result);
        Assert.Equal("Native Author", item.AuthorName);
        Assert.Equal("feature/overview", item.SourceBranch);
        Assert.Equal("main", item.TargetBranch);
        Assert.Equal("head-sha", item.ReviewRevision!.HeadSha);
        Assert.Equal("head-sha", item.ReviewRevision.ProviderRevisionId);
        Assert.Equal(CodeReviewState.Draft, item.ReviewState);
        Assert.Equal(2, fixture.Requests);
    }

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task Overview_CancellationDuringPaginationRemainsCancellation(ScmProvider provider)
    {
        var fixture = new Fixture(provider);
        using var cancellation = new CancellationTokenSource();
        fixture.Override = request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal))
            {
                return null;
            }

            cancellation.Cancel();
            return Json(Enumerable.Range(1, 100).Select(id => new { id }).ToArray());
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context, cancellation.Token));
    }

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task Overview_UsesSelectedBasePathForAllMetadataRequests(ScmProvider provider)
    {
        var fixture = new Fixture(provider, "/installed");
        fixture.Override = request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal))
            {
                Assert.StartsWith("/installed/", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
            }

            return null;
        };
        Assert.True((await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context)).IsComplete);
    }

    [Fact]
    public async Task GitHubOverview_MissingThreadStateIsUnavailable()
    {
        var fixture = new Fixture(ScmProvider.GitHub)
        {
            Override = request => request.RequestUri!.AbsolutePath.EndsWith("/graphql", StringComparison.Ordinal)
                ? Threads([new { id = "thread" }])
                : null,
        };
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Equal(5, overview.TotalComments);
        Assert.Null(overview.ResolvedDiscussions);
        Assert.Null(overview.UnresolvedDiscussions);
        Assert.False(overview.IsComplete);
    }

    [Fact]
    public async Task GitLabOverview_MalformedNoteDoesNotProduceKnownCounts()
    {
        var fixture = new Fixture(ScmProvider.GitLab)
        {
            Override = request => request.RequestUri!.AbsolutePath.EndsWith("/discussions", StringComparison.Ordinal)
                ? Json(
                    new[] { new { id = "discussion", individual_note = false, notes = new[] { new { system = false, resolvable = true, resolved = false } } } })
                : null,
        };
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Null(overview.TotalComments);
        Assert.Null(overview.UnresolvedDiscussions);
        Assert.False(overview.IsComplete);
    }

    [Theory]
    [InlineData(ScmProvider.GitHub, 5, 1, 1, true)]
    [InlineData(ScmProvider.GitLab, 4, 1, 1, true)]
    [InlineData(ScmProvider.Forgejo, 3, null, null, false)]
    public async Task Overview_CountsAllAuthorsAndDistinguishesMessagesFromDiscussions(
        ScmProvider provider, int total, int? resolved, int? unresolved, bool supported)
    {
        var fixture = new Fixture(provider);
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Equal(total, overview.TotalComments);
        Assert.Equal(resolved, overview.ResolvedDiscussions);
        Assert.Equal(unresolved, overview.UnresolvedDiscussions);
        Assert.Equal(supported, overview.ResolutionSupported);
        Assert.True(overview.IsComplete);
        await fixture.Connections.Received(1).GetOperationalConnectionByIdAsync(fixture.ClientId, fixture.Context.ConnectionId, Arg.Any<CancellationToken>());
        await fixture.Connections.DidNotReceiveWithAnyArgs().GetOperationalConnectionAsync(default, default!, default);
    }

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task Overview_MissingSelectedCredentialNeverUsesAnAlternative(ScmProvider provider)
    {
        var fixture = new Fixture(provider);
        fixture.Connections.GetOperationalConnectionByIdAsync(fixture.ClientId, fixture.Context.ConnectionId, Arg.Any<CancellationToken>())
            .Returns((ClientScmConnectionCredentialDto?)null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context));
        Assert.Equal(0, fixture.Requests);
        await fixture.Connections.DidNotReceiveWithAnyArgs().GetOperationalConnectionAsync(default, default!, default);
    }

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task Overview_ForeignCredentialIsRejectedBeforeNetworkAccess(ScmProvider provider)
    {
        var fixture = new Fixture(provider);
        fixture.Connections.GetOperationalConnectionByIdAsync(fixture.ClientId, fixture.Context.ConnectionId, Arg.Any<CancellationToken>())
            .Returns(fixture.Credential with { ClientId = Guid.NewGuid() });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context));
        Assert.Equal(0, fixture.Requests);
    }

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task Overview_PropagatesCallerCancellation(ScmProvider provider)
    {
        var fixture = new Fixture(provider);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context, cancelled.Token));
    }

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task Overview_ProviderFailureDoesNotReturnZeroCounts(ScmProvider provider)
    {
        var fixture = new Fixture(provider)
        {
            Override = request => request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal)
                ? null
                : new HttpResponseMessage(HttpStatusCode.TooManyRequests),
        };
        await Assert.ThrowsAsync<ProviderThrottledException>(() =>
            fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context));
    }

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task Overview_MalformedCollectionIsUnavailableInsteadOfEmpty(ScmProvider provider)
    {
        var fixture = new Fixture(provider)
        {
            Override = request => request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal)
                ? null
                : Json(new { unexpected = true }),
        };
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Null(overview.TotalComments);
        Assert.False(overview.IsComplete);
    }

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task Overview_SuccessfulEmptyCollectionsHaveKnownZeroMessages(ScmProvider provider)
    {
        var fixture = new Fixture(provider)
        {
            Override = request => request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal)
                ? null
                : request.RequestUri.AbsolutePath.EndsWith("/graphql", StringComparison.Ordinal)
                    ? Threads([])
                    : Json(Array.Empty<object>()),
        };
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Equal(0, overview.TotalComments);
        Assert.True(overview.IsComplete);
        Assert.Equal(provider == ScmProvider.Forgejo ? null : 0, overview.ResolvedDiscussions);
        Assert.Equal(provider == ScmProvider.Forgejo ? null : 0, overview.UnresolvedDiscussions);
    }

    [Fact]
    public async Task GitHubOverview_ReadsMultipleCommentPages()
    {
        var fixture = new Fixture(ScmProvider.GitHub)
        {
            Override = request => request.RequestUri!.AbsolutePath.Contains("/issues/", StringComparison.Ordinal)
                ? Json(
                    request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal)
                        ? new[] { new { id = 101 } }
                        : Enumerable.Range(1, 100).Select(id => new { id }).ToArray())
                : null,
        };
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Equal(104, overview.TotalComments);
        Assert.True(overview.IsComplete);
    }

    [Fact]
    public async Task GitHubOverview_RepeatedPageIsIncompleteAndBounded()
    {
        var fixture = new Fixture(ScmProvider.GitHub)
        {
            Override = request => request.RequestUri!.AbsolutePath.Contains("/issues/", StringComparison.Ordinal)
                ? Json(Enumerable.Range(1, 100).Select(id => new { id }).ToArray())
                : null,
        };
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Null(overview.TotalComments);
        Assert.False(overview.IsComplete);
        Assert.InRange(fixture.Requests, 2, 13);
    }

    [Fact]
    public async Task GitHubOverview_GraphQlErrorsDoNotBecomeZeroDiscussions()
    {
        var fixture = new Fixture(ScmProvider.GitHub)
        {
            Override = request => request.RequestUri!.AbsolutePath.EndsWith("/graphql", StringComparison.Ordinal)
                ? Json(new { errors = new[] { new { message = "private provider detail" } } })
                : null,
        };
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Equal(5, overview.TotalComments);
        Assert.Null(overview.ResolvedDiscussions);
        Assert.Null(overview.UnresolvedDiscussions);
        Assert.False(overview.IsComplete);
    }

    [Fact]
    public async Task GitLabOverview_AbsentResolvabilityDoesNotInventUnresolvedThreads()
    {
        var fixture = new Fixture(ScmProvider.GitLab)
        {
            Override = request => request.RequestUri!.AbsolutePath.EndsWith("/discussions", StringComparison.Ordinal)
                ? Json(
                    new[]
                    {
                        new
                        {
                            id = "d", individual_note = false,
                            notes = new[] { new { id = 1, system = false, resolved = false } }
                        }
                    })
                : null,
        };
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Equal(1, overview.TotalComments);
        Assert.Null(overview.UnresolvedDiscussions);
        Assert.False(overview.IsComplete);
    }

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public async Task Overview_RequestBudgetDoesNotReturnTruncatedTotalsAsComplete(ScmProvider provider)
    {
        var fixture = new Fixture(provider);
        var page = 0;
        fixture.Override = request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/user", StringComparison.Ordinal))
            {
                return null;
            }

            return Json(Enumerable.Range(++page * 100, 100).Select(id => new { id }).ToArray());
        };
        var overview = await fixture.Provider.GetOverviewAsync(fixture.ClientId, fixture.Review, fixture.Context);
        Assert.Null(overview.TotalComments);
        Assert.False(overview.IsComplete);
        Assert.InRange(fixture.Requests, 2, 13);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private static HttpResponseMessage Threads(object[] nodes) => Json(
        new
        {
            data = new
            {
                repository = new
                {
                    pullRequest = new
                    {
                        reviewThreads = new { nodes, pageInfo = new { hasNextPage = false, endCursor = (string?)null } },
                    }
                }
            },
        });

    private sealed class Fixture
    {
        public Guid ClientId { get; } = Guid.NewGuid();
        public ReviewDiscoveryContext Context { get; }
        public IClientScmConnectionRepository Connections { get; } = Substitute.For<IClientScmConnectionRepository>();
        public ClientScmConnectionCredentialDto Credential { get; }
        public CodeReviewRef Review { get; }
        public IReviewOverviewProvider Provider { get; }
        public IReviewDiscoveryProvider Discovery { get; }
        public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }
        public int Requests { get; private set; }

        public Fixture(ScmProvider provider, string basePath = "")
        {
            this.Context = new(Guid.NewGuid(), "https://scm.example.test" + basePath);
            var host = new ProviderHostRef(provider, this.Context.ProviderScopePath);
            this.Credential = new(
                this.Context.ConnectionId, this.ClientId, provider, this.Context.ProviderScopePath,
                ScmAuthenticationKind.PersonalAccessToken, "Fixture", "fixture-token", true);
            this.Connections.GetOperationalConnectionByIdAsync(this.ClientId, this.Context.ConnectionId, Arg.Any<CancellationToken>())
                .Returns(this.Credential);
            this.Review = new(new RepositoryRef(host, "101", "team", "team/repo"), CodeReviewPlatformKind.PullRequest, "42", 42);
            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient(Arg.Any<string>()).Returns(
                new HttpClient(
                    new Handler(request =>
                    {
                        this.Requests++;
                        Assert.Equal(
                            "fixture-token", request.Headers.Authorization?.Parameter ??
                                             request.Headers.GetValues("PRIVATE-TOKEN").SingleOrDefault());
                        if (this.Override?.Invoke(request) is { } overridden)
                        {
                            return overridden;
                        }

                        var path = request.RequestUri!.AbsolutePath;
                        if (path.EndsWith("/user", StringComparison.Ordinal))
                        {
                            return Json(new { login = "fixture", username = "fixture" });
                        }

                        if (path.EndsWith("/graphql", StringComparison.Ordinal))
                        {
                            return Threads([new { id = "resolved", isResolved = true }, new { id = "unresolved", isResolved = false }]);
                        }

                        if (provider == ScmProvider.GitLab)
                        {
                            return Json(
                                new object[]
                                {
                                    new
                                    {
                                        id = "d1", individual_note = false, notes = new object[]
                                        {
                                            new { id = 1, system = false, resolvable = true, resolved = false },
                                            new { id = 2, system = true, resolvable = false, resolved = false },
                                        }
                                    },
                                    new
                                    {
                                        id = "d2", individual_note = false, notes = new object[]
                                        {
                                            new { id = 3, system = false, resolvable = true, resolved = true },
                                            new { id = 4, system = false, resolvable = false, resolved = false },
                                        }
                                    },
                                    new
                                    {
                                        id = "d3", individual_note = true, notes = new object[]
                                        {
                                            new { id = 5, system = false, resolvable = false, resolved = false },
                                        }
                                    },
                                });
                        }

                        if (path.Contains("/issues/", StringComparison.Ordinal))
                        {
                            return Json(new[] { new { id = 1 }, new { id = 2 } });
                        }

                        if (path.EndsWith("/reviews", StringComparison.Ordinal))
                        {
                            return Json(
                                new[]
                                {
                                    new
                                    {
                                        id = 7, body = "", state = provider == ScmProvider.GitHub ? "COMMENTED" : "APPROVED",
                                        submitted_at = "2026-10-03T12:00:00Z"
                                    }
                                });
                        }

                        return Json(
                            provider == ScmProvider.Forgejo
                                ? new[] { new { id = 3 } }
                                : new[] { new { id = 10 }, new { id = 11 }, new { id = 12 } });
                    })));
            this.Provider = provider switch
            {
                ScmProvider.GitHub => new GitHubReviewOverviewProvider(new GitHubConnectionVerifier(this.Connections, factory), factory),
                ScmProvider.GitLab => new GitLabReviewOverviewProvider(new GitLabConnectionVerifier(this.Connections, factory), factory),
                ScmProvider.Forgejo => new ForgejoReviewOverviewProvider(new ForgejoConnectionVerifier(this.Connections, factory), factory),
                _ => throw new ArgumentOutOfRangeException(nameof(provider)),
            };
            this.Discovery = provider switch
            {
                ScmProvider.GitHub => new GitHubReviewDiscoveryProvider(new GitHubConnectionVerifier(this.Connections, factory), factory),
                ScmProvider.GitLab => new GitLabReviewDiscoveryProvider(new GitLabConnectionVerifier(this.Connections, factory), factory),
                ScmProvider.Forgejo => new ForgejoReviewDiscoveryProvider(new ForgejoConnectionVerifier(this.Connections, factory), factory),
                _ => throw new ArgumentOutOfRangeException(nameof(provider)),
            };
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
