// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Features.CodeInsights.Controllers;
using MeisterDev.ProPR.Application.Features.CodeInsights;
using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Api.Tests.Features.CodeInsights;

public sealed class JobCodeInsightsControllerTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid JobId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task WithAccessAndALicence_TheClassificationsAreReturned()
    {
        var harness = new Harness();

        var result = await harness.Controller.GetFindingClassifications(JobId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var views = Assert.IsAssignableFrom<IReadOnlyList<CodeInsightFindingClassificationView>>(ok.Value);
        Assert.Single(views);
    }

    [Fact]
    public async Task WithoutTheLicence_AnEmptyListIsReturnedRatherThanAnError()
    {
        // Reading is gated as well as collecting: an installation that collected while Commercial and has since
        // downgraded still holds the rows, and they must stop being served. Empty rather than an error, because
        // "no tags" is a state the review view renders anyway.
        var harness = new Harness(licensed: false);

        var result = await harness.Controller.GetFindingClassifications(JobId);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<CodeInsightFindingClassificationView>>(ok.Value));
        await harness.Store.DidNotReceive()
            .GetClassificationsForJobAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithNoLicensingServiceRegistered_NothingIsServed()
    {
        // Fails closed, matching the collection gate: an edition that cannot be established serves nothing.
        var harness = new Harness(withLicensingService: false);

        var result = await harness.Controller.GetFindingClassifications(JobId);

        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<CodeInsightFindingClassificationView>>(Assert.IsType<OkObjectResult>(result).Value));
    }

    [Fact]
    public async Task AFailingLicenceLookupServesNothingRatherThanThrowing()
    {
        var harness = new Harness();
        harness.Licensing
            .IsEnabledAsync(PremiumCapabilityKey.CodeInsights, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("licensing is unavailable"));

        var result = await harness.Controller.GetFindingClassifications(JobId);

        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<CodeInsightFindingClassificationView>>(Assert.IsType<OkObjectResult>(result).Value));
    }

    [Fact]
    public async Task WithoutTheSliceRegistered_AnEmptyListIsReturned()
    {
        var harness = new Harness(withStore: false);

        var result = await harness.Controller.GetFindingClassifications(JobId);

        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<CodeInsightFindingClassificationView>>(Assert.IsType<OkObjectResult>(result).Value));
    }

    [Fact]
    public async Task AnUnknownJobIsNotFound()
    {
        var harness = new Harness(jobExists: false);

        Assert.IsType<NotFoundResult>(await harness.Controller.GetFindingClassifications(JobId));
    }

    [Fact]
    public async Task AnUnauthenticatedCallerIsRefused()
    {
        var harness = new Harness(role: null);

        var result = await harness.Controller.GetFindingClassifications(JobId);

        Assert.Equal(StatusCodes.Status401Unauthorized, ((ObjectResult)result).StatusCode);
    }

    [Fact]
    public async Task ACallerWithoutAccessToTheJobsClientIsRefused()
    {
        var harness = new Harness(roleForClientId: Guid.NewGuid());

        var result = await harness.Controller.GetFindingClassifications(JobId);

        Assert.Equal(StatusCodes.Status403Forbidden, ((ObjectResult)result).StatusCode);
        await harness.Store.DidNotReceive()
            .GetClassificationsForJobAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private sealed class Harness
    {
        public Harness(
            bool licensed = true,
            bool withLicensingService = true,
            bool withStore = true,
            bool jobExists = true,
            ClientRole? role = ClientRole.ClientUser,
            Guid? roleForClientId = null)
        {
            this.Store = Substitute.For<ICodeInsightClassificationStore>();
            this.Store
                .GetClassificationsForJobAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(
                    new[]
                    {
                        new CodeInsightFindingClassificationView(
                            0,
                            CodeInsightClassificationStatus.Classified,
                            ["logic-error"],
                            [],
                            CodeInsightFindingLevel.Member,
                            CodeInsightFindingQualifier.Missing,
                            0.8),
                    });

            this.Licensing = Substitute.For<ILicensingCapabilityService>();
            this.Licensing
                .IsEnabledAsync(PremiumCapabilityKey.CodeInsights, Arg.Any<CancellationToken>())
                .Returns(ValueTask.FromResult(licensed));

            var jobRepository = Substitute.For<IJobRepository>();
            jobRepository.GetById(JobId).Returns(
                jobExists
                    ? new ReviewJob(JobId, ClientId, "https://dev.azure.com/org", "proj", "repo", 1, 1)
                    : null);

            var httpContext = new DefaultHttpContext();
            if (role.HasValue)
            {
                httpContext.Items["UserId"] = Guid.NewGuid().ToString();
                httpContext.Items["ClientRoles"] = new Dictionary<Guid, ClientRole>
                {
                    [roleForClientId ?? ClientId] = role.Value,
                };
            }

            this.Controller = new JobCodeInsightsController(
                jobRepository,
                withStore ? this.Store : null,
                withLicensingService ? this.Licensing : null)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext },
            };
        }

        public ICodeInsightClassificationStore Store { get; }

        public ILicensingCapabilityService Licensing { get; }

        public JobCodeInsightsController Controller { get; }
    }
}
