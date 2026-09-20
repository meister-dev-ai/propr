// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.GoogleVertexAddIn.Tests;

/// <summary>
///     Pins the function declaration the Google driver puts on the wire for a tool.
/// </summary>
/// <remarks>
///     <para>
///         Google reads a declaration's parameters as a protobuf message, not as a JSON Schema document: its
///         type holds one enum value and its field set is closed. Each test sends a tool through the client and
///         reads the request body, so the measurement is the payload, not the translation in isolation.
///     </para>
///     <para>
///         The schemas come from <see cref="AIFunctionFactory" /> wherever the shape under test is one the SDK
///         produces, so the suite tracks what the installed version emits. A hand-written schema is used only
///         for the shapes it does not produce, which an add-in author's tool still can.
///     </para>
/// </remarks>
public sealed class GoogleFunctionSchemaTests
{
    private static readonly ProviderModelDescriptor Model =
        new(Guid.NewGuid(), "gemini-3-pro", [ProviderDeclaredProtocolModes.Auto, GoogleVertexProviderDriver.GenerateContentProtocol]);

    /// <summary>
    ///     The live failure: a nullable parameter is a two-member type array, and the proto parser cannot read
    ///     a list into the single enum its type field is.
    /// </summary>
    [Fact]
    public async Task ANullableParameterIsSentAsOneTypeAndMarkedNullable()
    {
        var declaration = await DeclareAsync(
            AIFunctionFactory.Create(
                (string path, string? fileMask, int maxResults) => path,
                "search_source_changed_files",
                "Searches the files an increment changed."));

        var properties = declaration.GetProperty("parameters").GetProperty("properties");
        Assert.Equal("STRING", properties.GetProperty("fileMask").GetProperty("type").GetString());
        Assert.True(properties.GetProperty("fileMask").GetProperty("nullable").GetBoolean());

        // A parameter that was never nullable is not marked.
        Assert.Equal("STRING", properties.GetProperty("path").GetProperty("type").GetString());
        Assert.False(properties.GetProperty("path").TryGetProperty("nullable", out _));
        Assert.Equal("INTEGER", properties.GetProperty("maxResults").GetProperty("type").GetString());

        AssertEveryTypeIsOneName(declaration);
    }

    [Fact]
    public async Task ANestedObjectAndAnArrayOfObjectsAreTranslatedAtEveryLevel()
    {
        var declaration = await DeclareAsync(AIFunctionFactory.Create((Outer outer, Inner[] items) => outer.Tags, "inspect", "Inspects a shape."));

        var properties = declaration.GetProperty("parameters").GetProperty("properties");

        var child = properties.GetProperty("outer").GetProperty("properties").GetProperty("child");
        Assert.Equal("OBJECT", child.GetProperty("type").GetString());
        Assert.True(child.GetProperty("nullable").GetBoolean());
        Assert.Equal("INTEGER", child.GetProperty("properties").GetProperty("count").GetProperty("type").GetString());
        Assert.True(child.GetProperty("properties").GetProperty("count").GetProperty("nullable").GetBoolean());

        var items = properties.GetProperty("items");
        Assert.Equal("ARRAY", items.GetProperty("type").GetString());
        Assert.Equal("OBJECT", items.GetProperty("items").GetProperty("type").GetString());
        Assert.Equal("STRING", items.GetProperty("items").GetProperty("properties").GetProperty("name").GetProperty("type").GetString());

        AssertEveryTypeIsOneName(declaration);
    }

    /// <summary>
    ///     A recursive type points at a node that contains itself. Expanding it without a guard does not
    ///     terminate, so the payload has to be finite and the node that closes the loop has to be replaced.
    /// </summary>
    [Fact]
    public async Task ARecursiveSchemaProducesAFinitePayloadWithTheLoopReplaced()
    {
        var declaration = await DeclareAsync(AIFunctionFactory.Create((Node root) => root.Label, "walk", "Walks a tree."));

        var child = declaration.GetProperty("parameters").GetProperty("properties")
            .GetProperty("root").GetProperty("properties").GetProperty("child");

        // One level is expanded, because the node it refers to was not yet being expanded at that point.
        Assert.Equal("OBJECT", child.GetProperty("type").GetString());
        Assert.Equal("STRING", child.GetProperty("properties").GetProperty("label").GetProperty("type").GetString());

        // The level below closes the loop and is replaced with a string that says so.
        var repeated = child.GetProperty("properties").GetProperty("child");
        Assert.Equal("STRING", repeated.GetProperty("type").GetString());
        Assert.Contains("not expanded", repeated.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.False(repeated.TryGetProperty("properties", out _));

        // A list of the same type closes the same loop.
        var children = child.GetProperty("properties").GetProperty("children");
        Assert.Equal("ARRAY", children.GetProperty("type").GetString());
        Assert.Equal("STRING", children.GetProperty("items").GetProperty("type").GetString());

        AssertEveryTypeIsOneName(declaration);
        AssertNoFieldNamed(declaration, "$ref");
        Assert.True(Depth(declaration) < 20, "The recursive schema was expanded further than a bounded payload allows.");
    }

    /// <summary>
    ///     Google's message has no reference field on the Gemini API surface, so a reference has to be expanded
    ///     in place and the definitions it came from left off.
    /// </summary>
    [Fact]
    public async Task ReferencesAreInlinedAndTheDefinitionsAreNotSent()
    {
        var declaration = await DeclareAsync(
            new SchemaTool(
                "resolve",
                """
                {
                  "type": "object",
                  "properties": {
                    "pet": { "type": "object", "properties": { "name": { "type": "string" } } },
                    "other": { "$ref": "#/properties/pet" },
                    "tag": { "$ref": "#/$defs/Tag" }
                  },
                  "required": ["pet"],
                  "$defs": { "Tag": { "type": "string", "enum": ["red", "blue"] } }
                }
                """));

        var properties = declaration.GetProperty("parameters").GetProperty("properties");

        // A pointer into the schema itself, which is the form Microsoft.Extensions.AI emits.
        Assert.Equal("OBJECT", properties.GetProperty("other").GetProperty("type").GetString());
        Assert.Equal("STRING", properties.GetProperty("other").GetProperty("properties").GetProperty("name").GetProperty("type").GetString());

        // A named definition, which a hand-written schema uses.
        Assert.Equal("STRING", properties.GetProperty("tag").GetProperty("type").GetString());
        Assert.Equal(["red", "blue"], properties.GetProperty("tag").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));

        AssertNoFieldNamed(declaration, "$ref");
        AssertNoFieldNamed(declaration, "$defs");
    }

    [Fact]
    public async Task AnEnumIsSentAsStringsWithoutItsNullMember()
    {
        var declaration = await DeclareAsync(AIFunctionFactory.Create((Direction heading, Direction? fallback) => heading, "steer", "Steers."));

        var properties = declaration.GetProperty("parameters").GetProperty("properties");

        var heading = properties.GetProperty("heading");
        Assert.Equal("STRING", heading.GetProperty("type").GetString());
        Assert.Equal(["East", "North"], heading.GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("enum", heading.GetProperty("format").GetString());

        // Google's enum field holds strings, so the null member the SDK adds for a nullable enum is dropped and
        // the nullability is stated by the field that carries it.
        var fallback = properties.GetProperty("fallback");
        Assert.Equal(["East", "North"], fallback.GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.True(fallback.GetProperty("nullable").GetBoolean());
    }

    // A schema can say a field may be null in the enum instead of the type array. Google's enum field holds
    // strings, so the null cannot travel in it, and dropping it without saying so sent the field out forbidding
    // the value the source allowed.
    [Fact]
    public async Task ANullAmongTheEnumeratedValuesIsSentAsNullability()
    {
        var declaration = await DeclareAsync(
            new SchemaTool(
                "steer",
                """{"type":"object","properties":{"heading":{"type":"string","enum":["East","North",null]}}}"""));

        var heading = declaration.GetProperty("parameters").GetProperty("properties").GetProperty("heading");

        Assert.Equal(["East", "North"], heading.GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.True(heading.GetProperty("nullable").GetBoolean());
    }

    // An enum that lists nothing says no value is acceptable. Leaving the keyword off says the opposite, so the
    // field would go out accepting any string.
    [Fact]
    public async Task AnEnumThatListsNothingIsCarriedAcrossRatherThanDropped()
    {
        var declaration = await DeclareAsync(
            new SchemaTool(
                "steer",
                """{"type":"object","properties":{"heading":{"type":"string","enum":[]}}}"""));

        var heading = declaration.GetProperty("parameters").GetProperty("properties").GetProperty("heading");

        Assert.Empty(heading.GetProperty("enum").EnumerateArray());
    }

    [Fact]
    public async Task AConstantBecomesASingleValuedEnum()
    {
        var declaration = await DeclareAsync(
            new SchemaTool(
                "classify",
                """{"type":"object","properties":{"kind":{"const":"review"},"count":{"const":7}}}"""));

        var properties = declaration.GetProperty("parameters").GetProperty("properties");
        Assert.Equal(["review"], properties.GetProperty("kind").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));

        // The field holds strings whatever the constant was written as.
        Assert.Equal(["7"], properties.GetProperty("count").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        AssertNoFieldNamed(declaration, "const");
    }

    /// <summary>
    ///     Google rejects an object whose property map is empty, and the SDK writes a parameterless tool as
    ///     exactly that. The field is optional, so it is left off.
    /// </summary>
    [Fact]
    public async Task AToolThatTakesNoArgumentsDeclaresNoParametersField()
    {
        var declaration = await DeclareAsync(AIFunctionFactory.Create(() => "pong", "ping", "Answers."));

        Assert.Equal("ping", declaration.GetProperty("name").GetString());
        Assert.Equal("Answers.", declaration.GetProperty("description").GetString());
        Assert.False(declaration.TryGetProperty("parameters", out _));
    }

    /// <summary>
    ///     An open-ended map loses its value schema, because Google has no field for one, and would then be an
    ///     object declaring no properties, which Google rejects.
    /// </summary>
    [Fact]
    public async Task AnObjectWithNoDeclaredPropertiesIsNotSentAsAnEmptyObject()
    {
        var declaration = await DeclareAsync(AIFunctionFactory.Create((Dictionary<string, int> counts) => counts.Count, "tally", "Tallies."));

        var counts = declaration.GetProperty("parameters").GetProperty("properties").GetProperty("counts");
        Assert.Equal("STRING", counts.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(counts.GetProperty("description").GetString()));
        AssertNoFieldNamed(declaration, "additionalProperties");
    }

    [Fact]
    public async Task KeywordsGooglesMessageHasNoFieldForAreLeftOff()
    {
        var declaration = await DeclareAsync(
            new SchemaTool(
                "filter",
                """
                {
                  "$schema": "https://json-schema.org/draft/2020-12/schema",
                  "type": "object",
                  "additionalProperties": false,
                  "patternProperties": { "^x-": { "type": "string" } },
                  "properties": {
                    "name": { "type": "string", "description": "kept", "minLength": 2, "uniqueItems": true },
                    "score": { "type": "integer", "exclusiveMinimum": 0, "multipleOf": 2 }
                  },
                  "allOf": [ { "required": ["name"] } ],
                  "required": ["name"]
                }
                """));

        var name = declaration.GetProperty("parameters").GetProperty("properties").GetProperty("name");
        Assert.Equal("kept", name.GetProperty("description").GetString());
        Assert.Equal(2, name.GetProperty("minLength").GetInt32());

        foreach (var dropped in (string[])["$schema", "additionalProperties", "patternProperties", "allOf", "uniqueItems", "exclusiveMinimum", "multipleOf"])
        {
            AssertNoFieldNamed(declaration, dropped);
        }

        Assert.Equal(["name"], declaration.GetProperty("parameters").GetProperty("required").EnumerateArray().Select(value => value.GetString()));
    }

    /// <summary>
    ///     A type array naming more than one type has no single-enum equivalent. It is stated as the
    ///     alternatives Google's own field for them holds, so no member is silently dropped.
    /// </summary>
    [Fact]
    public async Task ATypeArrayWithSeveralTypesBecomesAnyOf()
    {
        var declaration = await DeclareAsync(
            new SchemaTool(
                "accept",
                """{"type":"object","properties":{"value":{"type":["string","integer","null"],"description":"either"}}}"""));

        var value = declaration.GetProperty("parameters").GetProperty("properties").GetProperty("value");
        Assert.False(value.TryGetProperty("type", out _));
        Assert.Equal("either", value.GetProperty("description").GetString());
        Assert.True(value.GetProperty("nullable").GetBoolean());
        Assert.Equal(
            ["STRING", "INTEGER"],
            value.GetProperty("anyOf").EnumerateArray().Select(branch => branch.GetProperty("type").GetString()));

        AssertEveryTypeIsOneName(declaration);
    }

    /// <summary>Sends one tool through the client and returns the declaration that reached the wire.</summary>
    /// <param name="tool">The tool to declare.</param>
    private static async Task<JsonElement> DeclareAsync(AITool tool)
    {
        var endpoint = new FakeGoogleEndpoint().Responds(
            """{"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP"}],"modelVersion":"gemini-3-pro"}""");

        var client = new GoogleGenerateContentChatClient(
            new FakeProviderHostContext(endpoint).Http,
            new GoogleCredentialSource(),
            new ProviderEndpoint(
                "meisterdev/googleVertex",
                "https://generativelanguage.googleapis.com",
                GoogleVertexProviderDriver.ApiKeyAuth,
                "gemini-key"),
            Model);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "go")], new ChatOptions { Tools = [tool] });

        return JsonDocument.Parse(endpoint.Bodies[0]).RootElement
            .GetProperty("tools")[0].GetProperty("functionDeclarations")[0].Clone();
    }

    /// <summary>Asserts that no type anywhere in the payload is a list, which is the shape Google rejects.</summary>
    /// <param name="element">The payload to walk.</param>
    private static void AssertEveryTypeIsOneName(JsonElement element)
    {
        foreach (var (name, value) in Fields(element))
        {
            if (name == "type")
            {
                Assert.Equal(JsonValueKind.String, value.ValueKind);
            }
        }
    }

    private static void AssertNoFieldNamed(JsonElement element, string name)
    {
        Assert.DoesNotContain(name, Fields(element).Select(field => field.Name));
    }

    private static int Depth(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => 1 + element.EnumerateObject().Select(field => Depth(field.Value)).DefaultIfEmpty(0).Max(),
            JsonValueKind.Array => 1 + element.EnumerateArray().Select(Depth).DefaultIfEmpty(0).Max(),
            _ => 0,
        };
    }

    private static IEnumerable<(string Name, JsonElement Value)> Fields(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var field in element.EnumerateObject())
                {
                    yield return (field.Name, field.Value);
                    foreach (var nested in Fields(field.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var entry in element.EnumerateArray())
                {
                    foreach (var nested in Fields(entry))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    /// <summary>A tool whose schema is written out, for a shape the SDK does not produce from a signature.</summary>
    /// <param name="name">The tool's name.</param>
    /// <param name="schema">The schema the tool publishes.</param>
    private sealed class SchemaTool(string name, string schema) : AIFunction
    {
        private readonly JsonElement _schema = JsonDocument.Parse(schema).RootElement.Clone();

        public override string Name => name;

        public override string Description => "A tool declared with a written-out schema.";

        public override JsonElement JsonSchema => this._schema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken) => ValueTask.FromResult<object?>(null);
    }

    /// <summary>A parameter type with a nullable member, for the nested-object translation.</summary>
    public sealed class Inner
    {
        public string Name { get; set; } = string.Empty;

        public int? Count { get; set; }
    }

    /// <summary>A parameter type holding another object and an array.</summary>
    public sealed class Outer
    {
        public Inner? Child { get; set; }

        public string[] Tags { get; set; } = [];
    }

    /// <summary>A parameter type defined in terms of itself, for the cycle guard.</summary>
    public sealed class Node
    {
        public string Label { get; set; } = string.Empty;

        public Node? Child { get; set; }

        public List<Node>? Children { get; set; }
    }

    /// <summary>A parameter type the SDK writes out as an enumerated set of strings.</summary>
    public enum Direction
    {
        /// <summary>Towards the east.</summary>
        East,

        /// <summary>Towards the north.</summary>
        North,
    }
}
