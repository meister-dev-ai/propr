// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;

namespace MeisterDev.ProPR.Runner.Tests;

/// <summary>
///     The boundary the separate host exists to create, enforced rather than documented.
///     <para>
///         A runner holds a customer's source code on a box the customer may not control as tightly as the
///         control plane. What makes that acceptable is not that it holds no code, but that it holds no
///         keys: no database, no source-control credential, no AI provider credential, no data-protection
///         key ring. Each is something a reviewer would reasonably reach for while adding a feature, and
///         each would be a quiet, permanent widening of what a compromised runner gets.
///     </para>
///     <para>
///         The line is drawn at the assemblies that hold or read secrets, not at every assembly the control
///         plane also uses. The runner runs the same review pipeline, so it necessarily references that
///         pipeline and the ports it is written against; both are free of persistence by their own guard.
///         Referencing a provider client library it never constructs is not the same as holding a provider
///         key, and these checks say exactly that and nothing stronger.
///     </para>
/// </summary>
public sealed class CredentialFreeBoundaryTests
{
    private static readonly Assembly RunnerHost = typeof(RunnerWorkLoop).Assembly;

    /// <summary>Solution assemblies the host may reference, each for a stated reason.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["MeisterDev.ProPR.Runner.Contracts"] = "the wire format both sides share",
        ["MeisterDev.ProPR.Reviewing"] = "the review pipeline, which has its own no-persistence guard",
        ["MeisterDev.ProPR.Application"] = "the ports the pipeline is written against; holds no persistence",
        ["MeisterDev.ProPR.Domain"] = "the review model the pipeline operates on",
        ["MeisterDev.ProPR.Observability"] = "traces, without which the host is not operable",
        ["MeisterDev.Ai.Providers"] = "reached through Application; the runner constructs no provider client",
        ["MeisterDev.ProPR.CodeAnalysis"] = "structural analysis of the working copy",
        ["MeisterDev.ProPR.CodeAnalysis.Roslyn"] = "structural analysis of the working copy",
        ["MeisterDev.ProPR.CodeAnalysis.TreeSitter"] = "structural analysis of the working copy",
        ["MeisterDev.ProPR.CodeInsights.Contracts"] = "insight shapes the pipeline emits",
        ["MeisterDev.ProPR.ProCursor.Contracts"] = "the code-knowledge contract, reached through the proxy",
        ["MeisterDev.ProPR.ProRV"] = "the review-knowledge lens the pipeline applies",
    };

    // The assemblies that hold or read a secret. Checked as direct references because a direct reference
    // is what grants compile-time access, and therefore what makes the mistake possible at all.
    [Theory]
    [InlineData("MeisterDev.ProPR.Infrastructure")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.EntityFrameworkCore.Relational")]
    [InlineData("Npgsql")]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL")]
    [InlineData("Microsoft.AspNetCore.DataProtection")]
    public void TheRunnerHost_DoesNotReference(string forbiddenAssembly)
    {
        var referenced = RunnerHost.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(name => name is not null)
            .ToArray();

        Assert.DoesNotContain(forbiddenAssembly, referenced);
    }

    // Every one of our own assemblies the host reaches has to be a deliberate entry above. Framework and
    // package references are not listed: a list over all of those would be edited on every addition and
    // stop meaning anything, while the set of our own projects is small and each one is a decision.
    [Fact]
    public void TheRunnerHost_ReferencesNoSolutionProjectBeyondItsAllowedSet()
    {
        var solutionReferences = RunnerHost.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(name => name is not null && name.StartsWith("MeisterDev.", StringComparison.Ordinal))
            .ToArray();

        Assert.All(
            solutionReferences,
            name => Assert.True(
                Allowed.ContainsKey(name!),
                $"{name} is not in the runner's allowed set. Add it with a reason, or find another way."));
    }

    // The reference list is a proxy for the thing that actually matters, so this asserts the thing itself:
    // nothing the host defines takes a database context.
    [Fact]
    public void TheRunnerHost_DefinesNoTypeThatTouchesADbContext()
    {
        var offenders = RunnerHost.GetTypes()
            .Where(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(c => c.GetParameters())
                .Any(p => p.ParameterType.Name.Contains("DbContext", StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .ToArray();

        Assert.Empty(offenders);
    }

    // Nothing in the host's own configuration surface should name a secret it has no business holding.
    // This catches the version of the mistake that arrives as "just one connection string for diagnostics".
    [Theory]
    [InlineData("ConnectionString")]
    [InlineData("ApiKey")]
    [InlineData("ClientSecret")]
    [InlineData("PersonalAccessToken")]
    [InlineData("DataProtection")]
    public void TheRunnerHostOptions_HoldNoCredentialBeyondItsOwn(string forbiddenFragment)
    {
        var propertyNames = typeof(RunnerHostOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, name => name.Contains(forbiddenFragment, StringComparison.OrdinalIgnoreCase));
    }
}
