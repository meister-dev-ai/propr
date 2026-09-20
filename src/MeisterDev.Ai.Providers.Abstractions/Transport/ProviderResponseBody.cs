// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Buffers;
using System.Text;

namespace MeisterDev.Ai.Providers.Transport;

/// <summary>
///     Reads a bounded amount of a provider's response body.
/// </summary>
/// <remarks>
///     <para>
///         A response body is whatever the endpoint at the other end chose to send, and an operator-supplied base
///         URL means the endpoint is not necessarily the vendor's. An unbounded read puts the whole of it in the
///         host's memory before anything looks at its length, so the configuration paths read through here.
///     </para>
///     <para>
///         Two bounds, because the two uses differ. A document the host has to parse is read whole or not at all,
///         so its bound is far above any model list a provider publishes and exceeding it is a failure. A body
///         kept only to tell an operator what the endpoint said is read as far as it is worth reading, and the
///         rest is dropped.
///     </para>
/// </remarks>
public static class ProviderResponseBody
{
    /// <summary>
    ///     How much of a refused call's body reaches the operator who reads why it failed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Nothing is swallowed. The body is read up to this bound and the rest is not read at all, so what
    ///         the operator sees is the opening of what the provider answered, carried into the verification
    ///         summary or the exception message that reports the call. The status the provider answered with is
    ///         separate and always kept.
    ///     </para>
    ///     <para>
    ///         Four kilobytes because a provider's refusal states its reason first: a JSON error object runs to a
    ///         few hundred bytes, and an HTML error page from a proxy in front of one states its title and its
    ///         heading well inside this. What the bound is against is a provider that echoes the request back in
    ///         its refusal, which makes the rest as long as the prompt was, and a truncated body is not the
    ///         failure being hidden — it is the part after the reason.
    ///     </para>
    ///     <para>
    ///         The same bound is applied where a stated retry delay is read out of a failure message.
    ///     </para>
    /// </remarks>
    public const int MaximumDetailBytes = 4096;

    /// <summary>How much of a document the host has to parse it will read.</summary>
    /// <remarks>
    ///     Model lists run to tens of kilobytes at the largest gateways, so this is orders of magnitude above what
    ///     the call needs. It bounds memory; no call is expected to approach it.
    /// </remarks>
    public const int MaximumDocumentBytes = 4 * 1024 * 1024;

    /// <summary>How much is read from the stream at a time.</summary>
    private const int ChunkBytes = 8192;

    /// <summary>Reads at most <paramref name="maximumBytes" /> of <paramref name="content" />.</summary>
    /// <param name="content">The response body to read.</param>
    /// <param name="maximumBytes">The most that will be kept.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>
    ///     What was read, and whether the body went on past the bound. A body cut mid-character decodes its last
    ///     character as the replacement character rather than failing the read.
    /// </returns>
    public static async Task<(string Text, bool Truncated)> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);

        // One byte past the bound, so a body that ends exactly on it is not reported as truncated.
        var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        var kept = new ArrayBufferWriter<byte>(Math.Min(maximumBytes, ChunkBytes));

        try
        {
            while (kept.WrittenCount <= maximumBytes)
            {
                // Widened for the arithmetic. The bound is a caller's number, and one byte past int.MaxValue
                // wraps negative, which turns the next read length negative and throws instead of reading.
                var room = (int)Math.Min(ChunkBytes, (long)maximumBytes + 1 - kept.WrittenCount);
                var read = await stream.ReadAsync(buffer.AsMemory(0, room), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                kept.Write(buffer.AsSpan(0, read));
            }
        }
        finally
        {
            // Cleared on the way back. A provider's response body can hold a credential the request echoed, and
            // the next renter of this array sees whatever was left in it.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        var truncated = kept.WrittenCount > maximumBytes;
        var text = EncodingOf(content).GetString(kept.WrittenSpan[..Math.Min(kept.WrittenCount, maximumBytes)]);

        return (text, truncated);
    }

    /// <summary>
    ///     The encoding the body states, falling back to UTF-8 for one that states none or names an encoding this
    ///     runtime does not have.
    /// </summary>
    /// <param name="content">The response body whose headers are read.</param>
    private static Encoding EncodingOf(HttpContent content)
    {
        var charSet = content.Headers.ContentType?.CharSet;
        if (string.IsNullOrWhiteSpace(charSet))
        {
            return Encoding.UTF8;
        }

        try
        {
            // Quoted by some servers, which Encoding.GetEncoding does not accept.
            return Encoding.GetEncoding(charSet.Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}
