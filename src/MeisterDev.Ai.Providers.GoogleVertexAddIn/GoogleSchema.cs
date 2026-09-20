// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeisterDev.Ai.Providers.GoogleVertexAddIn;

/// <summary>
///     Translates the JSON Schema a tool declares into the schema Google reads a function declaration's
///     parameters as.
/// </summary>
/// <remarks>
///     <para>
///         Google declares <c>FunctionDeclaration.parameters</c> as a protobuf message with a fixed field set,
///         not as a JSON Schema document. Two differences make an untranslated schema a rejected request. Its
///         <c>type</c> is a single enum value, and Microsoft.Extensions.AI writes a nullable parameter as the
///         two-member array <c>["string","null"]</c>, which the proto JSON parser cannot read into one enum
///         field. Its field set is closed, so a keyword the message has no field for is read as an unknown
///         name and fails the request.
///     </para>
///     <para>
///         The field set kept here is the one the Gemini API publishes for <c>Schema</c>, which is the smaller
///         of the two: Vertex additionally carries <c>$ref</c>, <c>$defs</c> and <c>additionalProperties</c>,
///         and one payload has to be valid on both hosts. That is why a reference is inlined, not forwarded.
///     </para>
///     <para>
///         Three shapes have no equivalent and are degraded. A reference that would re-enter a node already
///         being expanded is replaced with a string, because a recursive type cannot be inlined and Google
///         offers no way to name one. An object that declares no properties is replaced with a string, because
///         Google rejects <c>OBJECT</c> with an empty property map. A tool whose parameters come to nothing
///         leaves the field off, which Google documents as the shape for a function that takes no arguments.
///     </para>
/// </remarks>
internal static class GoogleSchema
{
    /// <summary>Says that a value could not be expanded, appended to whatever description the node carried.</summary>
    private const string RecursiveNote =
        "This value is defined in terms of itself and is not expanded here. Supply it as JSON text.";

    /// <summary>Says that an object's properties are not declared.</summary>
    private const string OpenObjectNote =
        "The properties of this object are not declared. Supply it as JSON text.";

    /// <summary>Says that a reference could not be followed.</summary>
    private const string UnresolvedNote =
        "This value refers to a definition that is not part of the tool's schema. Supply it as JSON text.";

    /// <summary>The fields that describe a node whatever its type, which a union keeps on the parent.</summary>
    private static readonly string[] SharedFields = ["description", "title"];

    /// <summary>The JSON Schema type names Google has an enum member for, at the names it reads them under.</summary>
    private static readonly Dictionary<string, string> TypeNames = new(StringComparer.Ordinal)
    {
        ["string"] = "STRING",
        ["number"] = "NUMBER",
        ["integer"] = "INTEGER",
        ["boolean"] = "BOOLEAN",
        ["array"] = "ARRAY",
        ["object"] = "OBJECT",
    };

    /// <summary>
    ///     The constraint fields Google's message carries for each type. Keyed by type so a branch of a union
    ///     carries only what applies to it, and so a constraint that belongs to another type is left off.
    /// </summary>
    private static readonly Dictionary<string, string[]> ConstraintFields = new(StringComparer.Ordinal)
    {
        ["STRING"] = ["format", "pattern", "minLength", "maxLength", "default", "example"],
        ["NUMBER"] = ["format", "minimum", "maximum", "default", "example"],
        ["INTEGER"] = ["format", "minimum", "maximum", "default", "example"],
        ["BOOLEAN"] = ["default", "example"],
        ["ARRAY"] = ["minItems", "maxItems", "default", "example"],
        ["OBJECT"] = ["minProperties", "maxProperties", "propertyOrdering", "default", "example"],
    };

    /// <summary>
    ///     The value of a function declaration's <c>parameters</c>, or <see langword="null" /> where the tool
    ///     declares none.
    /// </summary>
    /// <param name="jsonSchema">The schema the tool published.</param>
    /// <remarks>
    ///     Google rejects an <c>OBJECT</c> whose property map is empty, and Microsoft.Extensions.AI writes a
    ///     parameterless tool as exactly that. The field is optional, so a schema that translates to no named
    ///     parameters is reported as none, not sent as an empty object.
    /// </remarks>
    internal static JsonObject? ToFunctionParameters(JsonElement jsonSchema)
    {
        if (jsonSchema.ValueKind != JsonValueKind.Object
            || JsonNode.Parse(jsonSchema.GetRawText()) is not JsonObject root)
        {
            return null;
        }

        var translated = Translate(root, root, []);

        return GoogleJson.AsText(translated["type"]) == "OBJECT" && translated["properties"] is JsonObject { Count: > 0 }
            ? translated
            : null;
    }

    /// <summary>Translates one schema node.</summary>
    /// <param name="node">The node to translate, which need not be an object.</param>
    /// <param name="root">The whole schema, against which a reference is resolved.</param>
    /// <param name="path">The nodes currently being expanded, innermost last.</param>
    /// <remarks>
    ///     The path is what bounds the output. Every step either descends into a child or follows a reference to
    ///     a node that is not already on the path, so no node is expanded inside itself and the recursion ends on
    ///     a finite schema. Nodes are compared by reference because a schema parses to a tree in which two
    ///     equal-looking nodes are still distinct positions.
    /// </remarks>
    private static JsonObject Translate(JsonNode? node, JsonObject root, List<JsonNode> path)
    {
        if (node is not JsonObject schema)
        {
            // A boolean schema and an absent one both mean "any value". Google has no such node, and a string
            // is the loosest one it accepts: it has no property map to be empty and no item type to declare.
            return Loose(null);
        }

        if (OnPath(path, schema))
        {
            return Loose(Note(schema, RecursiveNote));
        }

        path.Add(schema);
        try
        {
            return TranslateBody(schema, root, path);
        }
        finally
        {
            path.RemoveAt(path.Count - 1);
        }
    }

    private static JsonObject TranslateBody(JsonObject schema, JsonObject root, List<JsonNode> path)
    {
        if (schema["$ref"] is { } reference)
        {
            // Inlined, not forwarded: the Gemini API's message has no reference field, so the target has to be
            // expanded in place. Sibling keywords are left off, because a reference that carries them is not
            // something Microsoft.Extensions.AI emits and merging them would guess at an intent.
            var target = Resolve(GoogleJson.AsText(reference), root);
            return target is null
                ? Loose(Note(schema, UnresolvedNote))
                : Translate(target, root, path);
        }

        var nullable = false;
        var types = ReadTypes(schema, ref nullable);

        var translated = types.Count > 1
            ? Union(schema, root, path, types)
            : Build(schema, root, path, types.Count == 1 ? types[0] : Infer(schema), shared: true);

        if (nullable)
        {
            translated["nullable"] = true;
        }

        return translated;
    }

    /// <summary>
    ///     Translates a node whose type array names more than one type, as one <c>anyOf</c> branch per type.
    /// </summary>
    /// <param name="schema">The node being translated.</param>
    /// <param name="root">The whole schema.</param>
    /// <param name="path">The nodes currently being expanded.</param>
    /// <param name="types">The named types, with <c>null</c> already taken off.</param>
    /// <remarks>
    ///     Google's <c>type</c> holds one enum value, so a union has no single-type equivalent. <c>anyOf</c> is
    ///     a field of the same message and states the alternatives without narrowing them, which taking one
    ///     member and dropping the others would do. The description stays on the parent and each branch carries
    ///     only the constraints its own type has.
    /// </remarks>
    private static JsonObject Union(JsonObject schema, JsonObject root, List<JsonNode> path, List<string> types)
    {
        var branches = new JsonArray();
        foreach (var type in types)
        {
            branches.Add(Build(schema, root, path, type, shared: false));
        }

        var translated = new JsonObject();
        CopyShared(schema, translated);
        translated["anyOf"] = branches;
        return translated;
    }

    /// <summary>Builds one node of a single Google type.</summary>
    /// <param name="schema">The node being translated.</param>
    /// <param name="root">The whole schema.</param>
    /// <param name="path">The nodes currently being expanded.</param>
    /// <param name="type">The Google type this node takes.</param>
    /// <param name="shared">Whether the description and title belong on this node or on a parent.</param>
    private static JsonObject Build(JsonObject schema, JsonObject root, List<JsonNode> path, string type, bool shared)
    {
        var translated = new JsonObject { ["type"] = type };

        if (shared)
        {
            CopyShared(schema, translated);
        }

        foreach (var field in ConstraintFields[type])
        {
            if (schema[field] is { } value)
            {
                translated[field] = value.DeepClone();
            }
        }

        var enumerated = Values(schema, out var enumeratedNull);
        if (enumerated is not null)
        {
            translated["enum"] = enumerated;

            // Google marks an enumerated field by its format, and documents that shape for string and integer
            // members alike. A format the schema stated for itself is left as it stands.
            translated["format"] ??= "enum";
        }

        // A null among the enumerated values is the source saying the field may be null. The type array is the
        // other way a schema says it, and Translate reads that one; both end at the same key. Without this a
        // node written as {"type":"string","enum":["a",null]} lost the null and came out forbidding it.
        if (enumeratedNull)
        {
            translated["nullable"] = true;
        }

        if (type == "ARRAY")
        {
            translated["items"] = Translate(Items(schema), root, path);
        }

        if (type != "OBJECT")
        {
            return translated;
        }

        AddProperties(schema, root, path, translated);

        // Google rejects an OBJECT whose property map is empty. An open-ended map becomes one once
        // additionalProperties is dropped, so it is reported as text and the tool stays callable.
        return translated["properties"] is JsonObject { Count: > 0 }
            ? translated
            : Loose(Note(schema, OpenObjectNote));
    }

    private static void AddProperties(JsonObject schema, JsonObject root, List<JsonNode> path, JsonObject translated)
    {
        if (schema["properties"] is not JsonObject properties)
        {
            return;
        }

        var declared = new JsonObject();
        foreach (var (name, value) in properties)
        {
            declared[name] = Translate(value, root, path);
        }

        if (declared.Count == 0)
        {
            return;
        }

        translated["properties"] = declared;

        if (schema["required"] is not JsonArray required)
        {
            return;
        }

        var names = new JsonArray();
        foreach (var entry in required)
        {
            // A name that no property answers to would leave the two fields disagreeing, so only the declared
            // ones are carried over.
            if (GoogleJson.AsText(entry) is { } name && declared.ContainsKey(name))
            {
                names.Add(JsonValue.Create(name));
            }
        }

        if (names.Count > 0)
        {
            translated["required"] = names;
        }
    }

    private static void CopyShared(JsonObject schema, JsonObject translated)
    {
        foreach (var field in SharedFields)
        {
            if (schema[field] is { } value)
            {
                translated[field] = value.DeepClone();
            }
        }
    }

    /// <summary>The Google type names a node states, with <c>null</c> taken off as nullability.</summary>
    /// <param name="schema">The node being read.</param>
    /// <param name="nullable">Set when the node named the null type among its own.</param>
    private static List<string> ReadTypes(JsonObject schema, ref bool nullable)
    {
        var stated = new List<string>();
        switch (schema["type"])
        {
            case JsonValue value when value.TryGetValue<string>(out var single):
                stated.Add(single);
                break;

            case JsonArray many:
                foreach (var entry in many)
                {
                    if (GoogleJson.AsText(entry) is { } name)
                    {
                        stated.Add(name);
                    }
                }

                break;
        }

        var types = new List<string>();
        foreach (var name in stated)
        {
            if (string.Equals(name, "null", StringComparison.Ordinal))
            {
                nullable = true;
                continue;
            }

            if (TypeNames.TryGetValue(name, out var type) && !types.Contains(type))
            {
                types.Add(type);
            }
        }

        return types;
    }

    /// <summary>The type of a node that states none, read from what else it declares.</summary>
    /// <param name="schema">The node being read.</param>
    /// <remarks>
    ///     Google documents <c>type</c> as required, so a node that reaches here without one is given the type
    ///     its own keywords imply, and a string where they imply nothing.
    /// </remarks>
    private static string Infer(JsonObject schema)
    {
        if (schema["properties"] is JsonObject)
        {
            return "OBJECT";
        }

        return schema["items"] is not null ? "ARRAY" : "STRING";
    }

    /// <summary>The enumerated values of a node, as the strings Google's repeated field holds.</summary>
    /// <param name="schema">The node being read.</param>
    /// <remarks>
    ///     A <c>const</c> is one enumerated value and is carried as one. Google's field holds strings, so a null
    ///     member cannot be carried in it; it is reported through <paramref name="enumeratedNull" /> so the
    ///     caller can say what it meant, that the field may be null.
    ///     <para>
    ///         A source enum that lists nothing is carried across as an empty one rather than left off. It states
    ///         that no value is acceptable, and omitting the keyword says the opposite: the field would go out
    ///         accepting anything of its type.
    ///     </para>
    /// </remarks>
    /// <param name="enumeratedNull">Set when the enumerated values included a null.</param>
    private static JsonArray? Values(JsonObject schema, out bool enumeratedNull)
    {
        enumeratedNull = false;
        var values = new JsonArray();

        if (schema["enum"] is JsonArray listed)
        {
            foreach (var entry in listed)
            {
                if (Literal(entry) is { } text)
                {
                    values.Add(JsonValue.Create(text));
                }
                else if (entry is null || entry.GetValueKind() == JsonValueKind.Null)
                {
                    enumeratedNull = true;
                }
            }

            // Returned whatever it holds. An enum listing only a null leaves nothing Google's field can carry,
            // and an empty one is what the source stated; both say the field takes no value of its own, and
            // returning null here would drop the keyword and say it takes any.
            return values;
        }

        if (Literal(schema["const"]) is { } only)
        {
            values.Add(JsonValue.Create(only));
        }

        return values.Count > 0 ? values : null;
    }

    private static string? Literal(JsonNode? node)
    {
        return node switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            _ => node.ToJsonString(),
        };
    }

    /// <summary>The schema an array's members take.</summary>
    /// <param name="schema">The array node being read.</param>
    /// <remarks>
    ///     Google holds one item schema. A draft that states a list of them describes a tuple, whose first
    ///     member is the closest single schema to it.
    /// </remarks>
    private static JsonNode? Items(JsonObject schema)
    {
        return schema["items"] switch
        {
            JsonArray tuple => tuple.Count > 0 ? tuple[0] : null,
            var single => single,
        };
    }

    /// <summary>The node a JSON Pointer names, or <see langword="null" /> where it names nothing readable.</summary>
    /// <param name="reference">The reference as the schema stated it.</param>
    /// <param name="root">The schema the pointer is resolved against.</param>
    /// <remarks>
    ///     Only a pointer into the same document is followed, which covers both the self-pointer
    ///     Microsoft.Extensions.AI emits for a repeated type and the <c>$defs</c> entry a hand-written schema
    ///     uses. Fetching an external document is not something a tool declaration may make this driver do.
    /// </remarks>
    private static JsonNode? Resolve(string? reference, JsonObject root)
    {
        if (reference is null || reference.Length == 0 || reference[0] != '#')
        {
            return null;
        }

        var pointer = reference[1..];
        if (pointer.Length == 0)
        {
            return root;
        }

        if (pointer[0] != '/')
        {
            return null;
        }

        JsonNode? node = root;
        foreach (var segment in pointer[1..].Split('/'))
        {
            var token = Uri.UnescapeDataString(segment)
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);

            node = node switch
            {
                JsonObject entries => entries[token],
                JsonArray entries when int.TryParse(token, out var index) && index >= 0 && index < entries.Count =>
                    entries[index],
                _ => null,
            };

            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    private static bool OnPath(List<JsonNode> path, JsonNode node)
    {
        foreach (var entry in path)
        {
            if (ReferenceEquals(entry, node))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonObject Loose(string? description)
    {
        var translated = new JsonObject { ["type"] = "STRING" };
        if (!string.IsNullOrEmpty(description))
        {
            translated["description"] = description;
        }

        return translated;
    }

    private static string Note(JsonObject schema, string note)
    {
        var description = GoogleJson.AsText(schema["description"]);
        return string.IsNullOrEmpty(description) ? note : description + " " + note;
    }
}
