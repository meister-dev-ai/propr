// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using HandlebarsDotNet;

namespace MeisterProPR.Infrastructure.AI;

internal sealed class HandlebarsPromptRenderer
{
    private static readonly HandlebarsConfiguration Configuration = new()
    {
        NoEscape = true,
    };

    internal string Render(string template, object? model, IReadOnlyDictionary<string, string>? partials = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        try
        {
            var handlebars = Handlebars.CreateSharedEnvironment(Configuration);

            if (partials is not null)
            {
                foreach (var partial in partials)
                {
                    handlebars.RegisterTemplate(partial.Key, partial.Value);
                }
            }

            var compiled = handlebars.Compile(template);
            return compiled(model);
        }
        catch (Exception ex) when (ex is HandlebarsCompilerException or HandlebarsRuntimeException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to render Handlebars prompt template: {ex.Message}", ex);
        }
    }
}
