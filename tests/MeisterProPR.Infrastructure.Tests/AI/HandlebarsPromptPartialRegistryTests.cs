// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class HandlebarsPromptPartialRegistryTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"meisterpropr-partials-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(this._rootPath))
        {
            Directory.Delete(this._rootPath, true);
        }
    }

    [Fact]
    public void GetPartials_WhenSharedPartialsExist_ReturnsNamedPartials()
    {
        var partialsPath = Path.Combine(this._rootPath, "AI", "Prompts", "shared", "partials");
        Directory.CreateDirectory(partialsPath);
        File.WriteAllText(Path.Combine(partialsPath, "output-key-reminder.hbs"), "Use {{key}}.");
        File.WriteAllText(Path.Combine(partialsPath, "system-prompt.hbs"), "System {{name}}");

        var provider = new PromptTemplateFileProvider(this._rootPath);
        var registry = new PromptTemplatePartialRegistry(provider);

        var partials = registry.GetPartials();

        Assert.Equal(2, partials.Count);
        Assert.Equal("Use {{key}}.", partials["output-key-reminder"]);
        Assert.Equal("System {{name}}", partials["system-prompt"]);
    }

    [Fact]
    public void GetPartials_WhenPartialDirectoryMissing_ReturnsEmptyDictionary()
    {
        Directory.CreateDirectory(Path.Combine(this._rootPath, "AI", "Prompts", "shared"));

        var provider = new PromptTemplateFileProvider(this._rootPath);
        var registry = new PromptTemplatePartialRegistry(provider);

        var partials = registry.GetPartials();

        Assert.Empty(partials);
    }
}
