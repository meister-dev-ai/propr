// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net.Http.Headers;
using System.Text;
using MeisterDev.Ai.Providers.Transport;

namespace MeisterDev.Ai.Providers.Tests.Transport;

/// <summary>
///     How much of a provider's response body the host reads, and what it does with a body that goes on past it.
/// </summary>
public sealed class ProviderResponseBodyTests
{
    [Fact]
    public async Task ABodyWithinTheBoundIsReadWholeAndNotReportedAsTruncated()
    {
        using var content = new StringContent("the endpoint refused the credential");

        var (text, truncated) = await ProviderResponseBody.ReadBoundedAsync(content, 4096);

        Assert.Equal("the endpoint refused the credential", text);
        Assert.False(truncated);
    }

    [Fact]
    public async Task ABodyEndingExactlyOnTheBoundIsNotReportedAsTruncated()
    {
        using var content = new StringContent(new string('x', 64));

        var (text, truncated) = await ProviderResponseBody.ReadBoundedAsync(content, 64);

        Assert.Equal(64, text.Length);
        Assert.False(truncated);
    }

    // The point of the bound: what the host holds is what it read, not what the endpoint chose to send.
    [Fact]
    public async Task ABodyLongerThanTheBoundIsReadOnlyUpToItAndReportedAsTruncated()
    {
        using var content = new StringContent(new string('x', 512 * 1024));

        var (text, truncated) = await ProviderResponseBody.ReadBoundedAsync(content, 4096);

        Assert.Equal(4096, text.Length);
        Assert.True(truncated);
    }

    // A stream that hands over a few bytes at a time is the ordinary case over a network, and the bound has to
    // hold across however many reads it takes.
    [Fact]
    public async Task TheBoundHoldsWhenTheBodyArrivesInSmallPieces()
    {
        using var content = new StreamContent(new DribblingStream(Encoding.UTF8.GetBytes(new string('y', 40_000))));

        var (text, truncated) = await ProviderResponseBody.ReadBoundedAsync(content, 1000);

        Assert.Equal(1000, text.Length);
        Assert.True(truncated);
    }

    [Fact]
    public async Task AnEmptyBodyReadsAsAnEmptyString()
    {
        using var content = new StringContent(string.Empty);

        var (text, truncated) = await ProviderResponseBody.ReadBoundedAsync(content, 4096);

        Assert.Equal(string.Empty, text);
        Assert.False(truncated);
    }

    [Fact]
    public async Task ABodyStatingAnEncodingIsDecodedWithIt()
    {
        using var content = new ByteArrayContent(Encoding.Latin1.GetBytes("refusé"));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "iso-8859-1" };

        var (text, _) = await ProviderResponseBody.ReadBoundedAsync(content, 4096);

        Assert.Equal("refusé", text);
    }

    // A provider is free to name an encoding this runtime does not have. Failing the read would replace the
    // provider's own refusal with a failure to read it, so the read falls back rather than throwing.
    [Fact]
    public async Task ABodyNamingAnUnknownEncodingIsStillRead()
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("refused"));
        content.Headers.TryAddWithoutValidation("Content-Type", "text/plain; charset=not-an-encoding");

        var (text, _) = await ProviderResponseBody.ReadBoundedAsync(content, 4096);

        Assert.Equal("refused", text);
    }

    /// <summary>A stream that returns fewer bytes per read than it was asked for.</summary>
    /// <param name="payload">Everything the stream will hand over.</param>
    private sealed class DribblingStream(byte[] payload) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => payload.Length;

        public override long Position
        {
            get => this._position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var taken = Math.Min(Math.Min(count, 7), payload.Length - this._position);
            Array.Copy(payload, this._position, buffer, offset, taken);
            this._position += taken;
            return taken;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
