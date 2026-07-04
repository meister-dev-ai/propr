// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.CodeAnalysis.TreeSitter.Parsing;

namespace MeisterProPR.CodeAnalysis.TreeSitter.Tests;

/// <summary>
///     Smoke check that the upstream TreeSitter.DotNet native libraries load on the
///     current platform and that the LanguageRegistry resolves each supported language.
///     Skipped (not failed) when the native libs are unavailable (e.g. on a non-Linux dev box).
/// </summary>
public sealed class TreeSitterNativeSmokeTests
{
    [Fact]
    public void LanguageRegistry_LoadsAllSupportedLanguages()
    {
        foreach (var language in LanguageRegistry.SupportedLanguages)
        {
            try
            {
                var native = LanguageRegistry.TryGetLanguage(language);
                // If the native lib is missing, TryGetLanguage returns null and we skip —
                // the probe's fail-soft behavior is covered by TreeSitterNativeProbeTests.
                if (native is null)
                {
                    continue;
                }

                Assert.True(native.AbiVersion > 0, $"{language} reported ABI version {native.AbiVersion}");
            }
            catch (Exception ex)
            {
                // Fail-soft: log via the test message and continue, the probe owns the
                // hard "IsAvailable=false" assertion in its own test class.
                Assert.Fail($"Native load for {language} threw: {ex.Message}");
            }
        }
    }

    [Fact]
    public void LanguageRegistry_ResolvesExtensionsToSupportedLanguages()
    {
        Assert.Equal(SupportedLanguage.TypeScript, LanguageRegistry.TryResolveByExtension("src/app.ts"));
        Assert.Equal(SupportedLanguage.Tsx, LanguageRegistry.TryResolveByExtension("view.tsx"));
        Assert.Equal(SupportedLanguage.JavaScript, LanguageRegistry.TryResolveByExtension("lib.js"));
        Assert.Equal(SupportedLanguage.Python, LanguageRegistry.TryResolveByExtension("script.py"));
        Assert.Equal(SupportedLanguage.Go, LanguageRegistry.TryResolveByExtension("main.go"));
        Assert.Equal(SupportedLanguage.Java, LanguageRegistry.TryResolveByExtension("Sample.java"));
        Assert.Equal(SupportedLanguage.Ruby, LanguageRegistry.TryResolveByExtension("app.rb"));

        // C# is intentionally unsupported (R7) - routed to the Roslyn path.
        Assert.Null(LanguageRegistry.TryResolveByExtension("Program.cs"));
        Assert.Null(LanguageRegistry.TryResolveByExtension("README.md"));
        Assert.Null(LanguageRegistry.TryResolveByExtension(null));
    }

    [Fact]
    public void LanguageRegistry_TagsQuerySourceIsAvailableForSupportedLanguages()
    {
        foreach (var language in LanguageRegistry.SupportedLanguages)
        {
            var query = LanguageRegistry.TryGetTagsQuerySource(language);
            Assert.False(string.IsNullOrWhiteSpace(query), $"{language} should have a vendored tags.scm");
            // Tags queries use @definition.<kind> captures by convention.
            Assert.Contains("@definition.", query, StringComparison.Ordinal);
            Assert.Contains("@name", query, StringComparison.Ordinal);
        }
    }
}
