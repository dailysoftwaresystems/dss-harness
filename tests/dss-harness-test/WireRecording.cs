using System.Globalization;

namespace DssHarness.Tests;

/// <summary>
/// Writes each request's body and its answer as the wire carried them into <paramref name="directory"/> - the
/// answer as it is read, so the stream stays one - and never a header, so never the key: a live run's record of what
/// it sent and was sent, each pair named by a count from one (<c>01-request.json</c>, and <c>01-answer.sse</c>, or
/// <c>.json</c> where the request was refused).
/// </summary>
internal sealed class WireRecording(HttpMessageHandler inner, string directory) : DelegatingHandler(inner)
{
    private const string Request = "-request.json";

    private int _sent;

    /// <summary>
    /// The streamed answer kept beside <paramref name="request"/> - a request's file as this recording names it - in the
    /// directory it is in: the one name of each pair's answer the readers of a recording use.
    /// </summary>
    public static string AnswerOf(string request)
    {
        var name = Path.GetFileName(request);

        if (!name.EndsWith(Request, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{name}' is no request this recording kept", nameof(request));
        }

        return Path.Combine(Path.GetDirectoryName(request)!, $"{name[..^Request.Length]}-answer.sse");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var at = Path.Combine(Directory.CreateDirectory(directory).FullName, Interlocked.Increment(ref _sent).ToString("D2", CultureInfo.InvariantCulture));
        await File.WriteAllBytesAsync($"{at}{Request}", await request.Content!.ReadAsByteArrayAsync(cancellationToken), cancellationToken);

        var response = await base.SendAsync(request, cancellationToken);
        var answer = new StreamContent(new Tee(await response.Content.ReadAsStreamAsync(cancellationToken), File.Create(response.IsSuccessStatusCode ? AnswerOf($"{at}{Request}") : $"{at}-answer.json")));

        foreach (var header in response.Content.Headers)
        {
            answer.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Content = answer;
        return response;
    }
}

/// <summary>A stream read as its source is, every byte read written to a copy as it comes.</summary>
internal sealed class Tee(Stream source, Stream copy) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        copy.Write(buffer, offset, read);
        copy.Flush();
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await source.ReadAsync(buffer, cancellationToken);
        await copy.WriteAsync(buffer[..read], cancellationToken);
        await copy.FlushAsync(cancellationToken);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            source.Dispose();
            copy.Dispose();
        }

        base.Dispose(disposing);
    }
}
