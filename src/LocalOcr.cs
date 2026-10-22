using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace XiaoJie
{
    /// <summary>本地引擎检测的一行文字，坐标对应传入图像的像素。</summary>
    public sealed class LocalOcrLine
    {
        public string Text = "";
        public RectangleF Bounds;
        public float Confidence;
    }

    /// <summary>一次离线识别的文本和检测框。</summary>
    public sealed class LocalOcrResult
    {
        public List<LocalOcrLine> Lines = new List<LocalOcrLine>();
        public string EngineName = "本地离线识别";

        public string Text
        {
            get { return string.Join(Environment.NewLine, Lines.Select(line => line.Text)); }
        }
    }

    /// <summary>
    /// 封装随包本地识别运行时。截图经内存中的 PNG/base64 管道传递，不创建临时截图文件。
    /// 每次请求拥有独立进程，取消、超时和释放均会结束该进程；同一实例不排队。
    /// </summary>
    public sealed class LocalOcr : IDisposable
    {
        [DllImport("RecorderNative.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "NativeOcr_IsAvailable")]
        private static extern int NativeOcr_IsAvailable();

        [DllImport("RecorderNative.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "NativeOcr_Recognize")]
        private static extern int NativeOcr_Recognize(IntPtr bgraBytes, int width, int height, int stride, IntPtr jsonBuffer, int maxBufferSize);

        public static string DefaultEnginePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "ocr", "RapidOCR-json.exe");

        private const string DetectionModel = "ch_PP-OCRv4_det_infer.onnx";
        private const string RecognitionModel = "rec_ch_PP-OCRv4_infer.onnx";
        private const string DirectionModel = "ch_ppocr_mobile_v2.0_cls_infer.onnx";
        private const string DictionaryFile = "dict_chinese.txt";
        private const int TimeoutMilliseconds = 60000;
        private const int MaxResponseCharacters = 4 * 1024 * 1024;
        private const long MaxImagePixels = 40000000;
        private readonly string enginePath;
        private readonly object sync = new object();
        private bool disposed;
        private bool busy;
        private Process activeProcess;

        public LocalOcr(string enginePath = null)
        {
            this.enginePath = Path.GetFullPath(enginePath ?? DefaultEnginePath);
        }

        /// <summary>是否已经部署引擎及其完整的简中英文模型或系统原生 OCR 可用。</summary>
        public static bool IsAvailable
        {
            get
            {
                if (NativeOcrAvailable()) return true;
                return HasEngineFiles(DefaultEnginePath);
            }
        }

        private static bool NativeOcrAvailable()
        {
            try { return NativeOcr_IsAvailable() == 1; }
            catch { return false; }
        }

        private static bool HasEngineFiles(string executable)
        {
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) return false;
            string directory = Path.GetDirectoryName(executable);
            return File.Exists(Path.Combine(directory, "models", DetectionModel)) &&
                File.Exists(Path.Combine(directory, "models", RecognitionModel)) &&
                File.Exists(Path.Combine(directory, "models", DirectionModel)) &&
                File.Exists(Path.Combine(directory, "models", DictionaryFile));
        }

        public async Task<LocalOcrResult> RecognizeAsync(Bitmap image, CancellationToken ct)
        {
            if (image == null) throw new ArgumentNullException("image");
            ct.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("LocalOcr");
                if (busy) throw new InvalidOperationException("本地文字识别正在运行，请等待当前识别完成。");
                busy = true;
            }

            Bitmap snapshot = null;
            try
            {
                if (!NativeOcrAvailable() && !HasEngineFiles(enginePath))
                    throw new FileNotFoundException("本地 OCR 组件不完整，且系统原生 OCR 不可用。");
                if (image.Width < 2 || image.Height < 2 ||
                    (long)image.Width * image.Height > MaxImagePixels)
                    throw new ArgumentException("本地文字识别支持 2 像素以上、总像素不超过 4000 万的图片。", "image");

                snapshot = new Bitmap(image);
                return await Task.Run(() => RecognizeCore(snapshot, ct)).ConfigureAwait(false);
            }
            finally
            {
                if (snapshot != null) snapshot.Dispose();
                lock (sync) busy = false;
            }
        }

        private LocalOcrResult RecognizeCore(Bitmap image, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // 优先尝试系统原生高精度 OCR (Windows.Media.Ocr)
            LocalOcrResult nativeResult = TryRecognizeNative(image);
            if (nativeResult != null && nativeResult.Lines.Count > 0)
            {
                return nativeResult;
            }

            if (!HasEngineFiles(enginePath))
            {
                if (nativeResult != null) return nativeResult; // 即使识别为空也返回空结果，避免在没有 RapidOCR 时崩溃
                throw new FileNotFoundException("本地 OCR 组件不完整，请将完整的 ocr 文件夹放到程序旁边。");
            }

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var process = new Process())
            {
                timeout.CancelAfter(TimeoutMilliseconds);
                Task stderrDrain = null;
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = enginePath,
                    WorkingDirectory = Path.GetDirectoryName(enginePath),
                    // 旧引擎默认会把长边缩小到 1024。保留常见截图分辨率可避免小字丢失。
                    Arguments = "--ensureAscii=1 --det=" + DetectionModel +
                        " --rec=" + RecognitionModel + " --cls=" + DirectionModel +
                        " --keys=" + DictionaryFile + " --maxSideLen=4096 --padding=50" +
                        " --numThread=" + Math.Min(4, Math.Max(1, Environment.ProcessorCount)) +
                        // 截图正文默认正向。逐行方向分类会将 G、数字和单个汉字误翻转。
                        " --doAngle=0 --mostAngle=0",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (timeout.Token.Register(() => Kill(process)))
                {
                    try
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        lock (sync)
                        {
                            if (disposed) throw new ObjectDisposedException("LocalOcr");
                            process.Start();
                            activeProcess = process;
                        }
                        // 取消可能恰好发生在启动前；此时注册回调看到的进程尚未启动。
                        timeout.Token.ThrowIfCancellationRequested();

                        // stderr 必须同时排空，否则子进程填满管道时会阻塞整个识别调用。
                        stderrDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
                        WaitForReady(process.StandardOutput);
                        timeout.Token.ThrowIfCancellationRequested();

                        return RecognizeTiles(process, image, timeout.Token);
                    }
                    catch (Exception) when (timeout.IsCancellationRequested)
                    {
                        ct.ThrowIfCancellationRequested();
                        throw new TimeoutException("本地文字识别超过 60 秒，已停止识别。可以缩小选区后重试。");
                    }
                    catch (Exception error) when (error is IOException || error is InvalidOperationException)
                    {
                        lock (sync)
                            if (disposed) throw new ObjectDisposedException("LocalOcr");
                        throw new InvalidOperationException("本地 OCR 引擎未能完成识别，请检查 ocr 组件是否完整。", error);
                    }
                    finally
                    {
                        Kill(process);
                        try { process.WaitForExit(2000); }
                        catch (InvalidOperationException) { }
                        lock (sync)
                            if (ReferenceEquals(activeProcess, process)) activeProcess = null;
                        // 进程退出后管道即关闭。仅观察任务，不把原始引擎输出写入日志。
                        if (stderrDrain != null)
                        {
                            try { stderrDrain.GetAwaiter().GetResult(); }
                            catch (IOException) { }
                            catch (ObjectDisposedException) { }
                        }
                    }
                }
            }
        }

        private static LocalOcrResult RecognizeTiles(Process process, Bitmap image, CancellationToken ct)
        {
            if (image.Width <= 4096 && image.Height <= 4096)
                return RecognizeImage(process, image, ct);

            // 超长截图整体缩小会让小字从检测阶段消失。切片重叠，并按文字中心的归属
            // 区域保留结果，既保留原始像素，也避免切片边界上的文字重复或被截断。
            const int tileSide = 3072;
            List<int> columns = GetTileOffsets(image.Width, tileSide);
            List<int> rows = GetTileOffsets(image.Height, tileSide);
            int tileWidth = Math.Min(image.Width, tileSide);
            int tileHeight = Math.Min(image.Height, tileSide);
            var result = new LocalOcrResult();
            for (int row = 0; row < rows.Count; row++)
            {
                for (int column = 0; column < columns.Count; column++)
                {
                    ct.ThrowIfCancellationRequested();
                    int x = columns[column], y = rows[row];
                    float left = column == 0 ? 0 : (x + columns[column - 1] + tileWidth) / 2f;
                    float right = column == columns.Count - 1 ? image.Width : (x + tileWidth + columns[column + 1]) / 2f;
                    float top = row == 0 ? 0 : (y + rows[row - 1] + tileHeight) / 2f;
                    float bottom = row == rows.Count - 1 ? image.Height : (y + tileHeight + rows[row + 1]) / 2f;
                    using (Bitmap tile = image.Clone(new Rectangle(x, y, tileWidth, tileHeight), PixelFormat.Format24bppRgb))
                    {
                        LocalOcrResult part = RecognizeImage(process, tile, ct);
                        foreach (LocalOcrLine line in part.Lines)
                        {
                            line.Bounds.Offset(x, y);
                            float centerX = line.Bounds.Left + line.Bounds.Width / 2f;
                            float centerY = line.Bounds.Top + line.Bounds.Height / 2f;
                            if (centerX >= left && centerX < right && centerY >= top && centerY < bottom)
                                result.Lines.Add(line);
                        }
                    }
                }
            }
            result.Lines.Sort((first, second) =>
            {
                int byRow = first.Bounds.Top.CompareTo(second.Bounds.Top);
                return byRow != 0 ? byRow : first.Bounds.Left.CompareTo(second.Bounds.Left);
            });
            return result;
        }

        private static List<int> GetTileOffsets(int length, int side)
        {
            var offsets = new List<int> { 0 };
            int actualSide = Math.Min(length, side);
            while (offsets[offsets.Count - 1] + actualSide < length)
            {
                int next = Math.Min(offsets[offsets.Count - 1] + actualSide - 192, length - actualSide);
                offsets.Add(next);
            }
            return offsets;
        }

        private static LocalOcrResult RecognizeImage(Process process, Bitmap image, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string encoded;
            using (var memory = new MemoryStream())
            {
                image.Save(memory, ImageFormat.Png);
                encoded = Convert.ToBase64String(memory.GetBuffer(), 0, (int)memory.Length);
            }
            ct.ThrowIfCancellationRequested();
            process.StandardInput.WriteLine("{\"image_base64\":\"" + encoded + "\"}");
            process.StandardInput.Flush();
            string response = ReadResponse(process.StandardOutput);
            ct.ThrowIfCancellationRequested();
            LocalOcrResult result = ParseResult(response, image.Width, image.Height);
            FilterGraphicRegions(result, image);
            return result;
        }

        /// <summary>结合像素和同行布局排除侧边图标，避免把字符长度作为删除条件。</summary>
        private static void FilterGraphicRegions(LocalOcrResult result, Bitmap image)
        {
            foreach (LocalOcrLine line in result.Lines)
            {
                float characterUnits = GetCharacterUnits(line.Text);
                if (characterUnits > 0 && line.Bounds.Width >= line.Bounds.Height * characterUnits * 1.6f)
                    TrimColorGraphicPrefix(line, new OcrRegionPixels(image, line.Bounds), characterUnits);
            }

            var candidates = new List<LocalOcrLine>();
            foreach (LocalOcrLine line in result.Lines)
            {
                if (line.Confidence >= 0.88f || line.Bounds.Width > line.Bounds.Height * 1.6f)
                    continue;
                LocalOcrLine neighbor = result.Lines.FirstOrDefault(other =>
                    !ReferenceEquals(line, other) && other.Confidence >= 0.9f &&
                    other.Bounds.Width > line.Bounds.Width * 1.8f &&
                    other.Bounds.Left >= line.Bounds.Right &&
                    other.Bounds.Left - line.Bounds.Right < line.Bounds.Height * 1.5f &&
                    Math.Abs(other.Bounds.Top + other.Bounds.Height / 2f -
                        line.Bounds.Top - line.Bounds.Height / 2f) < line.Bounds.Height * 0.45f);
                if (neighbor == null) continue;
                Rectangle ink = new OcrRegionPixels(image, line.Bounds).InkBounds;
                if (ink.Height > 0 && ink.Width >= ink.Height * 0.8f &&
                    ink.Width <= ink.Height * 1.5f)
                    candidates.Add(line);
            }

            // 重复的窄图标列是额外证据；孤立、低置信度的短文字仍交给用户判断。
            result.Lines.RemoveAll(line => candidates.Contains(line) && candidates.Count(other =>
                Math.Abs(other.Bounds.Left - line.Bounds.Left) < line.Bounds.Height * 0.4f &&
                Math.Abs(other.Bounds.Height - line.Bounds.Height) < line.Bounds.Height * 0.5f) >= 2);
        }

        private static float GetCharacterUnits(string text)
        {
            float characterUnits = 0;
            foreach (char character in text)
                if (!char.IsWhiteSpace(character)) characterUnits += character > 255 ? 1f : 0.6f;
            return characterUnits;
        }

        private static void TrimColorGraphicPrefix(LocalOcrLine line, OcrRegionPixels pixels, float characterUnits)
        {
            int colorEnd = -1, colorGroups = 0;
            bool previousColor = false;
            for (int x = 0; x < pixels.Width; x++)
            {
                bool color = pixels.ColorPixelsInColumn(x) >= 2;
                if (color && !previousColor) colorGroups++;
                if (color) colorEnd = x;
                previousColor = color;
            }
            // 多个彩色图形占据过宽框的前端、尾部有完整灰阶正文时才收缩边界。
            if (colorGroups < 2 || colorEnd < pixels.Height ||
                colorEnd > pixels.Width - pixels.Height * characterUnits * 0.7f) return;
            int textStart = colorEnd + 1;
            while (textStart < pixels.Width && pixels.InkPixelsInColumn(textStart) == 0) textStart++;
            if (textStart >= pixels.Width || pixels.Width - textStart < pixels.Height * characterUnits * 0.7f)
                return;
            float left = Math.Max(line.Bounds.Left, pixels.Left + textStart - 2);
            line.Bounds = RectangleF.FromLTRB(left, line.Bounds.Top, line.Bounds.Right, line.Bounds.Bottom);
        }

        /// <summary>只读取检测框内像素，限制长图后处理的内存与扫描范围。</summary>
        private sealed class OcrRegionPixels
        {
            private readonly byte[] data;
            private readonly int stride;
            private readonly Color background;
            public readonly int Left;
            public readonly int Width;
            public readonly int Height;
            public readonly Rectangle InkBounds;

            public OcrRegionPixels(Bitmap image, RectangleF bounds)
            {
                Rectangle rectangle = Rectangle.Intersect(new Rectangle(0, 0, image.Width, image.Height),
                    Rectangle.FromLTRB((int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
                        (int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom)));
                Left = rectangle.Left;
                Width = rectangle.Width;
                Height = rectangle.Height;
                BitmapData locked = image.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    stride = Width * 3;
                    data = new byte[stride * Height];
                    for (int y = 0; y < Height; y++)
                        Marshal.Copy(IntPtr.Add(locked.Scan0, y * locked.Stride), data, y * stride, stride);
                }
                finally { image.UnlockBits(locked); }
                // 检测框通常包含留白；四边采样的中位数避免单个角上的笔画影响背景。
                var red = new List<byte>();
                var green = new List<byte>();
                var blue = new List<byte>();
                for (int x = 0; x < Width; x++)
                {
                    AddBackgroundSample(x, 0, red, green, blue);
                    AddBackgroundSample(x, Height - 1, red, green, blue);
                }
                for (int y = 1; y < Height - 1; y++)
                {
                    AddBackgroundSample(0, y, red, green, blue);
                    AddBackgroundSample(Width - 1, y, red, green, blue);
                }
                red.Sort(); green.Sort(); blue.Sort();
                background = Color.FromArgb(red[red.Count / 2], green[green.Count / 2], blue[blue.Count / 2]);
                int left = Width, top = Height, right = 0, bottom = 0;
                for (int y = 0; y < Height; y++)
                    for (int x = 0; x < Width; x++)
                        if (IsInk(x, y))
                        {
                            left = Math.Min(left, x); top = Math.Min(top, y);
                            right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1);
                        }
                InkBounds = right > left && bottom > top ? Rectangle.FromLTRB(left, top, right, bottom) : Rectangle.Empty;
            }

            private void AddBackgroundSample(int x, int y, List<byte> red, List<byte> green, List<byte> blue)
            {
                int index = y * stride + x * 3;
                blue.Add(data[index]); green.Add(data[index + 1]); red.Add(data[index + 2]);
            }

            private bool IsInk(int x, int y)
            {
                int index = y * stride + x * 3;
                return Math.Max(Math.Abs(data[index] - background.B), Math.Max(
                    Math.Abs(data[index + 1] - background.G), Math.Abs(data[index + 2] - background.R))) >= 35;
            }

            public int InkPixelsInColumn(int x)
            {
                int count = 0;
                for (int y = 0; y < Height; y++) if (IsInk(x, y)) count++;
                return count;
            }

            public int ColorPixelsInColumn(int x)
            {
                int count = 0;
                for (int y = 0; y < Height; y++)
                {
                    int index = y * stride + x * 3;
                    int highest = Math.Max(data[index], Math.Max(data[index + 1], data[index + 2]));
                    int lowest = Math.Min(data[index], Math.Min(data[index + 1], data[index + 2]));
                    if (highest - lowest >= 45 && IsInk(x, y)) count++;
                }
                return count;
            }
        }

        private static void WaitForReady(StreamReader output)
        {
            for (int lineIndex = 0; lineIndex < 64; lineIndex++)
            {
                string line = ReadLimitedLine(output);
                if (line.IndexOf("OCR init completed.", StringComparison.Ordinal) >= 0) return;
            }
            throw new InvalidOperationException("本地 OCR 引擎初始化失败。");
        }

        private static string ReadResponse(StreamReader output)
        {
            for (int lineIndex = 0; lineIndex < 64; lineIndex++)
            {
                string line = ReadLimitedLine(output).Trim();
                if (line.StartsWith("{", StringComparison.Ordinal)) return line;
            }
            throw new InvalidOperationException("本地 OCR 引擎没有返回识别结果。");
        }

        private static string ReadLimitedLine(StreamReader output)
        {
            var line = new StringBuilder();
            while (true)
            {
                int next = output.Read();
                if (next < 0) throw new EndOfStreamException("本地 OCR 引擎提前退出。");
                if (next == '\n') return line.ToString().TrimEnd('\r');
                if (line.Length >= MaxResponseCharacters)
                    throw new InvalidOperationException("本地 OCR 结果超过支持的长度。");
                line.Append((char)next);
            }
        }

        private static LocalOcrResult ParseResult(string json, int width, int height)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = MaxResponseCharacters };
            Dictionary<string, object> root;
            try { root = serializer.DeserializeObject(json) as Dictionary<string, object>; }
            catch (ArgumentException)
            {
                throw new InvalidOperationException("本地 OCR 引擎返回了无法读取的结果。");
            }
            object codeValue;
            if (root == null || !root.TryGetValue("code", out codeValue))
                throw new InvalidOperationException("本地 OCR 引擎返回了不完整的结果。");
            int code = Convert.ToInt32(codeValue, CultureInfo.InvariantCulture);
            var result = new LocalOcrResult();
            if (code == 101) return result;
            if (code != 100)
                throw new InvalidOperationException("本地 OCR 引擎识别失败，状态码 " + code + "。");

            object dataValue;
            var items = root.TryGetValue("data", out dataValue) ? dataValue as object[] : null;
            if (items == null || items.Length > 20000)
                throw new InvalidOperationException("本地 OCR 引擎返回了无效的文字行数据。");
            foreach (object item in items)
            {
                var values = item as Dictionary<string, object>;
                object textValue, boxValue, scoreValue;
                if (values == null || !values.TryGetValue("text", out textValue) ||
                    !values.TryGetValue("box", out boxValue) || !values.TryGetValue("score", out scoreValue)) continue;
                string text = textValue as string;
                var points = boxValue as object[];
                if (string.IsNullOrWhiteSpace(text) || points == null || points.Length != 4) continue;
                float left = width, top = height, right = 0, bottom = 0;
                bool valid = true;
                foreach (object pointValue in points)
                {
                    var point = pointValue as object[];
                    if (point == null || point.Length != 2) { valid = false; break; }
                    float x = Convert.ToSingle(point[0], CultureInfo.InvariantCulture);
                    float y = Convert.ToSingle(point[1], CultureInfo.InvariantCulture);
                    if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y))
                    { valid = false; break; }
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
                RectangleF bounds = RectangleF.Intersect(
                    new RectangleF(0, 0, width, height), RectangleF.FromLTRB(left, top, right, bottom));
                float score = Convert.ToSingle(scoreValue, CultureInfo.InvariantCulture);
                if (!valid || bounds.Width <= 0 || bounds.Height <= 0 || float.IsNaN(score) || float.IsInfinity(score)) continue;
                result.Lines.Add(new LocalOcrLine
                {
                    Text = text,
                    Bounds = bounds,
                    Confidence = Math.Max(0, Math.Min(1, score))
                });
            }
            return result;
        }

        private static LocalOcrResult TryRecognizeNative(Bitmap image)
        {
            try
            {
                if (NativeOcr_IsAvailable() != 1) return null;

                int width = image.Width;
                int height = image.Height;
                using (Bitmap copy = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(copy))
                    {
                        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                        g.DrawImageUnscaled(image, 0, 0);
                    }

                    BitmapData data = copy.LockBits(
                        new Rectangle(0, 0, width, height),
                        ImageLockMode.ReadOnly,
                        PixelFormat.Format32bppArgb);

                    try
                    {
                        int bufferSize = Math.Max(256 * 1024, width * height / 4);
                        IntPtr jsonBuffer = Marshal.AllocHGlobal(bufferSize);
                        try
                        {
                            int res = NativeOcr_Recognize(data.Scan0, width, height, data.Stride, jsonBuffer, bufferSize);
                            if (res > bufferSize)
                            {
                                Marshal.FreeHGlobal(jsonBuffer);
                                bufferSize = res + 4096;
                                jsonBuffer = Marshal.AllocHGlobal(bufferSize);
                                res = NativeOcr_Recognize(data.Scan0, width, height, data.Stride, jsonBuffer, bufferSize);
                            }

                            if (res > 0)
                            {
                                byte[] bytes = new byte[res];
                                Marshal.Copy(jsonBuffer, bytes, 0, res);
                                string json = Encoding.UTF8.GetString(bytes);
                                return ParseNativeJson(json);
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(jsonBuffer);
                        }
                    }
                    finally
                    {
                        copy.UnlockBits(data);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Native OCR exception: " + ex.Message);
            }
            return null;
        }

        private static LocalOcrResult ParseNativeJson(string json)
        {
            var serializer = new JavaScriptSerializer();
            var dict = serializer.Deserialize<Dictionary<string, object>>(json);
            if (dict == null || !dict.ContainsKey("lines")) return null;

            var result = new LocalOcrResult { EngineName = "系统本地识别" };
            var linesList = dict["lines"] as System.Collections.ArrayList;
            if (linesList != null)
            {
                foreach (Dictionary<string, object> lineItem in linesList)
                {
                    string text = lineItem.ContainsKey("text") ? (lineItem["text"] as string ?? "") : "";
                    float x = Convert.ToSingle(lineItem["x"], CultureInfo.InvariantCulture);
                    float y = Convert.ToSingle(lineItem["y"], CultureInfo.InvariantCulture);
                    float w = Convert.ToSingle(lineItem["w"], CultureInfo.InvariantCulture);
                    float h = Convert.ToSingle(lineItem["h"], CultureInfo.InvariantCulture);

                    result.Lines.Add(new LocalOcrLine
                    {
                        Text = text,
                        Bounds = new RectangleF(x, y, w, h),
                        Confidence = 0.99f
                    });
                }
            }
            return result;
        }

        private static void Kill(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                if (activeProcess != null) Kill(activeProcess);
            }
        }
    }
}
