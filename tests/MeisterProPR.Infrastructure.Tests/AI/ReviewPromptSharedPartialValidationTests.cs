// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class ReviewPromptSharedPartialValidationTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"meisterpropr-partial-validation-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(this._rootPath))
        {
            Directory.Delete(this._rootPath, true);
        }
    }

    [Fact]
    public void ValidateStageTemplate_WhenTemplateReferencesMissingPartial_FailsFast()
    {
        Directory.CreateDirectory(Path.Combine(this._rootPath, "AI", "Prompts", "shared"));
        File.WriteAllText(
            Path.Combine(this._rootPath, "AI", "Prompts", "shared", "global-system.hbs"),
            "Before {{> missing-partial}} After");

        var provider = new PromptTemplateFileProvider(this._rootPath);
        var validator = new PromptTemplateValidator(provider);

        var ex = Assert.Throws<InvalidOperationException>((Action)(() => validator.ValidateStageTemplate(PromptStageKeys.GlobalSystem)));

        Assert.Contains("missing-partial", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateStageTemplate_WhenTemplateReferencesInvalidPartialName_FailsFast()
    {
        Directory.CreateDirectory(Path.Combine(this._rootPath, "AI", "Prompts", "shared"));
        Directory.CreateDirectory(Path.Combine(this._rootPath, "AI", "Prompts", "shared", "partials"));
        File.WriteAllText(
            Path.Combine(this._rootPath, "AI", "Prompts", "shared", "global-system.hbs"),
            "Before {{> ../bad-partial}} After");
        File.WriteAllText(
            Path.Combine(this._rootPath, "AI", "Prompts", "shared", "partials", "bad-partial.hbs"),
            "content");

        var provider = new PromptTemplateFileProvider(this._rootPath);
        var validator = new PromptTemplateValidator(provider);

        var ex = Assert.Throws<InvalidOperationException>((Action)(() => validator.ValidateStageTemplate(PromptStageKeys.GlobalSystem)));

        Assert.Contains("../bad-partial", ex.Message, StringComparison.Ordinal);
    }
}
