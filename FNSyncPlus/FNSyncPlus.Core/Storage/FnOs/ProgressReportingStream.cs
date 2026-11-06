using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FNSyncPlus.Core.Storage.FnOs
{
    /// <summary>
    /// 支持进度报告的流包装器，分块流式传输数据时精准汇报累计字节，零额外大内存分配
    /// </summary>
    public class ProgressReportingStream : Stream
    {
        private readonly Stream _innerStream;
        private readonly IProgress<long> _progress;
        private long _totalRead;

        public ProgressReportingStream(Stream innerStream, IProgress<long> progress, long initialOffset = 0)
        {
            _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
            _progress = progress ?? throw new ArgumentNullException(nameof(progress));
            _totalRead = initialOffset;
        }

        public override bool CanRead => _innerStream.CanRead;
        public override bool CanSeek => _innerStream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _innerStream.Length;
        public override long Position
        {
            get => _innerStream.Position;
            set => _innerStream.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _innerStream.Read(buffer, offset, count);
            if (read > 0)
            {
                _totalRead += read;
                _progress.Report(_totalRead);
            }
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                _totalRead += read;
                _progress.Report(_totalRead);
            }
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int read = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                _totalRead += read;
                _progress.Report(_totalRead);
            }
            return read;
        }

        public override void Flush() => _innerStream.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
        public override void SetLength(long value) => _innerStream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _innerStream.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
