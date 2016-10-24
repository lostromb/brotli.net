﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Brotli
{
    public class BrotliStream : Stream
    {
        const int BufferSize = 64 * 1024;
        protected Stream _stream = null;
        protected MemoryStream _intermediateStream = new MemoryStream();
        protected CompressionMode _mode = CompressionMode.Compress;
        protected IntPtr _state = IntPtr.Zero;
        protected IntPtr _ptrInputBuffer = IntPtr.Zero;
        protected IntPtr _ptrOutputBuffer = IntPtr.Zero;

        protected IntPtr _ptrNextInput = IntPtr.Zero;
        protected IntPtr _ptrNextOutput = IntPtr.Zero;
        protected UInt32 _availableIn = 0;
        protected UInt32 _availableOut = BufferSize;

        protected Byte[] _managedBuffer;
        protected Boolean _endOfStream = false;
        protected int _readOffset = 0;
        protected BrotliDecoderResult _lastDecodeResult = BrotliDecoderResult.NeedsMoreInput;
        public BrotliStream(Stream baseStream, CompressionMode mode)
        {
            _mode = mode;
            _stream = baseStream;
            if (_mode == CompressionMode.Compress)
            {
                _state = Brolib.BrotliEncoderCreateInstance();
                if (_state == IntPtr.Zero)
                {
                    throw new IOException("Unable to create brotli encoder instance");
                }
                Brolib.BrotliEncoderSetParameter(_state, BrotliEncoderParameter.Quality, 5);
                Brolib.BrotliEncoderSetParameter(_state, BrotliEncoderParameter.LGWin, 22);
            }
            else
            {
                _state = Brolib.BrotliDecoderCreateInstance();
                if (_state == IntPtr.Zero)
                {
                    throw new IOException("Unable to create brotli decoder instance");
                }
            }
            _ptrInputBuffer = Marshal.AllocHGlobal(BufferSize);
            _ptrOutputBuffer = Marshal.AllocHGlobal(BufferSize);
            _ptrNextInput = _ptrInputBuffer;
            _ptrNextOutput = _ptrOutputBuffer;

            _managedBuffer = new Byte[BufferSize];
        }

        /// <summary>
        /// Set the compress quality(0~11)
        /// </summary>
        /// <param name="quality">compress quality</param>
        public void SetQuality(uint quality)
        {
            if (quality < 0 || quality > 11)
            {
                throw new ArgumentException("quality", "the range of quality is 0~11");
            }
            Brolib.BrotliEncoderSetParameter(_state, BrotliEncoderParameter.Quality, quality);
        }

        /// <summary>
        /// Set the compress LGWin(10~24)
        /// </summary>
        /// <param name="window">the window size</param>
        public void SetWindow(uint window)
        {
            if (window < 10 || window > 24)
            {
                throw new ArgumentException("window", "the range of window is 10~24");
            }
            Brolib.BrotliEncoderSetParameter(_state, BrotliEncoderParameter.LGWin, window);
        }

        public override bool CanRead
        {
            get
            {
                if (_stream == null)
                {
                    return false;
                }

                return (_mode == System.IO.Compression.CompressionMode.Decompress && _stream.CanRead);
            }
        }

        public override bool CanSeek
        {
            get
            {
                return false;
            }
        }

        public override bool CanWrite
        {
            get
            {
                if (_stream == null)
                {
                    return false;
                }

                return (_mode == System.IO.Compression.CompressionMode.Compress && _stream.CanWrite);
            }
        }

        public override long Length
        {
            get
            {
                throw new NotImplementedException();
            }
        }

        public override long Position
        {
            get
            {
                throw new NotImplementedException();
            }

            set
            {
                throw new NotImplementedException();
            }
        }

        public override void Flush()
        {
            if (_stream == null)
            {
                throw new ObjectDisposedException(null, "Underlying stream is disposed");
            }
            if (_mode == CompressionMode.Compress)
            {
                FlushBrotliStream(false);
            }
        }

        protected virtual void FlushBrotliStream(Boolean finished)
        {
            if (Brolib.BrotliEncoderIsFinished(_state)) return;
            BrotliEncoderOperation op = finished ? BrotliEncoderOperation.Finish : BrotliEncoderOperation.Flush;
            UInt32 totalOut = 0;
            while (true)
            {
                var compressOK = Brolib.BrotliEncoderCompressStream(_state, op, ref _availableIn, ref _ptrNextInput, ref _availableOut, ref _ptrNextOutput, out totalOut);
                if (!compressOK) throw new InvalidOperationException("Unable to finish encode stream");
                var extraData = _availableOut != BufferSize;
                if (extraData)
                {
                    var bytesWrote = (int)(BufferSize - _availableOut);
                    Marshal.Copy(_ptrOutputBuffer, _managedBuffer, 0, bytesWrote);
                    _stream.Write(_managedBuffer, 0, bytesWrote);
                    _availableOut = BufferSize;
                    _ptrNextOutput = _ptrOutputBuffer;
                }
                if (Brolib.BrotliEncoderIsFinished(_state)) break;
                if (!extraData) break;
            }

        }

        protected override void Dispose(bool disposing)
        {
            if (_mode == CompressionMode.Compress)
            {
                FlushBrotliStream(true);
            }
            base.Dispose(disposing);
            _stream.Dispose();
            _intermediateStream.Dispose();
            Marshal.FreeHGlobal(_ptrInputBuffer);
            Marshal.FreeHGlobal(_ptrOutputBuffer);
            _managedBuffer = null;
        }

        public void TruncateBeginning(MemoryStream ms, int numberOfBytesToRemove)
        {
            byte[] buf = ms.GetBuffer();
            Buffer.BlockCopy(buf, numberOfBytesToRemove, buf, 0, (int)ms.Length - numberOfBytesToRemove);
            ms.SetLength(ms.Length - numberOfBytesToRemove);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_mode != CompressionMode.Decompress) throw new InvalidOperationException("Can't read on this stream");


            int bytesRead = (int)(_intermediateStream.Length - _readOffset);
            uint totalCount = 0;
            Boolean endOfStream = false;
            while (bytesRead < count)
            {
                while (true)
                {
                    if (_lastDecodeResult == BrotliDecoderResult.NeedsMoreInput)
                    {
                        _availableIn = (UInt32)_stream.Read(_managedBuffer, 0, (int)BufferSize);
                        _ptrNextInput = _ptrInputBuffer;
                        if (_availableIn <= 0)
                        {
                            endOfStream = true;
                            break;
                        }
                        Marshal.Copy(_managedBuffer, 0, _ptrInputBuffer, (int)_availableIn);
                    }
                    else if (_lastDecodeResult == BrotliDecoderResult.NeedsMoreOutput)
                    {
                        Marshal.Copy(_ptrOutputBuffer, _managedBuffer, 0, BufferSize);
                        _intermediateStream.Write(_managedBuffer, 0, BufferSize);
                        bytesRead += BufferSize;
                        _availableOut = BufferSize;
                        _ptrNextOutput = _ptrOutputBuffer;
                    }
                    else
                    {
                        //Error or OK
                        endOfStream = true;
                        break;
                    }
                    _lastDecodeResult = Brolib.BrotliDecoderDecompressStream(_state, ref _availableIn, ref _ptrNextInput,
                        ref _availableOut, ref _ptrNextOutput, out totalCount);
                    if (bytesRead >= count) break;
                }
                if (_lastDecodeResult == BrotliDecoderResult.Error)
                {
                    throw new InvalidOperationException("Unable to decode stream,possibly corrupt data");
                }

                if (endOfStream && _lastDecodeResult == BrotliDecoderResult.NeedsMoreOutput)
                {
                    throw new InvalidOperationException("Unable to decode stream,unexpected EOF");
                }

                if (endOfStream && _ptrNextOutput != _ptrOutputBuffer)
                {
                    int remainBytes = (int)(_ptrNextOutput.ToInt64() - _ptrOutputBuffer.ToInt64());
                    bytesRead += remainBytes;
                    Marshal.Copy(_ptrOutputBuffer, _managedBuffer, 0, remainBytes);
                    _intermediateStream.Write(_managedBuffer, 0, remainBytes);
                    _ptrNextOutput = _ptrOutputBuffer;
                }
                if (endOfStream) break;
            }

            if (_intermediateStream.Length - _readOffset >= count || endOfStream)
            {
                _intermediateStream.Seek(_readOffset, SeekOrigin.Begin);
                var bytesToRead = (int)(_intermediateStream.Length - _readOffset);
                if (bytesToRead > count) bytesToRead = count;
                _intermediateStream.Read(buffer, offset, bytesToRead);
                if (endOfStream)
                {
                    _intermediateStream.SetLength(0);
                }
                else
                {
                    TruncateBeginning(_intermediateStream, _readOffset + bytesToRead);
                }
                _readOffset = 0;
                return bytesToRead;
            }

            return 0;

        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        static int totalWrote = 0;
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_mode != CompressionMode.Compress) throw new InvalidOperationException("Can't write on this stream");
            totalWrote += count;
            Console.WriteLine(String.Format("Write {0} bytes,total={1} bytes.", count, totalWrote));

            UInt32 totalOut = 0;
            int bytesRemain = count;
            int currentOffset = offset;

            Boolean compressOK = true;
            while (bytesRemain > 0)
            {
                int copyLen = bytesRemain > BufferSize ? BufferSize : bytesRemain;
                Marshal.Copy(buffer, currentOffset, _ptrInputBuffer, copyLen);
                bytesRemain -= copyLen;
                currentOffset += copyLen;
                _availableIn = (UInt32)copyLen;
                _ptrNextInput = _ptrInputBuffer;
                while (_availableIn > 0)
                {
                    compressOK = Brolib.BrotliEncoderCompressStream(_state, BrotliEncoderOperation.Process, ref _availableIn, ref _ptrNextInput, ref _availableOut,
                        ref _ptrNextOutput, out totalOut);
                    if (!compressOK) throw new InvalidOperationException("Unable to compress stream");
                    if (_availableOut != BufferSize)
                    {
                        var bytesWrote = (int)(BufferSize - _availableOut);
                        //Byte[] localBuffer = new Byte[bytesWrote];
                        Marshal.Copy(_ptrOutputBuffer, _managedBuffer, 0, bytesWrote);
                        _stream.Write(_managedBuffer, 0, bytesWrote);
                        _availableOut = BufferSize;
                        _ptrNextOutput = _ptrOutputBuffer;
                    }
                }
                if (Brolib.BrotliEncoderIsFinished(_state)) break;
            }
        }
    }
}
