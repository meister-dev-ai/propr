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
    [InlineData("LicenseLimitKey", "authorsPerMonth,clients,runners,concurrentReviews")]
    [InlineData("LicenseStage", "none,notYetValid,active,warning,grace,reverted")]
    [InlineData("PremiumCapabilityUnavailableReason", "noLicense,notInLicense,disabledByOverride,reverted,notYetValid")]
    public async Task ForwardCompatibleLicensingValues_AreDocumentedAsUnrestrictedStrings(string schemaName, string knownValues)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(OpenApiPath));
        var schema = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(schemaName);

        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.False(schema.TryGetProperty("enum", out _));
        var description = schema.GetProperty("description").GetString();
        foreach (var knownValue in knownValues.Split(',', StringSplitOptions.TrimEntries))
        {
            Assert.Contains(knownValue, description, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("LicenseLimitDto", "effectiveCeiling", "LicenseLimitCeiling")]
    [InlineData("LicenseLimitDto", "effectiveSource", "LicenseLimitSource")]
    [InlineData("LicensingSummaryDto", "authorOverage", "AuthorOverageDto")]
    [InlineData("LicensingSummaryDto", "authorPeakMonth", "AuthorPeakMonthDto")]
    [InlineData("PremiumCapabilityDto", "reason", "PremiumCapabilityUnavailableReason")]
    [InlineData("SystemProfileDto", "current", "SystemProfileSnapshotDto")]
    public async Task NullableReferencedProperties_UseAnOpenApi30NullableAllOfSchema(
        string schemaName,
        string propertyName,
        string referencedSchemaName)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(OpenApiPath));
        var property = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(schemaName)
            .GetProperty("properties")
            .GetProperty(propertyName);

        Assert.True(property.GetProperty("nullable").GetBoolean());

        var allOf = Assert.Single(property.GetProperty("allOf").EnumerateArray());
        Assert.Equal($"#/components/schemas/{referencedSchemaName}", allOf.GetProperty("$ref").GetString());
    }

    // The cases above read the checked-in document, so they report a regression only once someone regenerates it.
    // This one generates the schema in the test and asserts what the filter produced, which holds the filter to the
    // DTO's current property names whether or not the document has been regenerated.
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

    private static string OpenApiPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "openapi.json"));
}
