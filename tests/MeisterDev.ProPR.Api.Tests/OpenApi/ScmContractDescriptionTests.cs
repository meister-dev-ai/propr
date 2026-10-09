// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.Swagger;

namespace MeisterDev.ProPR.Api.Tests.OpenApi;

public sealed class ScmContractDescriptionTests
{
    [Theory]
    [InlineData("ReviewOverviewDto", "resolutionSupported", "Whether the provider exposes discussion resolution; Forgejo does not.")]
    [InlineData(
        "ReviewJobProtocolDto", "passKind",
        "The kind of review pass — the `ReviewPassKind` name (e.g. `\"Baseline\"`,\n`\"MultiPassUnion\"`). null for legacy rows and passes with no\nmeaningful kind (e.g. synthesis, which the UI derives from MeisterDev.ProPR.Application.DTOs.ReviewJobProtocolDto.Label).")]
    public void EmittedSchema_PreservesExistingHttpDescription(string schemaName, string propertyName, string expected)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var scope = factory.Services.CreateScope();
        var document = scope.ServiceProvider.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");

        Assert.Equal(expected, document.Components!.Schemas![schemaName].Properties![propertyName].Description);
    }
}
