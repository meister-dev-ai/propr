// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Nodes;

namespace MeisterDev.Ai.Providers.GoogleVertexAddIn;

/// <summary>
///     Reads fields out of a body Google composed, in one place, so the driver and the chat client read them the
///     same way.
/// </summary>
internal static class GoogleJson
{
    /// <summary>The text a node holds, or <see langword="null" /> where it holds anything else.</summary>
    /// <remarks>
    ///     Read through the node's own type rather than by asking for a string, because a node holding a number,
    ///     an object or an array raises <see cref="InvalidOperationException" /> for that ask, and the reads here
    ///     are of a body a provider composed. A field that is not the text this family expects leaves the value
    ///     unread; it does not end the call.
    /// </remarks>
    /// <param name="node">The node to read.</param>
    internal static string? AsText(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    /// <summary>The field of an object, or <see langword="null" /> where the node is not an object.</summary>
    /// <remarks>
    ///     Indexing a node by name asks it for an object and raises <see cref="InvalidOperationException" /> where
    ///     it holds something else, so a nested read is taken a step at a time through the type.
    /// </remarks>
    /// <param name="node">The node the field is read from.</param>
    /// <param name="name">The field to read.</param>
    internal static JsonNode? Field(JsonNode? node, string name)
    {
        return (node as JsonObject)?[name];
    }
}
