// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeisterDev.ProPR.Api.Tests.OpenApi;

/// <summary>
/// The hand-written API page and the committed API contract must not drift apart. Every route the page tells a customer to call
/// has to exist in openapi.json with the method the page uses, so a renamed or removed endpoint breaks the build instead of
/// silently leaving a customer with a 404 in the documentation.
/// </summary>
public sealed class ApiDocumentationRouteDriftTests
{
    /// <summary>
    /// Endpoints the API host serves outside its OpenAPI document: the health and liveness probes and the Prometheus scrape
    /// endpoint. They are deliberately absent from the contract, so they are listed here one by one rather than excused by a
    /// pattern that could also hide a genuinely missing route.
    /// </summary>
    private static readonly string[] EndpointsOutsideTheContract =
    [
        "/healthz",
        "/livez",
        "/metrics",
    ];

    /// <summary>
    /// The API page, newest location first. The second entry covers the interval in which the restructured documentation tree is
    /// being introduced and is dropped once the flat page is gone.
    /// </summary>
    private static readonly string[] ApiPageCandidates =
    [
        "docs/reference/api.md",
        "docs/api.md",
    ];

    private static readonly string[] HttpMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    /// <summary>Shell flags whose following argument is a value, never the request URL.</summary>
    private static readonly string[] CurlValueFlags =
    [
        "-H", "--header", "-d", "--data", "--data-raw", "--data-binary", "-u", "--user", "-o", "--output",
        "-F", "--form", "-A", "--user-agent", "-e", "--referer", "-b", "--cookie", "-c", "--cookie-jar",
        "--connect-timeout", "--max-time",
    ];

    private static readonly Regex ProseRouteExpression = new(
        "`(?<method>GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)\\s+(?<path>/[^`\\s]*)`",
        RegexOptions.Compiled);

    private static readonly Regex ShellAssignmentExpression = new(
        @"^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)=(?<value>[^(\s].*)?$",
        RegexOptions.Compiled);

    private static readonly Regex ShellVariableExpression = new(
        @"\$\{(?<braced>[A-Za-z_][A-Za-z0-9_]*)\}|\$(?<bare>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    private static readonly Regex AbsoluteUrlExpression = new("^https?://", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PlaceholderSegmentExpression = new(@"^[<{].*[>}]$", RegexOptions.Compiled);

    /// <summary>A segment that stands in for a caller-supplied value: a documentation placeholder or an unresolved shell variable.</summary>
    private const string PlaceholderSegment = "{}";

    [Fact]
    public void DocumentedRoutes_ExistInTheCommittedContract()
    {
        var page = ReadApiPage();
        var contract = ReadContractPaths();

        var failures = new List<string>();

        foreach (var route in ParseDocumentedRoutes(page.Contents).DistinctBy(route => (route.Method, route.Path)))
        {
            if (EndpointsOutsideTheContract.Contains(route.Path, StringComparer.Ordinal))
            {
                continue;
            }

            var outcome = Resolve(route, contract);

            if (outcome is ContractMatch.MethodMatched)
            {
                continue;
            }

            var reason = outcome is ContractMatch.PathMatchedButNotMethod
                ? $"the contract describes that path but not {route.Method}"
                : "the contract describes no such path";

            failures.Add($"  {route.Method} {route.Path} ({page.RelativePath} line {route.LineNumber}, written as \"{route.DocumentedAs}\") - {reason}");
        }

        Assert.True(
            failures.Count == 0,
            $"{page.RelativePath} documents routes that openapi.json does not describe. Either the documentation is stale or the "
            + $"contract was not regenerated:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    [Fact]
    public void ApiPage_StillExposesItsRoutesInAParseableForm()
    {
        var page = ReadApiPage();

        var parsed = ParseDocumentedRoutes(page.Contents);
        var routes = parsed.DistinctBy(route => (route.Method, route.Path)).ToList();

        // A tripwire: the drift check is only worth anything while it still finds the routes. A rewrite that stops spelling routes
        // out as curl commands or as a method plus path in backticks has to be noticed here, not pass by finding nothing. Both
        // parsed forms are asserted separately, so one of them going quiet cannot hide behind the other still working.
        Assert.True(
            routes.Count >= 25,
            $"Only {routes.Count} routes could be parsed out of {page.RelativePath}. The drift check reads curl invocations and "
            + "inline \"METHOD /path\" spans; if the page now expresses its routes some other way, teach the parser that form.");

        Assert.Contains(parsed, route => route.Origin == RouteOrigin.CurlExample);
        Assert.Contains(parsed, route => route.Origin == RouteOrigin.InlineSpan);
        Assert.Contains(parsed, route => route.Path.StartsWith("/clients", StringComparison.Ordinal));
    }

    [Fact]
    public void EndpointsExemptedFromTheContract_AreStillAbsentFromIt()
    {
        var contract = ReadContractPaths();

        // Keeps the exemption list honest: the moment the contract starts describing one of these, its entry has to go, because an
        // exemption that no longer has a reason is a place a real gap can hide.
        foreach (var exemption in EndpointsOutsideTheContract)
        {
            Assert.False(
                contract.ContainsKey(exemption),
                $"openapi.json now describes {exemption}, so it no longer needs an exemption in the documentation drift check.");
        }
    }

    private static ContractMatch Resolve(DocumentedRoute route, IReadOnlyDictionary<string, HashSet<string>> contract)
    {
        var documentedSegments = SplitSegments(route.Path);
        var pathMatched = false;

        foreach (var (contractPath, methods) in contract)
        {
            if (!SegmentsMatch(documentedSegments, SplitSegments(contractPath)))
            {
                continue;
            }

            pathMatched = true;

            if (methods.Contains(route.Method))
            {
                return ContractMatch.MethodMatched;
            }
        }

        return pathMatched ? ContractMatch.PathMatchedButNotMethod : ContractMatch.NoMatch;
    }

    /// <summary>
    /// Compares a documented path against a contract route template. A template segment such as <c>{clientId}</c> stands for any
    /// single value, so a documented id, placeholder or literal all satisfy it; every other segment has to match exactly.
    /// </summary>
    private static bool SegmentsMatch(IReadOnlyList<string> documented, IReadOnlyList<string> contract)
    {
        if (documented.Count != contract.Count)
        {
            return false;
        }

        for (var index = 0; index < documented.Count; index++)
        {
            if (IsPlaceholder(contract[index]))
            {
                continue;
            }

            if (!string.Equals(documented[index], contract[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPlaceholder(string segment) => PlaceholderSegmentExpression.IsMatch(segment);

    private static string[] SplitSegments(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Reduces either side to a comparable path: drops scheme and host, drops the reverse-proxy <c>/api</c> prefix the curl
    /// examples carry and the contract does not, and drops the query string and any trailing slash.
    /// </summary>
    private static string NormalisePath(string raw)
    {
        var path = raw;
        var cut = path.AsSpan().IndexOfAny('?', '#');

        if (cut >= 0)
        {
            path = path[..cut];
        }

        if (AbsoluteUrlExpression.IsMatch(path))
        {
            path = "/" + string.Join('/', SplitSegments(path[(path.IndexOf("://", StringComparison.Ordinal) + 3)..]).Skip(1));
        }

        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        if (path.Equals("/api", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            path = path[4..];
        }

        path = path.TrimEnd('/');

        return path.Length == 0 ? "/" : path;
    }

    private static IReadOnlyList<DocumentedRoute> ParseDocumentedRoutes(string contents)
    {
        var lines = contents.Split('\n');
        var routes = new List<DocumentedRoute>();
        var shellVariables = new Dictionary<string, string>(StringComparer.Ordinal);
        var insideFence = false;
        var fenceIsShell = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                insideFence = !insideFence;
                fenceIsShell = insideFence && IsShellFence(line);
                shellVariables.Clear();
                continue;
            }

            if (!insideFence)
            {
                routes.AddRange(ParseProseRoutes(line, index + 1));
                continue;
            }

            if (!fenceIsShell)
            {
                continue;
            }

            var lineNumber = index + 1;
            var command = JoinContinuedLines(lines, ref index);
            routes.AddRange(ParseShellRoutes(command, lineNumber, shellVariables));
        }

        return routes;
    }

    private static bool IsShellFence(string fenceLine)
    {
        var info = fenceLine.TrimStart()[3..].Trim();

        return info.Length == 0
               || info.Equals("bash", StringComparison.OrdinalIgnoreCase)
               || info.Equals("sh", StringComparison.OrdinalIgnoreCase)
               || info.Equals("shell", StringComparison.OrdinalIgnoreCase)
               || info.Equals("console", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<DocumentedRoute> ParseProseRoutes(string line, int lineNumber)
    {
        foreach (Match match in ProseRouteExpression.Matches(line))
        {
            var documented = match.Groups["path"].Value;

            yield return new DocumentedRoute(
                match.Groups["method"].Value.ToUpperInvariant(),
                NormalisePath(documented),
                documented,
                lineNumber,
                RouteOrigin.InlineSpan);
        }
    }

    /// <summary>
    /// Joins the backslash continuations of a shell line into one command. A body opened with <c>-d '{</c> is left behind, which is
    /// what we want: the URLs inside a JSON payload are not routes of this API.
    /// </summary>
    private static string JoinContinuedLines(string[] lines, ref int index)
    {
        var command = lines[index].TrimEnd('\r');

        while (command.TrimEnd().EndsWith('\\')
               && index + 1 < lines.Length
               && !lines[index + 1].TrimStart().StartsWith("```", StringComparison.Ordinal))
        {
            command = string.Concat(command.TrimEnd().AsSpan()[..^1], " ", lines[index + 1].TrimEnd('\r'));
            index++;
        }

        return command;
    }

    private static IEnumerable<DocumentedRoute> ParseShellRoutes(string command, int lineNumber, Dictionary<string, string> shellVariables)
    {
        RecordAssignment(command, shellVariables);

        if (!command.Contains("curl", StringComparison.Ordinal))
        {
            return [];
        }

        var routes = new List<DocumentedRoute>();
        var tokens = Tokenize(command);

        for (var index = 0; index < tokens.Count; index++)
        {
            if (!StartsCurlInvocation(tokens[index]))
            {
                continue;
            }

            var invocation = ReadInvocation(tokens, index, shellVariables);

            if (invocation is not null)
            {
                routes.Add(invocation with { LineNumber = lineNumber });
            }
        }

        return routes;
    }

    private static bool StartsCurlInvocation(string token) =>
        token.Equals("curl", StringComparison.Ordinal) || token.EndsWith("(curl", StringComparison.Ordinal);

    private static DocumentedRoute? ReadInvocation(IReadOnlyList<string> tokens, int start, IReadOnlyDictionary<string, string> shellVariables)
    {
        var method = "GET";
        string? url = null;

        for (var index = start + 1; index < tokens.Count && !StartsCurlInvocation(tokens[index]); index++)
        {
            var token = tokens[index];

            if (token is "-X" or "--request" && index + 1 < tokens.Count)
            {
                method = tokens[++index].ToUpperInvariant();
                continue;
            }

            if (CurlValueFlags.Contains(token, StringComparer.Ordinal))
            {
                index++;
                continue;
            }

            var expanded = ExpandVariables(token, shellVariables);

            if (url is null && AbsoluteUrlExpression.IsMatch(expanded))
            {
                url = expanded;
            }
        }

        return url is null ? null : new DocumentedRoute(method, NormalisePath(url), url, 0, RouteOrigin.CurlExample);
    }

    /// <summary>
    /// Remembers a plain <c>NAME=value</c> assignment so a later <c>"$NAME/route"</c> resolves. A value produced by a command
    /// substitution is remembered as a placeholder, because its runtime value is an id, not a route segment.
    /// </summary>
    private static void RecordAssignment(string command, Dictionary<string, string> shellVariables)
    {
        if (command.Contains("curl", StringComparison.Ordinal))
        {
            return;
        }

        var assignment = ShellAssignmentExpression.Match(command);

        if (!assignment.Success)
        {
            return;
        }

        var value = assignment.Groups["value"].Value.Trim().Trim('\'', '"');

        shellVariables[assignment.Groups["name"].Value] = value.Contains("$(", StringComparison.Ordinal) || value.Contains('`')
            ? PlaceholderSegment
            : ExpandVariables(value, shellVariables);
    }

    private static string ExpandVariables(string text, IReadOnlyDictionary<string, string> shellVariables) =>
        ShellVariableExpression.Replace(
            text,
            match =>
            {
                var name = match.Groups["braced"].Success ? match.Groups["braced"].Value : match.Groups["bare"].Value;

                return shellVariables.TryGetValue(name, out var value) ? value : PlaceholderSegment;
            });

    /// <summary>Splits a shell line into arguments, honouring single and double quotes and tolerating a quote left open by a payload.</summary>
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var quote = '\0';
        var started = false;

        foreach (var character in line)
        {
            if (quoted)
            {
                if (character == quote)
                {
                    quoted = false;
                }
                else
                {
                    current.Append(character);
                }
            }
            else if (character is '\'' or '"')
            {
                quoted = true;
                quote = character;
                started = true;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (started || current.Length > 0)
                {
                    tokens.Add(current.ToString());
                }

                current.Clear();
                started = false;
            }
            else
            {
                current.Append(character);
            }
        }

        if (started || current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static ApiPage ReadApiPage()
    {
        var repositoryRoot = ResolveRepositoryRoot();

        foreach (var candidate in ApiPageCandidates)
        {
            var absolute = Path.Combine(repositoryRoot, candidate.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(absolute))
            {
                return new ApiPage(candidate, File.ReadAllText(absolute));
            }
        }

        throw new InvalidOperationException($"Unable to locate the API page. Looked for: {string.Join(", ", ApiPageCandidates)}.");
    }

    private static Dictionary<string, HashSet<string>> ReadContractPaths()
    {
        var contractPath = Path.Combine(ResolveRepositoryRoot(), "openapi.json");
        using var contract = JsonDocument.Parse(File.ReadAllText(contractPath));

        var paths = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var path in contract.RootElement.GetProperty("paths").EnumerateObject())
        {
            var methods = path.Value.EnumerateObject()
                .Select(operation => operation.Name.ToUpperInvariant())
                .Where(name => HttpMethods.Contains(name, StringComparer.Ordinal))
                .ToHashSet(StringComparer.Ordinal);

            if (methods.Count > 0)
            {
                paths[NormalisePath(path.Name)] = methods;
            }
        }

        Assert.NotEmpty(paths);

        return paths;
    }

    private static string ResolveRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MeisterDev.ProPR.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate the repository root.");
    }

    private enum ContractMatch
    {
        NoMatch,
        PathMatchedButNotMethod,
        MethodMatched,
    }

    /// <summary>The form the page wrote a route in: a runnable curl example, or a method and path inside a backtick span.</summary>
    private enum RouteOrigin
    {
        CurlExample,
        InlineSpan,
    }

    private sealed record ApiPage(string RelativePath, string Contents);

    private sealed record DocumentedRoute(string Method, string Path, string DocumentedAs, int LineNumber, RouteOrigin Origin);
}
