// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Test.Common
{
    internal class VirtualNetworkStream : Stream
    {
        private readonly VirtualNetwork _network;
        private MemoryStream _readStream;
        private readonly bool _isServer;
        private SemaphoreSlim _readStreamLock = new SemaphoreSlim(1, 1);
        private TaskCompletionSource _flushTcs;

        public VirtualNetworkStream(VirtualNetwork network, bool isServer)
        {
            _network = network;
            _isServer = isServer;
        }

        public int DelayMilliseconds { get; set; }

        public bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotImplementedException();

        public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public override void Flush() => HasBeenSyncFlushed = true;

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_flushTcs != null)
            {
                throw new InvalidOperationException();
            }
            _flushTcs = new TaskCompletionSource();

            return _flushTcs.Task;
        }

        public bool HasBeenSyncFlushed { get; private set; }

        public void CompleteAsyncFlush()
        {
            if (_flushTcs == null)
            {
                throw new InvalidOperationException();
            }

            _flushTcs.SetResult();
            _flushTcs = null;
        }

        public override void SetLength(long value) => throw new NotImplementedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            _readStreamLock.Wait();
            try
            {
                if (_readStream == null || (_readStream.Position >= _readStream.Length))
                {
                    _readStream = new MemoryStream(_network.ReadFrame(_isServer));
                }

                return _readStream.Read(buffer, offset, count);
            }
            finally
            {
                _readStreamLock.Release();
            }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await _readStreamLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (DelayMilliseconds > 0)
                {
                    await Task.Delay(DelayMilliseconds, cancellationToken);
                }

                if (_readStream == null || (_readStream.Position >= _readStream.Length))
                {
                    _readStream = new MemoryStream(await _network.ReadFrameAsync(_isServer, cancellationToken).ConfigureAwait(false));
                }

                return await _readStream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _readStreamLock.Release();
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _network.WriteFrame(_isServer, buffer.AsSpan(offset, count).ToArray());
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (DelayMilliseconds > 0)
            {
                await Task.Delay(DelayMilliseconds, cancellationToken);
            }

            Write(buffer, offset, count);
        }

        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state) =>
            TaskToApm.Begin(ReadAsync(buffer, offset, count), callback, state);

        public override int EndRead(IAsyncResult asyncResult) =>
            TaskToApm.End<int>(asyncResult);

        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state) =>
            TaskToApm.Begin(WriteAsync(buffer, offset, count), callback, state);

        public override void EndWrite(IAsyncResult asyncResult) =>
            TaskToApm.End(asyncResult);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
                _network.BreakConnection();
            }

            base.Dispose(disposing);
        }
    }
}
