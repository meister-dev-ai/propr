// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using System.Text;
using MeisterProPR.Api.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace MeisterProPR.Api.Tests.Startup;

/// <summary>
///     Guardrail: authorization is deny-by-default, but a controller action that carries no
///     <see cref="AuthorizeAttribute" />/<see cref="AllowAnonymousAttribute" /> and consults no in-code auth gate
///     ships an endpoint whose only protection is the global fallback (authenticated caller, no per-resource scoping).
///     This test enumerates every controller action and fails when one has neither an authorization attribute nor a
///     recognized in-code gate, so a forgotten check cannot pass CI unnoticed.
/// </summary>
public sealed class ControllerAuthorizationCoverageTests
{
    // Substrings that mark a method body as consulting the shared auth context. Any AuthHelpers member
    // (RequireAdmin/RequireClientRole/RequireAuthenticated/IsAdmin/GetUserId/...) counts as a gate.
    private static readonly string[] GateTokens = ["AuthHelpers."];

    [Fact]
    public void EveryControllerAction_HasAuthorizationAttributeOrInCodeGate()
    {
        var apiAssembly = typeof(AuthHelpers).Assembly;
        var apiSourceRoot = LocateApiSourceRoot();

        var controllerTypes = apiAssembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && type is { IsAbstract: false, IsClass: true })
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(controllerTypes);

        var uncovered = new List<string>();

        foreach (var controllerType in controllerTypes)
        {
            var classCovered = HasAuthorizationAttribute(controllerType.GetCustomAttributes(inherit: true));

            var actions = controllerType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(IsAction)
                .ToArray();

            if (actions.Length == 0)
            {
                continue;
            }

            // Only pay the source-parsing cost for controllers that might rely on an in-code gate.
            var gatedMethodNames = classCovered
                ? new HashSet<string>(StringComparer.Ordinal)
                : ResolveGatedMethodNames(controllerType, apiSourceRoot);

            foreach (var action in actions)
            {
                if (classCovered || HasAuthorizationAttribute(action.GetCustomAttributes(inherit: true)))
                {
                    continue;
                }

                if (!gatedMethodNames.Contains(action.Name))
                {
                    uncovered.Add($"{controllerType.FullName}.{action.Name}");
                }
            }
        }

        Assert.True(
            uncovered.Count == 0,
            "These controller actions have no [Authorize]/[AllowAnonymous] attribute and no recognized in-code auth "
            + "gate; add one (or [AllowAnonymous] if intentionally public):\n  "
            + string.Join("\n  ", uncovered));
    }

    private static bool HasAuthorizationAttribute(IEnumerable<object> attributes)
    {
        var materialized = attributes as object[] ?? attributes.ToArray();
        return materialized.OfType<IAuthorizeData>().Any() || materialized.OfType<IAllowAnonymous>().Any();
    }

    private static bool IsAction(MethodInfo method)
    {
        if (method.IsSpecialName || method.GetBaseDefinition().DeclaringType != method.DeclaringType)
        {
            return false;
        }

        if (method.GetCustomAttribute<NonActionAttribute>() is not null)
        {
            return false;
        }

        // Treat any method that declares an HTTP verb (directly or via a route attribute) as an action.
        return method.GetCustomAttributes(inherit: true).OfType<IActionHttpMethodProvider>().Any()
               || method.GetCustomAttributes(inherit: true).OfType<IRouteTemplateProvider>().Any();
    }

    // A controller method "gates" when its body reaches AuthHelpers directly or by calling another method on the
    // same controller that does. Computed as a fixed point over the controller's own method bodies.
    private static HashSet<string> ResolveGatedMethodNames(Type controllerType, string apiSourceRoot)
    {
        var source = ReadControllerSource(controllerType, apiSourceRoot);
        if (source is null)
        {
            // No source located: return empty so the action is flagged rather than silently passed.
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var cleaned = BlankStringsAndComments(source);
        var methodNames = controllerType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var bodies = methodNames.ToDictionary(
            name => name,
            name => ExtractMethodBodies(source, cleaned, name),
            StringComparer.Ordinal);

        var gated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, body) in bodies)
        {
            if (GateTokens.Any(token => body.Contains(token, StringComparison.Ordinal)))
            {
                gated.Add(name);
            }
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var (name, body) in bodies)
            {
                if (gated.Contains(name))
                {
                    continue;
                }

                if (gated.Any(gate => ReferencesInvocation(body, gate)))
                {
                    gated.Add(name);
                    changed = true;
                }
            }
        } while (changed);

        return gated;
    }

    private static bool ReferencesInvocation(string body, string methodName)
    {
        var searchStart = 0;
        while (true)
        {
            var index = body.IndexOf(methodName, searchStart, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            var end = index + methodName.Length;
            var beforeOk = index == 0 || !IsIdentifierChar(body[index - 1]);
            var afterChar = SkipSpaces(body, end);
            if (beforeOk && afterChar < body.Length && body[afterChar] == '(')
            {
                return true;
            }

            searchStart = end;
        }
    }

    private static string ExtractMethodBodies(string source, string cleaned, string methodName)
    {
        var combined = new StringBuilder();
        var searchStart = 0;

        while (true)
        {
            var index = cleaned.IndexOf(methodName, searchStart, StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }

            searchStart = index + methodName.Length;

            var beforeOk = index == 0 || !IsIdentifierChar(cleaned[index - 1]);
            var afterName = SkipSpaces(cleaned, index + methodName.Length);
            if (!beforeOk || afterName >= cleaned.Length || cleaned[afterName] != '(')
            {
                continue;
            }

            var closeParen = MatchDelimiter(cleaned, afterName, '(', ')');
            if (closeParen < 0)
            {
                continue;
            }

            var afterParams = SkipSpaces(cleaned, closeParen + 1);
            if (afterParams >= cleaned.Length)
            {
                continue;
            }

            if (cleaned[afterParams] == '{')
            {
                var closeBrace = MatchDelimiter(cleaned, afterParams, '{', '}');
                if (closeBrace > afterParams)
                {
                    combined.Append(source, afterParams, closeBrace - afterParams + 1).Append('\n');
                }
            }
            else if (afterParams + 1 < cleaned.Length && cleaned[afterParams] == '=' && cleaned[afterParams + 1] == '>')
            {
                var semicolon = cleaned.IndexOf(';', afterParams);
                if (semicolon > afterParams)
                {
                    combined.Append(source, afterParams, semicolon - afterParams + 1).Append('\n');
                }
            }
        }

        return combined.ToString();
    }

    private static int MatchDelimiter(string text, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == open)
            {
                depth++;
            }
            else if (text[i] == close)
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static int SkipSpaces(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static bool IsIdentifierChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_';
    }

    // Replaces the contents of string/char literals and comments with spaces so brace/paren matching on the
    // returned text never trips over a '{', '}', '(' or ')' that lives inside a literal or comment.
    private static string BlankStringsAndComments(string source)
    {
        var result = source.ToCharArray();
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    result[i] = ' ';
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                while (i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                {
                    if (source[i] != '\n')
                    {
                        result[i] = ' ';
                    }

                    i++;
                }

                if (i + 1 < source.Length)
                {
                    result[i] = ' ';
                    result[i + 1] = ' ';
                    i += 2;
                }

                continue;
            }

            if (c == '@' && i + 1 < source.Length && source[i + 1] == '"')
            {
                result[i] = ' ';
                i++;
                result[i] = ' ';
                i++;
                while (i < source.Length)
                {
                    if (source[i] == '"' && i + 1 < source.Length && source[i + 1] == '"')
                    {
                        result[i] = ' ';
                        result[i + 1] = ' ';
                        i += 2;
                        continue;
                    }

                    if (source[i] == '"')
                    {
                        result[i] = ' ';
                        i++;
                        break;
                    }

                    if (source[i] != '\n')
                    {
                        result[i] = ' ';
                    }

                    i++;
                }

                continue;
            }

            if (c == '"')
            {
                result[i] = ' ';
                i++;
                while (i < source.Length && source[i] != '"' && source[i] != '\n')
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        result[i] = ' ';
                        result[i + 1] = ' ';
                        i += 2;
                        continue;
                    }

                    result[i] = ' ';
                    i++;
                }

                if (i < source.Length && source[i] == '"')
                {
                    result[i] = ' ';
                    i++;
                }

                continue;
            }

            if (c == '\'')
            {
                result[i] = ' ';
                i++;
                while (i < source.Length && source[i] != '\'' && source[i] != '\n')
                {
                    if (source[i] == '\\' && i + 1 < source.Length)
                    {
                        result[i] = ' ';
                        result[i + 1] = ' ';
                        i += 2;
                        continue;
                    }

                    result[i] = ' ';
                    i++;
                }

                if (i < source.Length && source[i] == '\'')
                {
                    result[i] = ' ';
                    i++;
                }

                continue;
            }

            i++;
        }

        return new string(result);
    }

    // Concatenates every hand-written source file that declares this controller. Controllers can be `partial`
    // (e.g. for [LoggerMessage]); the compiler generates additional partials under obj/ that hold none of the
    // real action logic, so build-output directories are excluded to avoid matching those generated copies.
    private static string? ReadControllerSource(Type controllerType, string apiSourceRoot)
    {
        var simpleName = controllerType.Name;
        var combined = new StringBuilder();

        foreach (var file in Directory.EnumerateFiles(apiSourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutputPath(file))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (DeclaresType(text, simpleName))
            {
                combined.Append(text).Append('\n');
            }
        }

        return combined.Length == 0 ? null : combined.ToString();
    }

    private static bool DeclaresType(string source, string simpleName)
    {
        var needle = "class " + simpleName;
        var searchStart = 0;
        while (true)
        {
            var index = source.IndexOf(needle, searchStart, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            var afterName = index + needle.Length;
            if (afterName >= source.Length || !IsIdentifierChar(source[afterName]))
            {
                return true;
            }

            searchStart = index + needle.Length;
        }
    }

    private static bool IsBuildOutputPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/obj/", StringComparison.Ordinal)
               || normalized.Contains("/bin/", StringComparison.Ordinal);
    }

    private static string LocateApiSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "MeisterProPR.Api");
            if (File.Exists(Path.Combine(candidate, "MeisterProPR.Api.csproj")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the MeisterProPR.Api source directory from " + AppContext.BaseDirectory);
    }
}
