// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Reads the JSON object of an evidence judge answer. The answer is parsed strictly first, from its first opening brace
///     to its last closing brace. When that fails, the reader repairs the defects that judge models produced at the end of
///     an answer, mostly after a quoted source line that ends in a brace: text after the complete object, a quotation mark
///     or a surplus brace where the object closes, an incomplete last member, a backslash that starts no valid escape
///     sequence, and a raw control character inside a string. The verdict and the confidence evidence precede the end of
///     the answer, so a repair keeps them and drops at most the incomplete member at the end. In recorded reviews, about one
///     answer in a hundred needed such a repair, and most of them confirmed the finding.
/// </summary>
internal static class JudgeResponseJson
{
    // The repair tries the latest positions at which a nested object or array closes. The defects occur at the end of
    // the answer, so a few positions are enough.
    private const int MaxClosingPositions = 8;

    /// <summary>
    ///     Returns the JSON object of <paramref name="text" />, or <see langword="null" /> when no repair turns the text
    ///     into one. The caller disposes the returned document.
    /// </summary>
    internal static JsonDocument? TryParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        if (TryParseObjectText(text[start..(end + 1)]) is { } strict)
        {
            return strict;
        }

        var repaired = RepairStrings(text[start..]);
        return TryParseFirstValue(repaired) ?? TryParseClosedPrefix(repaired);
    }

    private static JsonDocument? TryParseObjectText(string json)
    {
        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Reads the first complete JSON value and ignores the text after it.
    private static JsonDocument? TryParseFirstValue(string json)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        try
        {
            var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Cuts the text after a nested object or array that closes and closes the containers that are still open, starting
    // with the latest such position. A cut drops the members after that position, which are the incomplete end.
    private static JsonDocument? TryParseClosedPrefix(string json)
    {
        var open = new List<char>();
        var positions = new List<(int End, string Closers)>();
        var inString = false;
        var escaped = false;
        for (var index = 0; index < json.Length; index++)
        {
            var character = json[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character is '{' or '[')
            {
                open.Add(character == '{' ? '}' : ']');
            }
            else if (character is '}' or ']')
            {
                // A closer that does not match, or the close of the outermost object, ends the scan: a complete outermost
                // object that still failed to parse is broken inside.
                if (open.Count == 0 || open[^1] != character)
                {
                    break;
                }

                open.RemoveAt(open.Count - 1);
                if (open.Count == 0)
                {
                    break;
                }

                positions.Add((index + 1, string.Concat(Enumerable.Reverse(open))));
            }
        }

        for (var position = positions.Count - 1; position >= Math.Max(0, positions.Count - MaxClosingPositions); position--)
        {
            var (end, closers) = positions[position];
            if (TryParseObjectText(json[..end] + closers) is { } document)
            {
                return document;
            }
        }

        return null;
    }

    // Escapes a backslash that starts no valid escape sequence and a raw control character, both only inside strings.
    private static string RepairStrings(string json)
    {
        var builder = new StringBuilder(json.Length + 16);
        var inString = false;
        for (var index = 0; index < json.Length; index++)
        {
            var character = json[index];
            if (!inString)
            {
                builder.Append(character);
                inString = character == '"';
                continue;
            }

            if (character == '\\')
            {
                var next = index + 1 < json.Length ? json[index + 1] : '\0';
                if (next is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't' || (next == 'u' && IsUnicodeEscape(json, index + 2)))
                {
                    builder.Append(character).Append(next);
                    index++;
                }
                else
                {
                    builder.Append(@"\\");
                }

                continue;
            }

            if (character == '"')
            {
                inString = false;
                builder.Append(character);
            }
            else if (character < ' ')
            {
                builder.Append(
                    character switch
                    {
                        '\n' => @"\n",
                        '\r' => @"\r",
                        '\t' => @"\t",
                        _ => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture),
                    });
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool IsUnicodeEscape(string json, int start)
    {
        return start + 4 <= json.Length && json.AsSpan(start, 4).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
    }
}
