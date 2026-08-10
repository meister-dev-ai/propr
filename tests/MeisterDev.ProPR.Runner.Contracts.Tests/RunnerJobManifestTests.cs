// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using System.Text.Json;
using MeisterDev.ProPR.Runner.Contracts;

namespace MeisterDev.ProPR.Runner.Contracts.Tests;

public sealed class RunnerJobManifestTests
{
    private static RunnerJobManifest SampleManifest()
    {
        return new RunnerJobManifest(
            RunnerContractVersion.Current,
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            7,
            new RunnerReviewTarget(
                "AzureDevOps",
                "https://dev.azure.com/contoso",
                "project",
                "repo-id",
                "repo",
                "42",
                42,
                3,
                "Add the widget",
                "It adds the widget.",
                "feature/widget",
                "main",
                "head-sha",
                "base-sha",
                ["src/a.cs", "src/b.cs"],
                [new RunnerReviewThread("src/a.cs", 12, "active", [new RunnerReviewThreadComment("Reviewer", "Is this bounded?")])]),
            new RunnerWorkspaceReference("/runner/workspace/33333333", "head-sha", "base-sha", 1_073_741_824),
            SampleModel("reviewer-default"),
            [new RunnerReviewPass(1, SampleModel("reviewer-medium"), null, null, false)],
            new RunnerPromptConfiguration("en", "balanced", new Dictionary<string, string> { ["review"] = "text" }),
            ["**/*.min.js"],
            [new RunnerRepositoryInstruction("guide.md", "House style", "Always", "Prefer records.")],
            12.5m,
            new RunnerTraceContext("00-trace-span-01", null),
            new RunnerReviewBehaviour(true, false, true, true, 0.2f, "file_by_file_balanced"),
            [
                new RunnerLinkedItem(
                    "AB#7", "User Story", "Add the widget", "So the widget exists.", "https://items.invalid/7",
                    [new RunnerLinkedItemRef("Parent", "AB#1", null, "The epic")]),
            ],
            "https://replica-2.internal:8443",
            ParallelReviewExecutionLicensed: false);
    }

    private static RunnerModelBinding SampleModel(string name)
    {
        return new RunnerModelBinding(name, "gpt-5", "OpenAi", "Medium", "o200k_base", 200_000, 400_000, true, true, true);
    }

    [Fact]
    public void Manifest_RoundTripsWithoutLoss()
    {
        var manifest = SampleManifest();

        var json = JsonSerializer.Serialize(manifest, RunnerContractJson.Options);
        var restored = JsonSerializer.Deserialize<RunnerJobManifest>(json, RunnerContractJson.Options);

        // Compared as the wire form rather than by record equality: these records hold collections, and
        // record equality compares those by reference, so it would report a difference between two
        // identical manifests and say nothing about whether anything was actually lost.
        Assert.NotNull(restored);
        Assert.Equal(json, JsonSerializer.Serialize(restored, RunnerContractJson.Options));
    }

    // The tolerant-reader half of the versioning contract: an additive field from a newer control plane
    // must not refuse the whole manifest on an older runner — that is the deploy the compatibility window
    // exists to survive. Shape changes are handled by the version gate's manifest floor, not the parser.
    [Fact]
    public void AnUnknownField_IsIgnoredByAProductionReader()
    {
        var json = JsonSerializer.Serialize(SampleManifest(), RunnerContractJson.Options);
        var withExtra = json.Insert(1, "\"somethingNewer\":true,");

        var restored = JsonSerializer.Deserialize<RunnerJobManifest>(withExtra, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(restored);
        Assert.Equal(SampleManifest().JobId, restored!.JobId);
    }

    // The strict options exist for the round-trip test above: refusing unknown members is what makes a
    // lost field loud there. This pins that they stay strict, so the loss detection keeps working.
    [Fact]
    public void TheTestSerializer_StaysStrictSoRoundTripsDetectLoss()
    {
        var json = JsonSerializer.Serialize(SampleManifest(), RunnerContractJson.Options);
        var withExtra = json.Insert(1, "\"somethingNewer\":true,");

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RunnerJobManifest>(withExtra, RunnerContractJson.Options));
    }

    // Structural, not procedural. "We remember not to populate it" is not a boundary: the schema must have
    // nowhere to put a credential in the first place, so this walks the whole graph rather than the top level.
    [Fact]
    public void TheManifestSchema_HasNoFieldThatCouldCarryASecret()
    {
        // Whole words a secret-bearing field would be named with. "pat" is deliberately spelled out:
        // as three letters it also matches "path", and a check that fires on every path is a check
        // somebody deletes.
        string[] forbidden =
        [
            "secret", "credential", "password", "passphrase", "token", "apikey", "accesskey", "privatekey",
            "connectionstring", "clientsecret", "personalaccess", "bearer", "authorization",
        ];

        var offenders = new List<string>();
        WalkProperties(typeof(RunnerJobManifest), [], offenders, forbidden, new HashSet<Type>());

        // What a model binding says about counting tokens: which tokenizer, and how many fit. Named
        // individually rather than by loosening "token" to something narrower, so a field actually called
        // AccessToken still fails this.
        Assert.Equal(
            ["DefaultModel.TokenizerName", "DefaultModel.MaxInputTokens", "DefaultModel.MaxContextTokens"],
            offenders.Select(offender => offender.Replace("Passes.Model.", "DefaultModel.", StringComparison.Ordinal)).Distinct());
    }

    // The trace-context property is named TraceParent, which contains "paren" but not a secret word; this
    // guards the guard, so a future rename that neuters the check above is noticed.
    [Fact]
    public void TheSecretCheck_ActuallyCatchesASecretBearingField()
    {
        var offenders = new List<string>();
        WalkProperties(typeof(LeakyShape), [], offenders, ["token"], new HashSet<Type>());

        Assert.Single(offenders);
    }

    private static void WalkProperties(
        Type type,
        string[] path,
        List<string> offenders,
        string[] forbidden,
        HashSet<Type> visited)
    {
        if (!visited.Add(type))
        {
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = property.Name.ToLowerInvariant();
            if (Array.Exists(forbidden, word => name.Contains(word, StringComparison.Ordinal)))
            {
                offenders.Add(string.Join('.', [.. path, property.Name]));
            }

            foreach (var nested in ContractTypesIn(property.PropertyType))
            {
                WalkProperties(nested, [.. path, property.Name], offenders, forbidden, visited);
            }
        }
    }

    private static IEnumerable<Type> ContractTypesIn(Type type)
    {
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var nested in ContractTypesIn(argument))
                {
                    yield return nested;
                }
            }

            yield break;
        }

        if (type.Namespace?.StartsWith("MeisterDev.ProPR.Runner.Contracts", StringComparison.Ordinal) == true)
        {
            yield return type;
        }
    }

    private sealed record LeakyShape(string AccessToken);
}
