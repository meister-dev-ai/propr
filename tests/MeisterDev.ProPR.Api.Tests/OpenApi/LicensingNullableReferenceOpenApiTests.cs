// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text.Json;
using MeisterDev.ProPR.Api.OpenApi;
using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MeisterDev.ProPR.Api.Tests.OpenApi;

public sealed class LicensingNullableReferenceOpenApiTests
{
    [Theory]
    [InlineData(typeof(LicenseLimitDto), "effectiveCeiling", "LicenseLimitCeiling")]
    [InlineData(typeof(LicenseLimitDto), "effectiveSource", "LicenseLimitSource")]
    [InlineData(typeof(LicensingSummaryDto), "authorOverage", "AuthorOverageDto")]
    [InlineData(typeof(LicensingSummaryDto), "authorPeakMonth", "AuthorPeakMonthDto")]
    [InlineData(typeof(PremiumCapabilityDto), "reason", "PremiumCapabilityUnavailableReason")]
    [InlineData(typeof(SystemProfileDto), "current", "SystemProfileSnapshotDto")]
    public void Filter_WrapsTheNullableReferencedPropertyInANullableAllOf(
        Type dtoType,
        string propertyName,
        string referencedSchemaName)
    {
        var repository = new SchemaRepository();
        var generator = new SchemaGenerator(
            new SchemaGeneratorOptions { SchemaFilters = { new LicensingNullableReferenceSchemaFilter() } },
            new JsonSerializerDataContractResolver(new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        generator.GenerateSchema(dtoType, repository);

        var dtoSchema = Assert.IsType<OpenApiSchema>(repository.Schemas[dtoType.Name]);
        var property = Assert.IsType<OpenApiSchema>(dtoSchema.Properties?[propertyName]);

        using var document = JsonDocument.Parse(SerializeAsV3(property));
        Assert.True(document.RootElement.GetProperty("nullable").GetBoolean());

        var allOf = Assert.Single(document.RootElement.GetProperty("allOf").EnumerateArray());
        Assert.Equal($"#/components/schemas/{referencedSchemaName}", allOf.GetProperty("$ref").GetString());
    }

    private static string SerializeAsV3(IOpenApiSchema schema)
    {
        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        schema.SerializeAsV3(new OpenApiJsonWriter(buffer));
        return buffer.ToString();
    }
}
