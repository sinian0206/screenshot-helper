using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using XiaoJie;

internal static class RecorderRegression
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static string _output;
    private static int _passed;
    private static int _failed;

    [STAThread]
    private static int Main(string[] args)
    {
        _output = args[0];
        Check("收尾失败进入录制错误状态", delegate
        {
            using (Fixture f = new Fixture("close-error", false))
            {
                ((FileStream)Get(f.Writer, "_stream")).Dispose();
                Call(f.Session, "CloseWriter");
                Require(f.Writer.LastError != null, "需要重现收尾异常");
                Require(!string.IsNullOrEmpty((string)Get(f.Session, "_error")), "收尾异常仍被当成成功");
            }
        });
        Check("4GB 边界在写入前拒绝且文件保持完整", TestClassicLimit);
        Check("文件预算包含 idx1 索引和偶数字节填充", TestExactBudget);
        Check("奇数字节 JPEG 的补零也计入文件预算", TestOddJpegBudget);
        Check("JPEG 缓存重复写入不依赖 Bitmap 或编码器", delegate
        {
            string path = PathFor("cached-jpeg");
            using (AviWriter writer = new AviWriter(path, 32, 24, 10, 80))
            {
                using (Bitmap first = Frame()) writer.AddFrame(first);
                ((System.Drawing.Imaging.EncoderParameters)Get(writer, "_encoderParams")).Dispose();
                for (int i = 0; i < 4; ++i)
                    Require((bool)Call(writer, "RepeatLastFrame"), "没有复用最近的 JPEG");
                Equal(5, writer.FrameCount);
            }
            VerifyAvi(path, 5);
            List<byte[]> frames = ReadJpegFrames(path);
            for (int i = 1; i < frames.Count; ++i)
                Equal(Convert.ToBase64String(frames[0]), Convert.ToBase64String(frames[i]));
        });
        Check("卡顿三秒后补齐固定帧率时间线", delegate
        {
            using (Fixture f = new Fixture("timeline", true))
            {
                Call(f.Session, "PumpFrames", 3000L);
                Equal(31, f.Writer.FrameCount);
                Equal(1, f.Captures);
            }
            VerifyAvi(PathFor("timeline"), 31);
        });
        Check("长时间卡顿分批处理且不跳过时间线", delegate
        {
            using (Fixture f = new Fixture("backlog", true))
            {
                for (int i = 0; i < 20 && f.Writer.FrameCount < 301; ++i)
                    Call(f.Session, "PumpFrames", 30000L);
                Equal(301, f.Writer.FrameCount);
                Equal(1, f.Captures);
                Require(f.Writer.BytesWritten < 400000, "回归文件不应大于 400KB");
            }
        });
        Check("抓帧暂时失败时复用上一帧保留时长", delegate
        {
            using (Fixture f = new Fixture("capture-failure", true))
            {
                f.FailCapture = true;
                Call(f.Session, "PumpFrames", 500L);
                Equal(6, f.Writer.FrameCount);
                Equal(1, f.Captures);
                Require(!(bool)Get(f.Session, "_fatal"), "一次抓帧失败不应终止录制");
            }
        });
        Check("连续抓帧失败停止并记录错误", delegate
        {
            using (Fixture f = new Fixture("repeated-capture-failure", true))
            {
                f.FailCapture = true;
                for (int i = 1; i <= 10; ++i) Call(f.Session, "PumpFrames", i * 100L);
                Require((bool)Get(f.Session, "_fatal"), "十次连续失败仍未停止");
                Require(!string.IsNullOrEmpty((string)Get(f.Session, "_error")), "没有记录失败原因");
                Call(f.Session, "PumpFrames", 3000L);
                Equal(10, f.Captures);
            }
            VerifyAvi(PathFor("repeated-capture-failure"), 10);
        });
        Check("单次抓帧耗时也计入成片时间", delegate
        {
            using (Fixture f = new Fixture("slow-capture", true))
            {
                Stopwatch clock = (Stopwatch)Get(f.Session, "_clock");
                f.OnCapture = delegate { Thread.Sleep(230); clock.Stop(); };
                clock.Start();
                Call(f.Session, "PumpFrames", 100L);
                int expected = (int)Math.Floor(clock.Elapsed.TotalMilliseconds / 100.0) + 1;
                Equal(expected, f.Writer.FrameCount);
                Equal(1, f.Captures);
            }
        });
        Check("抓帧消息泵重入不会重复编码", delegate
        {
            using (Fixture f = new Fixture("reentry", true))
            {
                f.OnCapture = delegate { Call(f.Session, "PumpFrames", 100L); };
                Call(f.Session, "PumpFrames", 100L);
                Equal(1, f.Captures);
                Equal(2, f.Writer.FrameCount);
            }
        });
        Check("抓帧期间停止不提交新帧且释放 Bitmap", delegate
        {
            using (Fixture f = new Fixture("stop-during-capture", true))
            {
                f.OnCapture = delegate { Set(f.Session, "_stopRequested", true); };
                Call(f.Session, "PumpFrames", 100L);
                Equal(1, f.Writer.FrameCount);
                bool disposed = false;
                try { f.LastCapturedFrame.GetPixel(0, 0); }
                catch (ArgumentException) { disposed = true; }
                Require(disposed, "停止时未释放尚未提交的 Bitmap");
            }
        });
        Check("停止时补齐最后不足一轮的时间线", delegate
        {
            using (Fixture f = new Fixture("final-timeline", false))
            {
                Stopwatch clock = (Stopwatch)Get(f.Session, "_clock");
                clock.Start();
                Thread.Sleep(190);
                Call(f.Session, "Cleanup");
                int expected = Math.Max(1, (int)Math.Ceiling(clock.Elapsed.TotalMilliseconds / 100.0));
                Equal(expected, f.Writer.FrameCount);
                VerifyAvi(PathFor("final-timeline"), expected);
            }
        });
        Check("停止后的调度不会继续抓帧", delegate
        {
            using (Fixture f = new Fixture("stop", true))
            {
                Set(f.Session, "_stopRequested", true);
                Call(f.Session, "PumpFrames", 3000L);
                Equal(0, f.Captures);
                Equal(1, f.Writer.FrameCount);
            }
        });
        Check("补帧遇到文件上限安全停止且保留可播放文件", delegate { TestLimitInRecorder(3000); });
        Check("新画面遇到文件上限安全停止且保留可播放文件", delegate { TestLimitInRecorder(100); });
        Console.WriteLine("结果: " + _passed + " PASS, " + _failed + " FAIL");
        return _failed == 0 ? 0 : 1;
    }

    private static void TestClassicLimit()
    {
        string path = PathFor("classic-limit");
        using (AviWriter writer = new AviWriter(path, 32, 24, 10, 80))
        {
            using (Bitmap first = Frame()) writer.AddFrame(first);
            FileStream original = (FileStream)Get(writer, "_stream");
            ((BinaryWriter)Get(writer, "_writer")).Flush();
            long end = original.Position;
            original.Dispose();
            using (VirtualPositionFileStream stream = new VirtualPositionFileStream(path))
            {
                stream.Position = end;
                stream.VirtualOffset = uint.MaxValue;
                Set(writer, "_stream", stream);
                Set(writer, "_writer", new BinaryWriter(stream));
                bool refused = false;
                try
                {
                    using (Bitmap frame = Frame()) writer.AddFrame(frame);
                }
                catch (IOException) { refused = true; }
                finally
                {
                    stream.VirtualOffset = 0;
                    stream.Position = end;
                    stream.SetLength(end);
                    Set(writer, "_goodEnd", end);
                    List<long> offsets = (List<long>)Get(writer, "_chunkOffsets");
                    List<int> lengths = (List<int>)Get(writer, "_chunkSizes");
                    if (offsets.Count > 1) offsets.RemoveRange(1, offsets.Count - 1);
                    if (lengths.Count > 1) lengths.RemoveRange(1, lengths.Count - 1);
                }
                writer.Close();
                Require(refused, "超过 32 位 AVI 长度仍允许继续写帧");
            }
        }
        Require(new FileInfo(path).Length < 4096, "测试不应分配大文件");
        VerifyAvi(path, 1);
    }

    private static void TestExactBudget()
    {
        string probe = PathFor("budget-probe");
        long firstEnd;
        using (AviWriter writer = new AviWriter(probe, 32, 24, 10, 80))
        {
            using (Bitmap frame = Frame()) writer.AddFrame(frame);
            firstEnd = writer.BytesWritten;
        }
        long exact = firstEnd + 8 + 16;
        string exactPath = PathFor("exact-budget");
        using (AviWriter writer = LimitedWriter(exactPath, exact))
        {
            using (Bitmap frame = Frame()) writer.AddFrame(frame);
            Equal(1, writer.FrameCount);
            long before = writer.BytesWritten;
            bool refused = false;
            try { using (Bitmap frame = Frame()) writer.AddFrame(frame); }
            catch (IOException) { refused = true; }
            Require(refused, "必须在第二帧写入前停下");
            Equal(before, writer.BytesWritten);
            Equal(1, writer.FrameCount);
        }
        Equal(exact, new FileInfo(exactPath).Length);
        VerifyAvi(exactPath, 1);
        using (AviWriter writer = LimitedWriter(PathFor("too-small-budget"), exact - 1))
        {
            bool refused = false;
            try { using (Bitmap frame = Frame()) writer.AddFrame(frame); }
            catch (IOException) { refused = true; }
            Require(refused, "少一个字节也不能超出完整文件预算");
            Equal(0, writer.FrameCount);
        }
    }

    private static void TestLimitInRecorder(long now)
    {
        string probe = PathFor("recorder-limit-probe");
        long size;
        using (AviWriter writer = new AviWriter(probe, 32, 24, 10, 80))
        {
            using (Bitmap frame = Frame()) writer.AddFrame(frame);
            size = writer.BytesWritten + 8 + 16;
        }
        string path = PathFor("recorder-limit-" + now);
        using (Fixture f = new Fixture("recorder-limit-unused", true))
        using (AviWriter limited = LimitedWriter(path, size))
        {
            using (Bitmap frame = Frame()) limited.AddFrame(frame);
            f.Writer.Close();
            Set(f.Session, "_writer", limited);
            Set(f.Session, "_savePath", path);
            Call(f.Session, "PumpFrames", now);
            Require((bool)Get(f.Session, "_stopRequested"), "达到上限未自动停止");
            Require(!(bool)Get(f.Session, "_fatal"), "正常文件上限不应当成磁盘故障");
            Require(string.IsNullOrEmpty((string)Get(f.Session, "_error")), "已有合法帧应安全保存");
            Equal(1, limited.FrameCount);
            limited.Close();
            VerifyAvi(path, 1);
        }
    }

    private static void TestOddJpegBudget()
    {
        string probe = PathFor("odd-probe");
        using (AviWriter writer = new AviWriter(probe, 32, 24, 10, 80))
        using (Bitmap frame = Frame()) writer.AddFrame(frame);
        byte[] original = ReadJpegFrames(probe)[0];
        byte[] odd = original;
        if ((original.Length & 1) == 0)
        {
            // 插入合法的单字节 JPEG COM 段，使 JPEG 长度为奇数。
            odd = new byte[original.Length + 5];
            odd[0] = 255; odd[1] = 216;
            odd[2] = 255; odd[3] = 254; odd[4] = 0; odd[5] = 3; odd[6] = 0;
            Buffer.BlockCopy(original, 2, odd, 7, original.Length - 2);
        }
        long exact = 224L + 8 + odd.Length + 1 + 8 + 16;
        string path = PathFor("odd-budget");
        using (AviWriter writer = LimitedWriter(path, exact)) writer.AddFrameJpeg(odd, odd.Length);
        Equal(exact, new FileInfo(path).Length);
        VerifyAvi(path, 1);
        using (AviWriter writer = LimitedWriter(PathFor("odd-budget-short"), exact - 1))
        {
            bool refused = false;
            try { writer.AddFrameJpeg(odd, odd.Length); }
            catch (IOException) { refused = true; }
            Require(refused, "奇数帧的补零未计入预算");
            Equal(0, writer.FrameCount);
        }
    }

    private static AviWriter LimitedWriter(string path, long maxBytes)
    {
        ConstructorInfo ctor = typeof(AviWriter).GetConstructor(PrivateInstance, null,
            new[] { typeof(string), typeof(int), typeof(int), typeof(int), typeof(int), typeof(long) }, null);
        Require(ctor != null, "缺少内部 AVI 文件预算接缝");
        return (AviWriter)ctor.Invoke(new object[] { path, 32, 24, 10, 80, maxBytes });
    }

    private static void VerifyAvi(string path, int expectedFrames)
    {
        using (FileStream stream = File.OpenRead(path))
        using (BinaryReader reader = new BinaryReader(stream))
        {
            Equal("RIFF", Encoding.ASCII.GetString(reader.ReadBytes(4)));
            Equal(stream.Length - 8, reader.ReadUInt32());
            stream.Position = 48;
            Equal(expectedFrames, (int)reader.ReadUInt32());
            stream.Position = stream.Length - (8 + 16L * expectedFrames);
            Equal("idx1", Encoding.ASCII.GetString(reader.ReadBytes(4)));
            Equal(expectedFrames * 16, (int)reader.ReadUInt32());
            for (int i = 0; i < expectedFrames; ++i)
            {
                Equal("00dc", Encoding.ASCII.GetString(reader.ReadBytes(4)));
                Equal(16, (int)reader.ReadUInt32());
                uint offset = reader.ReadUInt32();
                uint length = reader.ReadUInt32();
                long returnAt = stream.Position;
                stream.Position = 220 + offset;
                Equal("00dc", Encoding.ASCII.GetString(reader.ReadBytes(4)));
                Equal(length, reader.ReadUInt32());
                byte[] jpeg = reader.ReadBytes((int)length);
                using (MemoryStream data = new MemoryStream(jpeg))
                using (Image image = Image.FromStream(data))
                {
                    Equal(32, image.Width);
                    Equal(24, image.Height);
                }
                stream.Position = returnAt;
            }
        }
    }

    private static List<byte[]> ReadJpegFrames(string path)
    {
        List<byte[]> frames = new List<byte[]>();
        using (FileStream stream = File.OpenRead(path))
        using (BinaryReader reader = new BinaryReader(stream))
        {
            stream.Position = 224;
            while (Encoding.ASCII.GetString(reader.ReadBytes(4)) == "00dc")
            {
                int length = reader.ReadInt32();
                frames.Add(reader.ReadBytes(length));
                if ((length & 1) != 0) reader.ReadByte();
            }
        }
        return frames;
    }

    private static string PathFor(string name) { return Path.Combine(_output, name + ".avi"); }
    private static Bitmap Frame()
    {
        Bitmap frame = new Bitmap(32, 24);
        using (Graphics g = Graphics.FromImage(frame)) g.Clear(Color.FromArgb(32, 96, 160));
        return frame;
    }

    private static object Get(object target, string name)
    {
        return target.GetType().GetField(name, PrivateInstance).GetValue(target);
    }
    private static void Set(object target, string name, object value)
    {
        target.GetType().GetField(name, PrivateInstance).SetValue(target, value);
    }
    private static object Call(object target, string name, params object[] args)
    {
        MethodInfo method = target.GetType().GetMethod(name, PrivateInstance);
        Require(method != null, "缺少内部方法 " + name);
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static void Equal(long expected, long actual)
    {
        Require(expected == actual, "期望 " + expected + "，实际 " + actual);
    }
    private static void Equal(string expected, string actual)
    {
        Require(expected == actual, "期望 " + expected + "，实际 " + actual);
    }
    private static void Check(string name, Action test)
    {
        try { test(); ++_passed; Console.WriteLine("[PASS] " + name); }
        catch (Exception ex)
        {
            ++_failed;
            Console.WriteLine("[FAIL] " + name + ": " + ex.Message);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly RecordingSession Session;
        public readonly AviWriter Writer;
        public int Captures;
        public bool FailCapture;
        public Action OnCapture;
        public Bitmap LastCapturedFrame;

        public Fixture(string name, bool injected)
        {
            AppConfig config = new AppConfig { RecordFps = 10, RecordIncludeCursor = false };
            Rectangle region = new Rectangle(0, 0, 32, 24);
            if (injected)
            {
                ConstructorInfo ctor = typeof(RecordingSession).GetConstructor(PrivateInstance, null,
                    new[] { typeof(Rectangle), typeof(AppConfig), typeof(Func<Rectangle, Bitmap>) }, null);
                Require(ctor != null, "缺少内部画面捕获接缝");
                Func<Rectangle, Bitmap> capture = delegate
                {
                    ++Captures;
                    if (FailCapture) throw new IOException("模拟抓帧失败");
                    LastCapturedFrame = Frame();
                    if (OnCapture != null) OnCapture();
                    return LastCapturedFrame;
                };
                Session = (RecordingSession)ctor.Invoke(new object[] { region, config, capture });
            }
            else Session = new RecordingSession(region, config);
            Writer = new AviWriter(PathFor(name), 32, 24, 10, 80);
            using (Bitmap frame = Frame()) Writer.AddFrame(frame);
            Set(Session, "_writer", Writer);
            Set(Session, "_savePath", PathFor(name));
            Set(Session, "_writtenFrames", 1);
            Set(Session, "_nextFrameDueMs", 100.0);
        }

        public void Dispose() { Session.Dispose(); }
    }

    // 仅伪造逻辑位置，实际文件始终只有几 KB；用于验证 32 位边界的写前算术。
    private sealed class VirtualPositionFileStream : FileStream
    {
        public long VirtualOffset;
        public VirtualPositionFileStream(string path) : base(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read) { }
        public override long Position
        {
            get { return base.Position + VirtualOffset; }
            set { base.Position = value - VirtualOffset; }
        }
    }
}
