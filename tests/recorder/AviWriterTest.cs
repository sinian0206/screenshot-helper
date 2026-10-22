// AviWriter 离屏自测程序（仅用于验证 AVI 结构，不参与主项目构建）：
//   csc /nologo /target:exe /codepage:65001 /out:build_recorder\avi_writer_test.exe ^
//       /r:System.Drawing.dll tests\recorder\AviWriterTest.cs src\AviWriter.cs
// 用法: avi_writer_test.exe <输出目录>
// 生成:
//   test_640x360_15fps_2s.avi   标准尺寸 / 30 帧
//   test_641x361_15fps_1s.avi   奇数宽高（内部应偶数化为 640x360）/ 15 帧
//   bench_1920x1080_15fps.avi   1080p 编码性能参考
// 输出每步结果，退出码 0 表示全部成功。
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace XiaoJie.Test
{
    internal static class AviWriterTest
    {
        private static int _failures;

        private static int Main(string[] args)
        {
            string outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "avi_writer_test");
            Directory.CreateDirectory(outDir);
            Console.WriteLine("输出目录: " + outDir);

            // 1) 标准 640x360 @15fps 2 秒 = 30 帧
            WriteSynthetic(Path.Combine(outDir, "test_640x360_15fps_2s.avi"), 640, 360, 15, 30);
            // 2) 奇数宽高 641x361：内部应裁成 640x360，15 帧
            WriteSynthetic(Path.Combine(outDir, "test_641x361_15fps_1s.avi"), 641, 361, 15, 15);

            // 3) 异常安全：录 5 帧后直接 Close（不 Dispose），检查仍能写出索引
            ExceptionSafety(Path.Combine(outDir, "test_partial_close.avi"));

            // 4) 1080p 编码耗时参考（15fps 预算 66.7ms/帧）
            Benchmark1080p(Path.Combine(outDir, "bench_1920x1080_15fps.avi"), 30);

            Console.WriteLine(_failures == 0 ? "RESULT: ALL OK" : "RESULT: " + _failures + " FAILURE(S)");
            return _failures == 0 ? 0 : 1;
        }

        private static void WriteSynthetic(string path, int width, int height, int fps, int frames)
        {
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                using (AviWriter writer = new AviWriter(path, width, height, fps, 85))
                {
                    for (int i = 0; i < frames; i++)
                    {
                        using (Bitmap frame = MakeFrame(width, height, i, frames))
                        {
                            writer.AddFrame(frame);
                        }
                    }
                    sw.Stop();
                    long bytes = writer.BytesWritten;
                    Console.WriteLine(string.Format(
                        "OK   {0}: {1} 帧 / {2} 字节 / 内部 {3}x{4} @ {5}fps / 编码 {6:N1}ms (请求 {7}x{8})",
                        Path.GetFileName(path), writer.FrameCount, bytes, writer.Width, writer.Height,
                        writer.Fps, sw.Elapsed.TotalMilliseconds, width, height));
                    if (writer.FrameCount != frames) { Fail("帧数不符: " + writer.FrameCount + " != " + frames); }
                    if (writer.Width != (width & ~1) && width > 2) { Fail("宽度未偶数化: " + writer.Width); }
                    if (writer.Height != (height & ~1) && height > 2) { Fail("高度未偶数化: " + writer.Height); }
                    if (bytes <= 0) { Fail("BytesWritten 为 0"); }
                }
                if (!File.Exists(path) || new FileInfo(path).Length < 1024) { Fail("输出文件不存在或过小: " + path); return; }
                Console.WriteLine("     -> " + path + " (" + new FileInfo(path).Length + " 字节)");
            }
            catch (Exception ex)
            {
                Fail("写 " + Path.GetFileName(path) + " 异常: " + ex);
            }
        }

        private static void ExceptionSafety(string path)
        {
            try
            {
                AviWriter writer = new AviWriter(path, 320, 240, 10, 80);
                for (int i = 0; i < 5; i++)
                {
                    using (Bitmap frame = MakeFrame(320, 240, i, 5)) writer.AddFrame(frame);
                }
                // 模拟“录制中途异常退出”：直接 Close，不 Dispose
                writer.Close();
                writer.Close(); // 幂等
                Console.WriteLine("OK   test_partial_close.avi: 异常路径 Close 完成，文件大小 " +
                    new FileInfo(path).Length + " 字节");
            }
            catch (Exception ex)
            {
                Fail("异常安全测试失败: " + ex);
            }
        }

        private static void Benchmark1080p(string path, int frames)
        {
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                double encodeTotal = 0;
                using (AviWriter writer = new AviWriter(path, 1920, 1080, 15, 85))
                {
                    for (int i = 0; i < frames; i++)
                    {
                        using (Bitmap frame = MakeFrame(1920, 1080, i, frames))
                        {
                            Stopwatch one = Stopwatch.StartNew();
                            writer.AddFrame(frame);
                            one.Stop();
                            encodeTotal += one.Elapsed.TotalMilliseconds;
                        }
                    }
                }
                sw.Stop();
                double avg = encodeTotal / Math.Max(1, frames);
                Console.WriteLine(string.Format(
                    "OK   {0}: 1080p {1} 帧，平均 AddFrame(含JPEG编码+写盘) {2:N1}ms/帧，总 {3:N0}ms；文件 {4:N0} 字节",
                    Path.GetFileName(path), frames, avg, sw.Elapsed.TotalMilliseconds,
                    new FileInfo(path).Length));
                if (avg > 66.0) Console.WriteLine("注意: 1080p 编码速度低于 15fps 预算（会丢帧/时间线补帧）");
            }
            catch (Exception ex)
            {
                Fail("1080p 性能测试失败: " + ex);
            }
        }

        private static Bitmap MakeFrame(int width, int height, int index, int total)
        {
            Bitmap bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // 渐变背景（颜色随帧变化，便于肉眼确认视频在播放）
                int phase = total > 0 ? index * 255 / total : 0;
                using (LinearGradientBrush bg = new LinearGradientBrush(
                    new Rectangle(0, 0, width, height),
                    Color.FromArgb(255, 20 + phase / 3, 60 + phase / 2),
                    Color.FromArgb(255, 40, 120 + phase / 2, 40 + phase / 3), 35f))
                {
                    g.FillRectangle(bg, 0, 0, width, height);
                }
                // 移动方块
                int box = Math.Max(20, Math.Min(width, height) / 6);
                int x = total > 0 ? (width - box) * index / total : 0;
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(230, 255, 255, 255)))
                {
                    g.FillRectangle(brush, x, height / 2 - box / 2, box, box);
                }
                // 网格线（压缩后细节可辨）
                using (Pen pen = new Pen(Color.FromArgb(90, 255, 255, 255), 1f))
                {
                    for (int gx = 0; gx < width; gx += 40) g.DrawLine(pen, gx, 0, gx, height);
                    for (int gy = 0; gy < height; gy += 40) g.DrawLine(pen, 0, gy, width, gy);
                }
            }
            return bmp;
        }

        private static void Fail(string message)
        {
            _failures++;
            Console.WriteLine("FAIL " + message);
        }
    }
}
