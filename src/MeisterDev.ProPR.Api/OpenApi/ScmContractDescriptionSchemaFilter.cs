// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MeisterDev.ProPR.Api.OpenApi;

/// <summary>Preserves HTTP schema descriptions independently of internal projection documentation.</summary>
public sealed class ScmContractDescriptionSchemaFilter : ISchemaFilter
{
    /// <inheritdoc />
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema is not OpenApiSchema objectSchema || objectSchema.Properties is null)
        {
            return;
        }

        if (context.Type == typeof(ReviewOverviewDto) &&
            objectSchema.Properties.TryGetValue("resolutionSupported", out var resolution))
        {
            if (resolution is OpenApiSchema resolutionSchema)
            {
                resolutionSchema.Description = "Whether the provider exposes discussion resolution; Forgejo does not.";
            }
        }

        if (context.Type == typeof(MeisterDev.ProPR.Api.Features.Reviewing.Contracts.ReviewJobProtocolDto) &&
            objectSchema.Properties.TryGetValue("passKind", out var passKind))
        {
            if (passKind is OpenApiSchema passKindSchema)
            {
                passKindSchema.Description =
                    "The kind of review pass — the `ReviewPassKind` name (e.g. `\"Baseline\"`,\n`\"MultiPassUnion\"`). null for legacy rows and passes with no\nmeaningful kind (e.g. synthesis, which the UI derives from MeisterDev.ProPR.Application.DTOs.ReviewJobProtocolDto.Label).";
            }
        }
    }
}
