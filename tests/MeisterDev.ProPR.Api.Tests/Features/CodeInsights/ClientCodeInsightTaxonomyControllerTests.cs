// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Features.CodeInsights.Controllers;
using MeisterDev.ProPR.Application.Features.CodeInsights.Taxonomy;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Features.CodeInsights;

public sealed class ClientCodeInsightTaxonomyControllerTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TagId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly CodeInsightCustomTagWriteRequest Request =
        new("domain-rule", "Domain rule", "Violates one of our business rules.");

    [Fact]
    public async Task ReadingTheVocabulary_NeedsClientAccessOnly()
    {
        var service = CreateService();
        var controller = CreateController(service, ClientRole.ClientUser);

        var result = await controller.GetTaxonomy(ClientId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        await service.Received(1).GetTaxonomyAsync(ClientId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnauthenticatedCaller_CannotReadTheVocabulary()
    {
        var service = CreateService();
        var controller = CreateController(service, role: null);

        var result = await controller.GetTaxonomy(ClientId, CancellationToken.None);

        Assert.Equal(StatusCodes.Status401Unauthorized, ((ObjectResult)result).StatusCode);
        await service.DidNotReceive().GetTaxonomyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ACallerWithoutAccessToThisClient_CannotReadItsVocabulary()
    {
        var service = CreateService();
        var controller = CreateController(service, ClientRole.ClientAdministrator, roleForClientId: Guid.NewGuid());

        var result = await controller.GetTaxonomy(ClientId, CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, ((ObjectResult)result).StatusCode);
        await service.DidNotReceive().GetTaxonomyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task APlainClientUser_CannotCreateUpdateOrRetireACustomTag()
    {
        // The vocabulary decides how every finding is labelled from now on, so changing it is an
        // administrative act even though reading it is not.
        var service = CreateService();
        var controller = CreateController(service, ClientRole.ClientUser);

        var create = await controller.CreateCustomTag(ClientId, Request, CancellationToken.None);
        var update = await controller.UpdateCustomTag(ClientId, TagId, Request, CancellationToken.None);
        var retire = await controller.RetireCustomTag(ClientId, TagId, CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, ((ObjectResult)create).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, ((ObjectResult)update).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, ((ObjectResult)retire).StatusCode);

        await service.DidNotReceive().CreateCustomTagAsync(
            Arg.Any<Guid>(),
            Arg.Any<CodeInsightCustomTagWriteRequest>(),
            Arg.Any<CancellationToken>());
        await service.DidNotReceive().UpdateCustomTagAsync(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<CodeInsightCustomTagWriteRequest>(),
            Arg.Any<CancellationToken>());
        await service.DidNotReceive().RetireCustomTagAsync(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AClientAdministrator_CanCreateACustomTag()
    {
        var service = CreateService();
        var controller = CreateController(service, ClientRole.ClientAdministrator);

        var result = await controller.CreateCustomTag(ClientId, Request, CancellationToken.None);

        var created = Assert.IsType<CreatedResult>(result);
        Assert.Equal(
            $"/clients/{ClientId}/code-insights/taxonomy/custom-tags/{TagId}",
            created.Location);
    }

    [Theory]
    [InlineData(CodeInsightCustomTagWriteError.ShadowsCoreTag, StatusCodes.Status409Conflict)]
    [InlineData(CodeInsightCustomTagWriteError.SlugAlreadyUsed, StatusCodes.Status409Conflict)]
    [InlineData(CodeInsightCustomTagWriteError.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(CodeInsightCustomTagWriteError.Invalid, StatusCodes.Status400BadRequest)]
    public async Task ARejectionMapsToAStatusThatTellsTheOperatorWhatWentWrong(
        CodeInsightCustomTagWriteError error,
        int expectedStatus)
    {
        var service = CreateService();
        service.CreateCustomTagAsync(
                Arg.Any<Guid>(),
                Arg.Any<CodeInsightCustomTagWriteRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(CodeInsightCustomTagWriteResult.Rejected(error, "explanation"));
        var controller = CreateController(service, ClientRole.ClientAdministrator);

        var result = await controller.CreateCustomTag(ClientId, Request, CancellationToken.None);

        Assert.Equal(expectedStatus, ((ObjectResult)result).StatusCode);
    }

    private static ICodeInsightTaxonomyService CreateService()
    {
        var service = Substitute.For<ICodeInsightTaxonomyService>();
        service.GetTaxonomyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new CodeInsightTaxonomyDto(CodeInsightCoreTaxonomy.Version, [], []));

        var tag = new CodeInsightCustomTagDto(
            TagId,
            "domain-rule",
            "Domain rule",
            "Violates one of our business rules.",
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var success = CodeInsightCustomTagWriteResult.Success(tag);

        service.CreateCustomTagAsync(
                Arg.Any<Guid>(),
                Arg.Any<CodeInsightCustomTagWriteRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(success);
        service.UpdateCustomTagAsync(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<CodeInsightCustomTagWriteRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(success);
        service.RetireCustomTagAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(success);

        return service;
    }

    private static ClientCodeInsightTaxonomyController CreateController(
        ICodeInsightTaxonomyService service,
        ClientRole? role,
        Guid? roleForClientId = null)
    {
        var httpContext = new DefaultHttpContext();
        if (role.HasValue)
        {
            httpContext.Items["UserId"] = Guid.NewGuid().ToString();
            httpContext.Items["ClientRoles"] = new Dictionary<Guid, ClientRole>
            {
                [roleForClientId ?? ClientId] = role.Value,
            };
        }

        return new ClientCodeInsightTaxonomyController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }
}
