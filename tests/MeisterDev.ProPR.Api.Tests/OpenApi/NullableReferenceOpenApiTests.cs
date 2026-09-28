// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text.Json;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Api.OpenApi;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MeisterDev.ProPR.Api.Tests.OpenApi;

public sealed class NullableReferenceOpenApiTests
{
    // The scope is null on a review stopped by a refusal that named no cap. Emitted as a bare reference, the
    // document promises a scope on every budget status and the generated client types it as always present.
    [Theory]
    [InlineData(typeof(JobsController.BudgetStatusDto), "scope", "BudgetScopeKind")]
    public void Filter_WrapsTheNullableReferencedPropertyInANullableAllOf(
        Type dtoType,
        string propertyName,
        string referencedSchemaName)
    {
        var repository = new SchemaRepository();
        var generator = new SchemaGenerator(
            new SchemaGeneratorOptions { SchemaFilters = { new NullableReferenceSchemaFilter() } },
            new JsonSerializerDataContractResolver(new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        generator.GenerateSchema(dtoType, repository);

        var dtoSchema = Assert.IsType<OpenApiSchema>(repository.Schemas[dtoType.Name]);
        var property = Assert.IsType<OpenApiSchema>(dtoSchema.Properties?[propertyName]);

        using var document = JsonDocument.Parse(SerializeAsV3(property));
        Assert.True(document.RootElement.GetProperty("nullable").GetBoolean());

        var allOf = Assert.Single(document.RootElement.GetProperty("allOf").EnumerateArray());
        Assert.Equal($"#/components/schemas/{referencedSchemaName}", allOf.GetProperty("$ref").GetString());
    }

    // The properties the filter leaves alone keep the schema the generator produced, so a cap kind the API
    // always sends is not documented as optional.
    [Fact]
    public void Filter_LeavesAPropertyItDoesNotNameUntouched()
    {
        var repository = new SchemaRepository();
        var generator = new SchemaGenerator(
            new SchemaGeneratorOptions { SchemaFilters = { new NullableReferenceSchemaFilter() } },
            new JsonSerializerDataContractResolver(new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        generator.GenerateSchema(typeof(JobsController.BudgetStatusDto), repository);

        var dtoSchema = Assert.IsType<OpenApiSchema>(repository.Schemas[nameof(JobsController.BudgetStatusDto)]);
        var property = dtoSchema.Properties?["capKind"];

        using var document = JsonDocument.Parse(SerializeAsV3(property!));
        Assert.False(document.RootElement.TryGetProperty("nullable", out _));
        Assert.Equal("#/components/schemas/BudgetCapKind", document.RootElement.GetProperty("$ref").GetString());
    }

    private static string SerializeAsV3(IOpenApiSchema schema)
    {
        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        schema.SerializeAsV3(new OpenApiJsonWriter(buffer));
        return buffer.ToString();
    }
}
