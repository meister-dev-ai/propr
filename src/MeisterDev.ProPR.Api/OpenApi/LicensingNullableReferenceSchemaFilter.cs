// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MeisterDev.ProPR.Api.OpenApi;

/// <summary>
///     Preserves nullability on licensing properties whose schemas are emitted as references.
/// </summary>
/// <remarks>
///     OpenAPI 3.0 ignores metadata beside a direct reference. Wrapping the reference in <c>allOf</c> gives the
///     property its own schema, where the OpenAPI 3.0 writer can emit <c>nullable: true</c> and generated clients
///     can represent the response value accurately.
/// </remarks>
public sealed class LicensingNullableReferenceSchemaFilter : ISchemaFilter
{
    private static readonly IReadOnlyDictionary<Type, string[]> NullableReferenceProperties =
        new Dictionary<Type, string[]>
        {
            [typeof(LicenseLimitDto)] = ["effectiveCeiling", "effectiveSource"],
            [typeof(LicensingSummaryDto)] = ["authorOverage", "authorPeakMonth"],
            [typeof(PremiumCapabilityDto)] = ["reason"],
            [typeof(SystemProfileDto)] = ["current"],
        };

    private static readonly HashSet<Type> ForwardCompatibleStringTypes =
    [
        typeof(LicenseLimitKey),
        typeof(LicenseStage),
        typeof(PremiumCapabilityUnavailableReason),
    ];

    /// <inheritdoc />
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is OpenApiSchema forwardCompatibleSchema
            && ForwardCompatibleStringTypes.Contains(context.Type))
        {
            forwardCompatibleSchema.Enum?.Clear();
        }

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
