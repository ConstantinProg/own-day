namespace OwnDay.Infrastructure.Diagnostics;

internal sealed class DiagnosticExportLimitException : Exception;

internal sealed class DiagnosticLimitedStream(Stream destination, long maximum) : Stream
{
    private long _written;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _written;
    public override long Position { get => _written; set => throw new NotSupportedException(); }
    public override void Flush() => destination.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Check(buffer.Length);
        destination.Write(buffer);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Check(buffer.Length);
        return destination.WriteAsync(buffer, cancellationToken);
    }

    private void Check(int next)
    {
        if (next > maximum - _written) throw new DiagnosticExportLimitException();
        _written += next;
    }
}
