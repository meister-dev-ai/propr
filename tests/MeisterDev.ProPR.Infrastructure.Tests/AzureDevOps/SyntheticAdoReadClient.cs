// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.WebApi;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

namespace MeisterDev.ProPR.Infrastructure.Tests.AzureDevOps;

internal static class SyntheticAdoReadClient
{
    internal static GitHttpClient Create(HttpStatusCode status, bool metadata, string? throttle = null)
    {
        var name = metadata ? "PullRequestThreadsLocationId" : "ProjectRepoPullRequestsLocationId";
        var id = typeof(GitHttpClient).Assembly.GetTypes()
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .First(field => field.Name == name && field.FieldType == typeof(Guid)).GetValue(null);
        var locations = new ApiResourceLocationCollection();
        locations.AddResourceLocation(
            new()
            {
                Id = metadata ? (Guid)id! : new Guid("9946fd70-0d40-406e-b686-b4744cbbcc37"), Area = "git",
                ResourceName = metadata ? "pullRequestThreads" : "pullRequests",
                RouteTemplate = metadata
                    ? "{project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/threads/{threadId}"
                    : "{project}/_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}",
                ResourceVersion = 1, MinVersion = new Version(1, 0), MaxVersion = new Version(8, 0), ReleasedVersion = new Version(7, 1),
            });
        var pipeline = new AdoOverviewResponseHandler { InnerHandler = new Handler(status, metadata, throttle) };
        var client = new GitHttpClient(new Uri("https://dev.azure.com/acme"), pipeline, true);
        client.SetResourceLocations(locations);
        return client;
    }

    private sealed class Handler(HttpStatusCode status, bool metadata, string? throttle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains("/_apis/git/repositories/", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Assert.EndsWith(metadata ? "/threads" : "/pullRequests", request.RequestUri.AbsolutePath.TrimEnd('/'));
            var response = new HttpResponseMessage(status)
            {
                RequestMessage = request, Content = JsonContent.Create(new { message = "Private detail" }),
            };
            if (throttle == "retry")
            {
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            }

            if (throttle == "remaining")
            {
                response.Headers.Add("x-ratelimit-remaining", "0");
            }

            return Task.FromResult(response);
        }
    }
}
