// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace System.IO.Pipelines
{
    internal sealed class PipeReaderStream : Stream, IValueTaskSource<int>
    {
        private readonly PipeReader _pipeReader;
        private Action _onAsyncReadCompleted;
        private ValueTaskAwaiter<ReadResult> _awaiter;
        private Memory<byte> _buffer;
        private ManualResetValueTaskSourceCore<int> _tcs = new ManualResetValueTaskSourceCore<int> { RunContinuationsAsynchronously = true };

        public PipeReaderStream(PipeReader pipeReader, bool leaveOpen)
        {
            Debug.Assert(pipeReader != null);
            _pipeReader = pipeReader;
            LeaveOpen = leaveOpen;
            _onAsyncReadCompleted = OnAsyncReadCompleted;
        }

        protected override void Dispose(bool disposing)
        {
            if (!LeaveOpen)
            {
                _pipeReader.Complete();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        internal bool LeaveOpen { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public sealed override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state) =>
            TaskToApm.Begin(ReadAsync(buffer, offset, count, default), callback, state);

        public sealed override int EndRead(IAsyncResult asyncResult) =>
            TaskToApm.End<int>(asyncResult);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsyncInternal(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
        }

#if !netstandard
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ReadAsyncInternal(buffer, cancellationToken);
        }
#endif

        private ValueTask<int> ReadAsyncInternal(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            _awaiter = _pipeReader.ReadAsync(cancellationToken).GetAwaiter();

            if (_awaiter.IsCompleted)
            {
                ReadResult result = _awaiter.GetResult();

                if (result.IsCanceled)
                {
                    ThrowHelper.CreateOperationCanceledException_ReadCanceled();
                }

                ReadOnlySequence<byte> sequence = result.Buffer;
                long bufferLength = sequence.Length;
                SequencePosition consumed = sequence.Start;

                try
                {
                    if (bufferLength != 0)
                    {
                        int actual = (int)Math.Min(bufferLength, buffer.Length);

                        ReadOnlySequence<byte> slice = actual == bufferLength ? sequence : sequence.Slice(0, actual);
                        consumed = slice.End;
                        slice.CopyTo(buffer.Span);

                        return new ValueTask<int>(actual);
                    }

                    if (result.IsCompleted)
                    {
                        return new ValueTask<int>(0);
                    }
                }
                finally
                {
                    _pipeReader.AdvanceTo(consumed);
                }
            }

            _buffer = buffer;

            _awaiter.UnsafeOnCompleted(_onAsyncReadCompleted);

            return new ValueTask<int>(this, _tcs.Version);
        }

        private void OnAsyncReadCompleted()
        {
            ReadResult result = _awaiter.GetResult();
            Memory<byte> buffer = _buffer;

            if (result.IsCanceled)
            {
                _tcs.SetException(ThrowHelper.CreateOperationCanceledException_ReadCanceled());
                return;
            }

            ReadOnlySequence<byte> sequence = result.Buffer;
            long bufferLength = sequence.Length;
            SequencePosition consumed = sequence.Start;

            try
            {
                if (bufferLength != 0)
                {
                    int actual = (int)Math.Min(bufferLength, buffer.Length);

                    ReadOnlySequence<byte> slice = actual == bufferLength ? sequence : sequence.Slice(0, actual);
                    consumed = slice.End;
                    slice.CopyTo(buffer.Span);

                    _tcs.SetResult(actual);
                    return;
                }

                if (result.IsCompleted)
                {
                    _tcs.SetResult(0);
                    return;
                }
            }
            finally
            {
                _pipeReader.AdvanceTo(consumed);
            }

            // This is a buggy PipeReader implementation that returns 0 byte reads even though the PipeReader
            // isn't completed or canceled
            _tcs.SetException(ThrowHelper.CreateInvalidOperationException_InvalidZeroByteRead());
        }

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            // Delegate to CopyToAsync on the PipeReader
            return _pipeReader.CopyToAsync(destination, cancellationToken);
        }

        public int GetResult(short token)
        {
             return _tcs.GetResult(token);
        }

        public ValueTaskSourceStatus GetStatus(short token)
        {
            return _tcs.GetStatus(token);
        }

        public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            _tcs.OnCompleted(continuation, state, token, flags);
        }
    }
}
