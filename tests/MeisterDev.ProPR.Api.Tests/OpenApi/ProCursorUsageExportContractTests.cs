// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.Swagger;

namespace MeisterDev.ProPR.Api.Tests.OpenApi;

public sealed class ProCursorUsageExportContractTests
{
    // The export answers with CSV when it succeeds. Its refusals and the answer of an installation without ProCursor are
    // JSON, and a client generated from the document reads them as JSON only when the document declares them so.
    [Fact]
    public void EmittedDocument_DeclaresOnlyTheSuccessfulAnswerOfTheExportAsCsv()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Testing").UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true"));
        using var scope = factory.Services.CreateScope();
        var document = scope.ServiceProvider.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var export = document.Paths!["/admin/clients/{clientId}/procursor/token-usage/export"].Operations![HttpMethod.Get];

        Assert.Equal("text/csv", Assert.Single(export.Responses!["200"].Content!.Keys));
        Assert.Equal("application/problem+json", Assert.Single(export.Responses["404"].Content!.Keys));
        Assert.All(
            new[] { "400", "401", "403" },
            status => Assert.DoesNotContain("text/csv", export.Responses[status].Content!.Keys));
    }
}
