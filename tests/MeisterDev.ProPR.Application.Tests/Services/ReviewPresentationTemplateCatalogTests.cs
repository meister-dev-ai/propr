// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.Globalization;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

namespace MeisterDev.ProPR.Application.Tests.Services;

public sealed class ReviewPresentationTemplateCatalogTests
{
    [Fact]
    public void ConcurrentRenderingLoadsAndCompilesEachTemplateOnceWithoutSharingModels()
    {
        var loads = new ConcurrentDictionary<ReviewPresentationTemplate, int>();
        var compilations = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var catalog = new ReviewPresentationTemplateCatalog(
            template =>
            {
                loads.AddOrUpdate(template, 1, (_, count) => count + 1);
                return template.ToString();
            },
            text =>
            {
                compilations.AddOrUpdate(text, 1, (_, count) => count + 1);
                return model => text + ":" + ((Probe)model).Value;
            });
        var templates = Enum.GetValues<ReviewPresentationTemplate>();

        Parallel.For(
            0, 100, new ParallelOptions { MaxDegreeOfParallelism = 4 }, index =>
            {
                foreach (var template in templates)
                {
                    var value = "{{literal}} <code> " + index;
                    Assert.Equal(template + ":" + value, catalog.Render(template, new Probe(value)));
                }
            });

        Assert.Equal(templates.Length, loads.Count);
        Assert.Equal(templates.Length, compilations.Count);
        Assert.All(loads.Values, count => Assert.Equal(1, count));
        Assert.All(compilations.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public void ConcurrentFirstUseDoesNotCompileDifferentTemplatesInOneEnvironmentAtTheSameTime()
    {
        var activeCompilations = 0;
        var catalog = new ReviewPresentationTemplateCatalog(
            template => template.ToString(),
            text =>
            {
                Assert.Equal(1, Interlocked.Increment(ref activeCompilations));
                Thread.Yield();
                Interlocked.Decrement(ref activeCompilations);
                return _ => text;
            });

        Parallel.ForEach(
            Enum.GetValues<ReviewPresentationTemplate>(), new ParallelOptions { MaxDegreeOfParallelism = 4 }, template =>
                Assert.Equal(template.ToString(), catalog.Render(template, new Probe("value"))));
    }

    [Fact]
    public void EmptyTemplateFailsOnceWithoutInvokingCompiler()
    {
        var loads = 0;
        var catalog = new ReviewPresentationTemplateCatalog(
            _ =>
            {
                loads++;
                return " \n\t";
            },
            _ => throw new InvalidOperationException("The compiler must not receive empty templates."));

        for (var index = 0; index < 2; index++)
        {
            var error = Assert.Throws<InvalidOperationException>(() => catalog.Render(ReviewPresentationTemplate.Finding, new Probe("value")));
            Assert.Contains("empty", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(1, loads);
    }

    [Fact]
    public void InvalidTemplateKeyDoesNotLoadOrCompileAnyTemplate()
    {
        var catalog = new ReviewPresentationTemplateCatalog(
            _ => throw new InvalidOperationException("Unexpected resource load."),
            _ => throw new InvalidOperationException("Unexpected compilation."));

        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.Render((ReviewPresentationTemplate)int.MaxValue, new Probe("value")));
    }

    [Fact]
    public void CompiledTemplateRendersDistinctConcurrentModelsWithoutEscapingTheirPreparedBytes()
    {
        var loads = 0;
        var catalog = new ReviewPresentationTemplateCatalog(_ =>
        {
            Interlocked.Increment(ref loads);
            return "{{Value}}";
        });

        Parallel.For(
            0, 100, new ParallelOptions { MaxDegreeOfParallelism = 4 }, index =>
            {
                var value = "{{#if x}} <tag> | ` 😀\r\n" + index;
                Assert.Equal(value, catalog.Render(ReviewPresentationTemplate.Finding, new Probe(value)));
            });

        Assert.Equal(1, loads);
    }

    private sealed record Probe(string Value);

    [Fact]
    public void NumericRenderingUsesInvariantCulture()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var catalog = new ReviewPresentationTemplateCatalog(_ => "{{Number}}");

            Assert.Equal("1234.5", catalog.Render(ReviewPresentationTemplate.Summary, new { Number = 1234.5m }));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
