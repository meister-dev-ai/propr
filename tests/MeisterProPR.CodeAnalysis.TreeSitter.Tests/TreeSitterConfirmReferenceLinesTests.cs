// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.CodeAnalysis.TreeSitter.Tests;

/// <summary>
///     <c>ConfirmReferenceLinesAsync</c> across the seven Tree-sitter languages: a symbol used
///     in code is confirmed; the same name in a comment or string literal is excluded.
/// </summary>
public sealed class TreeSitterConfirmReferenceLinesTests
{
    public static IEnumerable<object[]> Cases()
    {
        // Each snippet: a real code use of the symbol, then a comment-only line, then a string-only line.
        yield return Case(
            SupportedLanguage.TypeScript,
            "sample.ts",
            "targetSym",
            "const a = targetSym();\n// targetSym\nconst s = \"targetSym\";\n",
            1, 2, 3);

        yield return Case(
            SupportedLanguage.Tsx,
            "sample.tsx",
            "targetSym",
            "const a = targetSym();\n// targetSym\nconst s = \"targetSym\";\n",
            1, 2, 3);

        yield return Case(
            SupportedLanguage.JavaScript,
            "sample.js",
            "targetSym",
            "const a = targetSym();\n// targetSym\nconst s = \"targetSym\";\n",
            1, 2, 3);

        yield return Case(
            SupportedLanguage.Python,
            "sample.py",
            "target_sym",
            "a = target_sym()\n# target_sym\ns = \"target_sym\"\n",
            1, 2, 3);

        yield return Case(
            SupportedLanguage.Go,
            "sample.go",
            "targetSym",
            "package main\nfunc use() {\n\ta := targetSym()\n\t// targetSym\n\ts := \"targetSym\"\n\t_ = a\n\t_ = s\n}\n",
            3, 4, 5);

        yield return Case(
            SupportedLanguage.Java,
            "Sample.java",
            "targetSym",
            "class C {\n\tvoid use() {\n\t\tint a = targetSym();\n\t\t// targetSym\n\t\tString s = \"targetSym\";\n\t}\n}\n",
            3, 4, 5);

        yield return Case(
            SupportedLanguage.Ruby,
            "sample.rb",
            "target_sym",
            "a = target_sym()\n# target_sym\ns = \"target_sym\"\n",
            1, 2, 3);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Confirms_code_use_and_excludes_comment_and_string(
        SupportedLanguage language,
        string path,
        string symbol,
        string source,
        int codeLine,
        int commentLine,
        int stringLine)
    {
        var analyzer = AnalyzerTestFactory.CreateAnalyzer();
        Assert.True(analyzer.IsAvailable, "Native Tree-sitter parser must be available for this test.");

        var request = new StructuralParseRequest(path, language, source, Array.Empty<ChangedLineRange>());
        var lines = await analyzer.ConfirmReferenceLinesAsync(request, symbol, CancellationToken.None);

        Assert.Contains(codeLine, lines); // real code use confirmed
        Assert.DoesNotContain(commentLine, lines); // comment excluded
        Assert.DoesNotContain(stringLine, lines); // string excluded
    }

    [Fact]
    public async Task Returns_empty_for_unknown_symbol()
    {
        var analyzer = AnalyzerTestFactory.CreateAnalyzer();
        var request = new StructuralParseRequest("sample.ts", SupportedLanguage.TypeScript, "const a = foo();\n", Array.Empty<ChangedLineRange>());

        var lines = await analyzer.ConfirmReferenceLinesAsync(request, "doesNotExist", CancellationToken.None);

        Assert.Empty(lines);
    }

    private static object[] Case(
        SupportedLanguage language,
        string path,
        string symbol,
        string source,
        int codeLine,
        int commentLine,
        int stringLine)
    {
        return [language, path, symbol, source, codeLine, commentLine, stringLine];
    }
}
