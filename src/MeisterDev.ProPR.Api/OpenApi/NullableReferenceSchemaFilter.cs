// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Controllers;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MeisterDev.ProPR.Api.OpenApi;

/// <summary>
///     Preserves nullability on properties whose schemas are emitted as references.
/// </summary>
/// <remarks>
///     OpenAPI 3.0 ignores metadata beside a direct reference. Wrapping the reference in <c>allOf</c> gives the
///     property its own schema, where the OpenAPI 3.0 writer can emit <c>nullable: true</c> and generated clients
///     can represent the response value accurately.
/// </remarks>
public sealed class NullableReferenceSchemaFilter : ISchemaFilter
{
    // The budget scope is null on a review stopped by a refusal that named no cap. A hard-cap refusal relayed
    // from another replica is one such refusal. Emitted as a bare reference, the document promises a scope on
    // every budget status and the generated client types it as always present.
    private static readonly IReadOnlyDictionary<Type, string[]> NullableReferenceProperties =
        new Dictionary<Type, string[]>
        {
            [typeof(JobsController.BudgetStatusDto)] = ["scope"],
        };

    /// <inheritdoc />
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema objectSchema
            || objectSchema.Properties is null
            || !NullableReferenceProperties.TryGetValue(context.Type, out var propertyNames))
        {
            return;
        }

        foreach (var propertyName in propertyNames)
        {
            if (!objectSchema.Properties.TryGetValue(propertyName, out var propertySchema))
            {
                continue;
            }

            objectSchema.Properties[propertyName] = new OpenApiSchema
            {
                Type = JsonSchemaType.Null,
                AllOf = [propertySchema],
            };
        }
    }
}
