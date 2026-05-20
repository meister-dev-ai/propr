// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class HandlebarsPromptTemplateLoaderTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"meisterpropr-prompts-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(this._rootPath))
        {
            Directory.Delete(this._rootPath, true);
        }
    }

    [Fact]
    public void LoadStageTemplate_WhenTemplateExists_ReturnsTemplateContent()
    {
        Directory.CreateDirectory(Path.Combine(this._rootPath, "AI", "Prompts", "shared"));
        File.WriteAllText(
            Path.Combine(this._rootPath, "AI", "Prompts", "shared", "global-system.hbs"),
            "Hello {{name}}!");

        var provider = new PromptTemplateFileProvider(this._rootPath);

        var content = provider.ReadStageTemplate(PromptStageKeys.GlobalSystem);

        Assert.Equal("Hello {{name}}!", content);
    }

    [Fact]
    public void LoadStageTemplate_WhenTemplateMissing_ThrowsInvalidOperationException()
    {
        Directory.CreateDirectory(Path.Combine(this._rootPath, "AI", "Prompts", "shared"));

        var provider = new PromptTemplateFileProvider(this._rootPath);

        var ex = Assert.Throws<InvalidOperationException>(() => provider.ReadStageTemplate(PromptStageKeys.GlobalSystem));

        Assert.Contains(PromptStageKeys.GlobalSystem, ex.Message, StringComparison.Ordinal);
        Assert.Contains("global-system.hbs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadStageTemplatePathMap_UsesStableCatalogEntries()
    {
        var descriptor = PromptTemplateCatalog.Get(PromptStageKeys.AgenticFileInvestigationSystem);

        Assert.Equal(PromptStageKeys.AgenticFileInvestigationSystem, descriptor.StageKey);
        Assert.Equal(PromptStageRole.System, descriptor.PromptRole);
        Assert.Equal("agentic-file-by-file/investigation-system.hbs", descriptor.TemplateRelativePath);
    }
}
