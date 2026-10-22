using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace XiaoJie
{
    /// <summary>
    /// 纯 C# 标准 RIFF AVI 写入器（单视频流，MJPEG 编码，无任何第三方/外部依赖）。
    ///
    /// 文件结构（帧数据流式写入，收尾时回填长度）：
    /// <code>
    /// RIFF &lt;size&gt; AVI
    ///   LIST hdrl
    ///     avih  (AVIMAINHEADER, 56 字节)
    ///     LIST strl
    ///       strh  (AVISTREAMHEADER, 56 字节, fccType=vids, fccHandler=MJPG)
    ///       strf  (BITMAPINFOHEADER, 40 字节, biCompression=MJPG)
    ///   LIST movi
    ///     00dc &lt;len&gt; &lt;JPEG 数据&gt; [填充字节]
    ///     ...
    ///   idx1  (每帧 16 字节索引, AVIIF_KEYFRAME)
    /// </code>
    ///
    /// 设计要点：
    ///   * 视频流与录制时长无关，帧数/最大帧尺寸/索引均在 Close() 时回填；
    ///   * AddFrame 逐帧编码并写盘；只保留当前 JPEG 缓冲以及每帧索引，补帧复用 JPEG；
    ///   * JPEG/MJPEG 对奇数宽高兼容性差，构造时把宽高向下取偶（最多裁掉 1px），AddFrame 自动裁/补到内部尺寸；
    ///   * Close() 幂等且逐项吞掉异常：即使在录制中途失败/磁盘写满，也尽量把已写入的帧补上
    ///     movi / RIFF 长度与 idx1，让“已录部分”仍可被播放器正常打开；
    ///   * 全程 C# 5 语法，便于用 .NET Framework 自带 csc.exe 单独编译做 AVI 结构自测。
    /// 负责队友：recorder
    /// </summary>
    public sealed class AviWriter : IDisposable
    {
        // ---- AVI 常量 ----
        private const uint AVI_HAS_INDEX = 0x00000010;          // AVIF_HASINDEX
        private const uint AVIIF_KEYFRAME = 0x00000010;         // idx1 中每帧都是关键帧（MJPEG）
        private const uint AVI_FRAME_CHUNK_FLAGS = AVIIF_KEYFRAME;
        private static readonly byte[] FCC_RIFF = Encoding.ASCII.GetBytes("RIFF");
        private static readonly byte[] FCC_AVI = Encoding.ASCII.GetBytes("AVI ");
        private static readonly byte[] FCC_LIST = Encoding.ASCII.GetBytes("LIST");
        private static readonly byte[] FCC_HDRL = Encoding.ASCII.GetBytes("hdrl");
        private static readonly byte[] FCC_AVIH = Encoding.ASCII.GetBytes("avih");
        private static readonly byte[] FCC_STRL = Encoding.ASCII.GetBytes("strl");
        private static readonly byte[] FCC_STRH = Encoding.ASCII.GetBytes("strh");
        private static readonly byte[] FCC_STRF = Encoding.ASCII.GetBytes("strf");
        private static readonly byte[] FCC_MOVI = Encoding.ASCII.GetBytes("movi");
        private static readonly byte[] FCC_IDX1 = Encoding.ASCII.GetBytes("idx1");
        private static readonly byte[] FCC_VIDS = Encoding.ASCII.GetBytes("vids");
        private static readonly byte[] FCC_MJPG = Encoding.ASCII.GetBytes("MJPG");
        private static readonly byte[] FCC_00DC = Encoding.ASCII.GetBytes("00dc");

        private static ImageCodecInfo _jpegCodec;

        private readonly string _path;
        private FileStream _stream;
        private BinaryWriter _writer;
        private bool _closed;
        private bool _disposed;

        private readonly int _width;      // 内部（偶数化后）视频宽
        private readonly int _height;     // 内部（偶数化后）视频高
        private readonly int _fps;
        private readonly int _jpegQuality;
        private readonly long _maxFileBytes;
        private bool _hasEncodedFrame;

        private readonly List<long> _chunkOffsets = new List<long>();  // 相对 movi FourCC 的偏移
        private readonly List<int> _chunkSizes = new List<int>();      // 每帧 JPEG 字节数（idx1 用）
        private readonly EncoderParameters _encoderParams;
        private Bitmap _frameBuffer;       // 奇数宽高裁剪/补齐用（必要时才创建）
        private MemoryStream _jpegStream;  // 每帧复用，避免反复分配大内存

        // ---- 头部位置（用于 Close 时回填）----
        private long _riffSizePos;
        private long _avihDataPos;
        private long _strhDataPos;
        private long _moviSizePos;
        private long _moviFourCCPos;
        private long _goodEnd;             // 最后一个完整帧结束位置（异常时截断到此处）
        private long _maxFrameBytes;
        private long _totalFrameBytes;
        private long _bytesWritten;
        private Exception _lastError;

        /// <summary>创建 AVI 文件并写入文件头（帧数等字段在 Close 时回填）。</summary>
        /// <param name="path">输出 .avi 路径</param>
        /// <param name="width">画面宽（内部向下取偶，最多裁 1px）</param>
        /// <param name="height">画面高（内部向下取偶，最多裁 1px）</param>
        /// <param name="fps">帧率（1~60，超出自动收敛）</param>
        /// <param name="jpegQuality">JPEG 质量 1~100（超出自动收敛）</param>
        public AviWriter(string path, int width, int height, int fps, int jpegQuality)
            : this(path, width, height, fps, jpegQuality, uint.MaxValue)
        {
        }

        // 使用较小预算可验证完整文件的边界，无需在测试中分配 4GB。
        internal AviWriter(string path, int width, int height, int fps, int jpegQuality, long maxFileBytes)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("AVI 输出路径为空", "path");
            if (width <= 0 || height <= 0) throw new ArgumentException("AVI 画面尺寸无效: " + width + " × " + height);
            if (maxFileBytes < 256 || maxFileBytes > uint.MaxValue)
                throw new ArgumentOutOfRangeException("maxFileBytes", "AVI 文件预算必须介于 256 字节和 4GB 之间");
            if (fps < 1) fps = 15;
            if (fps > 60) fps = 60;
            if (jpegQuality < 1) jpegQuality = 85;
            if (jpegQuality > 100) jpegQuality = 100;

            _path = path;
            // JPEG/MJPEG 对奇数宽高兼容性差：内部向下取偶（最多裁 1px；极端 1px 时补齐到 2px）
            _width = Math.Max(2, width & ~1);
            _height = Math.Max(2, height & ~1);
            _fps = fps;
            _jpegQuality = jpegQuality;
            _maxFileBytes = maxFileBytes;

            ImageCodecInfo codec = GetJpegCodec();
            if (codec == null) throw new InvalidOperationException("系统未找到 JPEG 编码器");
            _jpegCodec = codec;

            _encoderParams = new EncoderParameters(1);
            _encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)_jpegQuality);
            _jpegStream = new MemoryStream(256 * 1024);

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            bool ok = false;
            try
            {
                _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
                _writer = new BinaryWriter(_stream);
                WriteHeader();
                _goodEnd = _stream.Position;
                ok = true;
            }
            finally
            {
                if (!ok)
                {
                    try { if (_writer != null) _writer.Dispose(); } catch { }
                    try { if (_stream != null) _stream.Dispose(); } catch { }
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                }
            }
        }

        // ==================================================================
        // 公开属性
        // ==================================================================

        /// <summary>已写入文件的当前字节数（录制控制条用于估算文件大小）。</summary>
        public long BytesWritten
        {
            get
            {
                try
                {
                    if (_stream != null && _stream.CanWrite) return _stream.Position;
                }
                catch { }
                return _bytesWritten;
            }
        }

        /// <summary>已写入的帧数。</summary>
        public int FrameCount { get { return _chunkOffsets.Count; } }

        /// <summary>内部视频宽（偶数化后，实际写入 AVI 的宽度）。</summary>
        public int Width { get { return _width; } }

        /// <summary>内部视频高（偶数化后，实际写入 AVI 的高度）。</summary>
        public int Height { get { return _height; } }

        /// <summary>写入 AVI 头的帧率。</summary>
        public int Fps { get { return _fps; } }

        /// <summary>输出文件路径。</summary>
        public string OutputPath { get { return _path; } }

        /// <summary>收尾（Close）时吞掉的最后一个异常，供上层写日志诊断；正常为 null。</summary>
        public Exception LastError { get { return _lastError; } }

        // ==================================================================
        // 写入帧
        // ==================================================================

        /// <summary>把一帧加入视频。frame 会被编码为 JPEG 后立即写盘，调用方可立即释放 frame。</summary>
        public void AddFrame(Bitmap frame)
        {
            if (frame == null) return;
            EnsureOpen();
            _hasEncodedFrame = false;
            EncodeJpeg(frame);
            byte[] buffer = _jpegStream.GetBuffer();
            AddFrameJpeg(buffer, (int)_jpegStream.Length);
            _hasEncodedFrame = true;
        }

        /// <summary>直接复用最近一次成功编码的 JPEG，不再次访问 Bitmap 或编码器。</summary>
        internal bool RepeatLastFrame()
        {
            EnsureOpen();
            return _hasEncodedFrame && AddFrameJpeg(_jpegStream.GetBuffer(), (int)_jpegStream.Length);
        }

        /// <summary>
        /// 直接写入一张已编码好的 JPEG（供录制器在落后于时间线时重复上一帧，保证时长真实）。
        /// 返回 false 表示没有有效数据。
        /// </summary>
        internal bool AddFrameJpeg(byte[] jpeg, int length)
        {
            if (jpeg == null || length <= 0) return false;
            EnsureOpen();
            if (length > jpeg.Length) throw new ArgumentOutOfRangeException("length");
            long chunkPos = _stream.Position;
            long chunkBytes = 8L + length + (length & 1);
            long indexBytes = 8L + 16L * (_chunkOffsets.Count + 1L);
            // RIFF、movi 和 idx1 都使用 32 位字段，必须在写入任何字节前预留完整索引。
            if (chunkPos > _maxFileBytes - chunkBytes - indexBytes)
                throw new AviSizeLimitException();
            WriteBytes(FCC_00DC);
            WriteUInt32((uint)length);
            _stream.Write(jpeg, 0, length);
            if ((length & 1) != 0) _stream.WriteByte(0);   // AVI chunk 必须偶数字节对齐
            _chunkOffsets.Add(chunkPos - _moviFourCCPos);
            _chunkSizes.Add(length);
            _totalFrameBytes += length;
            if (length > _maxFrameBytes) _maxFrameBytes = length;
            _goodEnd = _stream.Position;
            return true;
        }

        private void EncodeJpeg(Bitmap frame)
        {
            Bitmap encode = frame;
            if (frame.Width != _width || frame.Height != _height)
            {
                if (_frameBuffer == null)
                    _frameBuffer = new Bitmap(_width, _height, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(_frameBuffer))
                {
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    if (frame.Width >= _width && frame.Height >= _height)
                    {
                        // 裁剪左上角 _width × _height（奇数宽高裁掉最右/最下 1px）
                        g.DrawImage(frame, new Rectangle(0, 0, _width, _height), 0, 0, _width, _height, GraphicsUnit.Pixel);
                    }
                    else
                    {
                        // 极小画面：黑底补齐
                        using (SolidBrush black = new SolidBrush(Color.Black)) g.FillRectangle(black, 0, 0, _width, _height);
                        g.DrawImageUnscaled(frame, 0, 0);
                    }
                }
                encode = _frameBuffer;
            }
            _jpegStream.SetLength(0);
            _jpegStream.Position = 0;
            encode.Save(_jpegStream, _jpegCodec, _encoderParams);
        }

        // ==================================================================
        // 文件头
        // ==================================================================

        private void WriteHeader()
        {
            // ---- RIFF AVI ----
            WriteBytes(FCC_RIFF);
            _riffSizePos = _stream.Position;
            WriteUInt32(0);                    // 之后回填：文件大小 - 8
            WriteBytes(FCC_AVI);

            // ---- LIST hdrl ----
            long hdrlListPos = _stream.Position;
            WriteBytes(FCC_LIST);
            long hdrlSizePos = _stream.Position;
            WriteUInt32(0);                    // 之后回填
            WriteBytes(FCC_HDRL);

            // ---- avih（AVIMAINHEADER，56 字节）----
            WriteBytes(FCC_AVIH);
            WriteUInt32(56);
            _avihDataPos = _stream.Position;
            WriteUInt32((uint)Math.Max(1, (long)Math.Round(1000000.0 / _fps)));  // dwMicroSecPerFrame
            WriteUInt32(0);                    // dwMaxBytesPerSec（收尾回填）
            WriteUInt32(0);                    // dwPaddingGranularity
            WriteUInt32(AVI_HAS_INDEX);        // dwFlags
            WriteUInt32(0);                    // dwTotalFrames（收尾回填）
            WriteUInt32(0);                    // dwInitialFrames
            WriteUInt32(1);                    // dwStreams
            WriteUInt32(0);                    // dwSuggestedBufferSize（收尾回填）
            WriteUInt32((uint)_width);         // dwWidth
            WriteUInt32((uint)_height);        // dwHeight
            WriteUInt32(0); WriteUInt32(0); WriteUInt32(0); WriteUInt32(0);  // dwReserved[4]

            // ---- LIST strl ----
            long strlListPos = _stream.Position;
            WriteBytes(FCC_LIST);
            long strlSizePos = _stream.Position;
            WriteUInt32(0);                    // 之后回填
            WriteBytes(FCC_STRL);

            // ---- strh（AVISTREAMHEADER，56 字节）----
            WriteBytes(FCC_STRH);
            WriteUInt32(56);
            _strhDataPos = _stream.Position;
            WriteBytes(FCC_VIDS);              // fccType
            WriteBytes(FCC_MJPG);              // fccHandler
            WriteUInt32(0);                    // dwFlags
            WriteUInt16(0);                    // wPriority
            WriteUInt16(0);                    // wLanguage
            WriteUInt32(0);                    // dwInitialFrames
            WriteUInt32(1);                    // dwScale
            WriteUInt32((uint)_fps);           // dwRate
            WriteUInt32(0);                    // dwStart
            WriteUInt32(0);                    // dwLength（总帧数，收尾回填）
            WriteUInt32(0);                    // dwSuggestedBufferSize（收尾回填）
            WriteUInt32(0xFFFFFFFF);           // dwQuality = -1（默认）
            WriteUInt32(0);                    // dwSampleSize
            WriteUInt16(0);                    // rcFrame.left
            WriteUInt16(0);                    // rcFrame.top
            WriteUInt16((ushort)_width);       // rcFrame.right
            WriteUInt16((ushort)_height);      // rcFrame.bottom

            // ---- strf（BITMAPINFOHEADER，40 字节，MJPG）----
            WriteBytes(FCC_STRF);
            WriteUInt32(40);
            WriteUInt32(40);                   // biSize
            WriteUInt32((uint)_width);         // biWidth
            WriteUInt32((uint)_height);        // biHeight（正数 = 自下而上，播放器可自行处理）
            WriteUInt16(1);                    // biPlanes
            WriteUInt16(24);                   // biBitCount
            WriteBytes(FCC_MJPG);              // biCompression
            WriteUInt32((uint)(_width * _height * 3));  // biSizeImage（估算，播放器不使用）
            WriteUInt32(0);                    // biXPelsPerMeter
            WriteUInt32(0);                    // biYPelsPerMeter
            WriteUInt32(0);                    // biClrUsed
            WriteUInt32(0);                    // biClrImportant

            // ---- 回填 hdrl / strl 的 LIST 长度 ----
            PatchListSize(strlSizePos, strlListPos);
            PatchListSize(hdrlSizePos, hdrlListPos);

            // ---- LIST movi ----
            WriteBytes(FCC_LIST);
            _moviSizePos = _stream.Position;
            WriteUInt32(0);                    // 之后回填
            _moviFourCCPos = _stream.Position;
            WriteBytes(FCC_MOVI);
        }

        private void PatchListSize(long sizePos, long listPos)
        {
            // LIST chunk 的 size 字段 = 后续数据字节数 = 结束位置 - (LIST 头起始 + 8)
            long end = _stream.Position;
            WriteUInt32At(sizePos, (uint)(end - (listPos + 8)));
        }

        // ==================================================================
        // 收尾（写索引 + 回填长度）
        // ==================================================================

        /// <summary>
        /// 结束写入：补齐 movi 长度、写 idx1、回填 RIFF/avih/strh 字段。可重复调用；
        /// 逐项容错，保证异常/中断后“已写入部分”仍可播放。
        /// </summary>
        public void Close()
        {
            if (_closed) return;
            _closed = true;
            FileStream stream = _stream;
            BinaryWriter writer = _writer;
            try
            {
                if (writer == null || stream == null) return;

                // 若上一帧只写了一半（磁盘写满等），截断到最后一个完整帧，避免播放器读到坏 chunk
                long end;
                try
                {
                    writer.Flush();
                    end = stream.Position;
                    if (end > _goodEnd && _goodEnd > 0)
                    {
                        stream.SetLength(_goodEnd);
                        stream.Position = _goodEnd;
                        end = _goodEnd;
                        if (_lastError == null) _lastError = new IOException("最后一帧未完整写入，已截断");
                    }
                }
                catch (Exception ex)
                {
                    if (_lastError == null) _lastError = ex;
                    try { stream.Position = _goodEnd > 0 ? _goodEnd : stream.Position; } catch { }
                }

                // 1) 回填 movi LIST 长度（movi 数据从 "movi" FourCC 开始）
                try
                {
                    long moviEnd = stream.Position;
                    WriteUInt32At(_moviSizePos, (uint)Math.Max(0, moviEnd - _moviFourCCPos));
                }
                catch (Exception ex) { if (_lastError == null) _lastError = ex; }

                // 2) 写 idx1 索引（每帧 16 字节：ckid + flags + offset + chunk length）
                try
                {
                    WriteBytes(FCC_IDX1);
                    WriteUInt32((uint)(16 * _chunkOffsets.Count));
                    for (int i = 0; i < _chunkOffsets.Count; i++)
                    {
                        WriteBytes(FCC_00DC);
                        WriteUInt32(AVI_FRAME_CHUNK_FLAGS);
                        WriteUInt32((uint)_chunkOffsets[i]);
                        WriteUInt32((uint)_chunkSizes[i]);
                    }
                }
                catch (Exception ex) { if (_lastError == null) _lastError = ex; }

                // 3) 回填 RIFF 大小
                try
                {
                    writer.Flush();
                    long fileEnd = stream.Position;
                    WriteUInt32At(_riffSizePos, (uint)Math.Max(0, fileEnd - 8));
                    _bytesWritten = fileEnd;
                }
                catch (Exception ex) { if (_lastError == null) _lastError = ex; }

                // 4) 回填 avih / strh 的帧数、建议缓冲、码率
                try
                {
                    uint frames = (uint)_chunkOffsets.Count;
                    uint maxFrame = (uint)_maxFrameBytes;
                    uint maxBytesPerSec = 0;
                    if (frames > 0)
                    {
                        double avg = (double)_totalFrameBytes / frames;
                        maxBytesPerSec = (uint)Math.Min(int.MaxValue, (long)Math.Round(avg * _fps));
                    }
                    WriteUInt32At(_avihDataPos + 4, maxBytesPerSec);        // dwMaxBytesPerSec
                    WriteUInt32At(_avihDataPos + 16, frames);               // dwTotalFrames
                    WriteUInt32At(_avihDataPos + 28, maxFrame);             // dwSuggestedBufferSize
                    WriteUInt32At(_strhDataPos + 32, frames);               // dwLength
                    WriteUInt32At(_strhDataPos + 36, maxFrame);             // dwSuggestedBufferSize
                }
                catch (Exception ex) { if (_lastError == null) _lastError = ex; }

                try { writer.Flush(); } catch { }
                try { stream.Flush(); } catch { }
            }
            finally
            {
                try { if (writer != null) writer.Dispose(); } catch { }
                try { if (stream != null) stream.Dispose(); } catch { }
                try { if (_jpegStream != null) { _jpegStream.Dispose(); _jpegStream = null; } } catch { }
                try { if (_frameBuffer != null) { _frameBuffer.Dispose(); _frameBuffer = null; } } catch { }
                try { if (_encoderParams != null) _encoderParams.Dispose(); } catch { }
            }
        }

        // ==================================================================
        // 底层写法
        // ==================================================================

        private void EnsureOpen()
        {
            if (_closed || _disposed) throw new ObjectDisposedException("AviWriter");
            if (_writer == null) throw new InvalidOperationException("AviWriter 未初始化");
        }

        private void WriteBytes(byte[] bytes)
        {
            _writer.Write(bytes);
        }

        private void WriteUInt32(uint value)
        {
            _writer.Write(value);
        }

        private void WriteUInt16(ushort value)
        {
            _writer.Write(value);
        }

        private void WriteUInt32At(long position, uint value)
        {
            if (_writer == null || _stream == null) return;
            _writer.Flush();
            long current = _stream.Position;
            _stream.Position = position;
            _writer.Write(value);
            _writer.Flush();
            _stream.Position = current;
        }

        private static ImageCodecInfo GetJpegCodec()
        {
            if (_jpegCodec != null) return _jpegCodec;
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
            for (int i = 0; i < codecs.Length; i++)
            {
                if (codecs[i].FormatID == ImageFormat.Jpeg.Guid) return codecs[i];
            }
            return null;
        }

        // ==================================================================
        // 释放
        // ==================================================================

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Close(); } catch { }
        }

        ~AviWriter()
        {
            try { Close(); } catch { }
        }
    }

    internal sealed class AviSizeLimitException : IOException
    {
        internal AviSizeLimitException() : base("已达到 AVI 文件大小上限") { }
    }
}
