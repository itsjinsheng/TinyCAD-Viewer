using System.IO;

namespace TinyCAD;

public sealed record CadLoadProgress(string Stage, int Objects = 0);

// Check cancellation even during parsing phases that emit no notifications.
internal sealed class CancelableReadStream(Stream inner, CancellationToken token) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set { token.ThrowIfCancellationRequested(); inner.Position = value; } }
    public override int Read(byte[] buffer, int offset, int count) { token.ThrowIfCancellationRequested(); return inner.Read(buffer, offset, count); }
    public override int Read(Span<byte> buffer) { token.ThrowIfCancellationRequested(); return inner.Read(buffer); }
    public override int ReadByte() { token.ThrowIfCancellationRequested(); return inner.ReadByte(); }
    public override long Seek(long offset, SeekOrigin origin) { token.ThrowIfCancellationRequested(); return inner.Seek(offset, origin); }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
