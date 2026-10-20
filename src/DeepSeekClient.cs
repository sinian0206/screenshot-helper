using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace XiaoJie
{
    /// <summary>一次 API 调用的 token 用量。</summary>
    public class AiUsage
    {
        public int PromptTokens;
        public int CompletionTokens;
        public int TotalTokens;
        public int ElapsedMs;

        public override string ToString()
        {
            if (TotalTokens <= 0) return "耗时 " + (ElapsedMs / 1000.0).ToString("0.0") + "s";
            return "输入 " + PromptTokens + " / 输出 " + CompletionTokens + " tokens · " + (ElapsedMs / 1000.0).ToString("0.0") + "s";
        }
    }

    /// <summary>面向用户的 API 错误（消息为可直接展示的中文）。</summary>
    public class AiException : Exception
    {
        public int HttpStatus { get; private set; }

        public AiException(string message, int httpStatus = 0, Exception inner = null)
            : base(message, inner)
        {
            HttpStatus = httpStatus;
        }
    }

    /// <summary>AI 识别出的一行文字（坐标均为相对图片宽高的 0~1 小数）。</summary>
    public class ImageTextBlock
    {
        public string Text = "";
        public string Translation = "";
        public float X;
        public float Y;
        public float W;
        public float H;
        public bool Sensitive;
        internal bool HasLocalCoordinates;
        public string Kind = "";   // name/phone/id/email/address/account/code/other

        public bool HasRect
        {
            get
            {
                return !float.IsNaN(X) && !float.IsInfinity(X) &&
                    !float.IsNaN(Y) && !float.IsInfinity(Y) &&
                    !float.IsNaN(W) && !float.IsInfinity(W) &&
                    !float.IsNaN(H) && !float.IsInfinity(H) &&
                    W > (HasLocalCoordinates ? 0f : 0.002f) && H > (HasLocalCoordinates ? 0f : 0.002f);
            }
        }

        public override string ToString()
        {
            return (Kind.Length > 0 ? Kind + ":" : "") + Text;
        }
    }

    /// <summary>人脸/头像框（相对坐标 0~1）。</summary>
    public class FaceBox
    {
        public float X;
        public float Y;
        public float W;
        public float H;
    }

    /// <summary>隐私分析结果：文字行（含敏感标记）+ 人脸/头像框。</summary>
    public class PrivacyAnalysis
    {
        public List<ImageTextBlock> Lines = new List<ImageTextBlock>();
        public List<FaceBox> Faces = new List<FaceBox>();
        internal bool HasLocalCoordinates;
        internal string Warning = "";
    }

    /// <summary>隐私区域（坐标均为相对图片宽高的 0~1 小数）。</summary>
    public class PrivacyRegion
    {
        public float X;
        public float Y;
        public float W;
        public float H;
        public string Type = "";

        public override string ToString()
        {
            return Type + " (" + X.ToString("0.000") + "," + Y.ToString("0.000") + "," + W.ToString("0.000") + "," + H.ToString("0.000") + ")";
        }
    }

    /// <summary>
    /// DeepSeek（OpenAI 兼容）API 客户端。
    /// 所有带 onDelta 的方法都会在后台线程回调增量文本，UI 层需自行 Invoke/BeginInvoke。
    /// 本类不做重试，调用方负责重试/取消。
    /// </summary>
    public sealed partial class DeepSeekClient
    {
        private readonly AppConfig _cfg;
        private readonly object _sync = new object();
        private HttpWebRequest _current;

        public DeepSeekClient(AppConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException("cfg");
            _cfg = cfg;
        }

        public AppConfig Config { get { return _cfg; } }

        public bool HasApiKey
        {
            get { return _cfg != null && !string.IsNullOrWhiteSpace(_cfg.ApiKey); }
        }

        /// <summary>真正的 chat/completions 地址。</summary>
        public string Endpoint
        {
            get
            {
                string url = (_cfg.BaseUrl ?? "").Trim();
                if (url.Length == 0) url = "https://api.deepseek.com";
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    url = "https://" + url;
                }
                url = url.TrimEnd('/');
                if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return url;
                if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return url + "/chat/completions";
                Uri baseUri;
                if (Uri.TryCreate(url, UriKind.Absolute, out baseUri) && baseUri.AbsolutePath == "/")
                {
                    // 官方根地址可直接使用；第三方根地址保留原有 OpenAI 兼容路径。
                    if (AppConfig.IsOfficialDeepSeekUrl(url)) return url + "/chat/completions";
                    return url + "/v1/chat/completions";
                }
                return url + "/chat/completions";
            }
        }

        private bool IsDeepSeekEndpoint
        {
            get { return AppConfig.IsOfficialDeepSeekUrl(Endpoint); }
        }

        /// <summary>中止当前进行中的请求（用于取消/窗口关闭）。</summary>
        public void CancelAll()
        {
            lock (_sync)
            {
                if (_current != null)
                {
                    try { _current.Abort(); } catch { }
                    _current = null;
                }
            }
        }

        // ===================== 对外功能 =====================

        /// <summary>测试连接：发一个极小的文本请求，返回耗时。</summary>
        public Task<AiUsage> TestConnectionAsync(CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                Stopwatch sw = Stopwatch.StartNew();
                string system = "你是连通性测试助手。";
                string body = BuildRequestBody(_cfg.TextModel, system, "请只回复四个字：连接成功", null, false, false, 32);
                string content = await SendAsync(body, false, null, null, "original", ct).ConfigureAwait(false);
                sw.Stop();
                AiUsage usage = new AiUsage();
                usage.ElapsedMs = (int)sw.ElapsedMilliseconds;
                if (string.IsNullOrWhiteSpace(content)) throw new AiException("连接成功，但模型返回为空。");
                return usage;
            }, ct);
        }

        /// <summary>在本机识别文字，不发送图片，不需要 API Key。</summary>
        public async Task<string> OcrAsync(Bitmap image, Action<string> onDelta, Action<AiUsage> onUsage, CancellationToken ct)
        {
            LocalOcrResult res = await OcrResultAsync(image, onDelta, onUsage, ct).ConfigureAwait(false);
            return res != null ? res.Text : "";
        }

        public async Task<LocalOcrResult> OcrResultAsync(Bitmap image, Action<string> onDelta, Action<AiUsage> onUsage, CancellationToken ct)
        {
            Stopwatch sw = Stopwatch.StartNew();
            using (var engine = new LocalOcr())
            {
                LocalOcrResult result = await engine.RecognizeAsync(image, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                sw.Stop();
                if (onDelta != null) onDelta(result.Text);
                if (onUsage != null) onUsage(new AiUsage { ElapsedMs = (int)sw.ElapsedMilliseconds });
                return result;
            }
        }

        /// <summary>提取并翻译：先 OCR，再翻译为 targetLang。返回 [原文, 译文]。</summary>
        public async Task<string[]> TranslateImageAsync(Bitmap image, string targetLang,
            Action<string> onOriginalDelta, Action<string> onTranslationDelta, Action<AiUsage> onUsage, CancellationToken ct)
        {
            string original = await OcrAsync(image, onOriginalDelta, onUsage, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(original))
            {
                return new string[] { original ?? "", "(未识别到可翻译的文字)" };
            }
            string translation = await TranslateTextAsync(original, targetLang, onTranslationDelta, onUsage, ct).ConfigureAwait(false);
            return new string[] { original, translation };
        }

        /// <summary>文本翻译（流式）。</summary>
        public Task<string> TranslateTextAsync(string text, string targetLang, Action<string> onDelta, Action<AiUsage> onUsage, CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                string target = string.IsNullOrWhiteSpace(targetLang) ? "中文" : targetLang.Trim();
                string system = TranslateSystemPrompt.Replace("{target}", target);
                if (!string.IsNullOrEmpty(_cfg.TranslateExtraPrompt))
                    system += "\n补充要求：" + _cfg.TranslateExtraPrompt;
                string body = BuildRequestBody(_cfg.TextModel, system, text ?? "", null, true, false, 4096);
                string translation = await SendAsync(body, true, onDelta, onUsage, "original", ct).ConfigureAwait(false);
                return (translation ?? "").Trim();
            }, ct);
        }

        /// <summary>一键打码：让视觉模型找出隐私信息区域。</summary>
        public Task<List<PrivacyRegion>> DetectPrivacyAsync(Bitmap image, CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                string userText = "请检测并返回这张图片中所有隐私信息区域。";
                string dataUrl = ImageUtil.ToDataUrl(image);
                string body = BuildRequestBody(_cfg.VisionModel, PrivacySystemPrompt, userText, dataUrl, false, true, 2048);
                string content = await SendAsync(body, false, null, null, "high", ct).ConfigureAwait(false);
                return ParsePrivacyRegions(content);
            }, ct);
        }


        private async Task<string> AnalyzeVisionJsonAsync(Bitmap image, string systemPrompt, string userText, int maxTokens, CancellationToken ct)
        {
            string dataUrl = ImageUtil.ToDataUrl(image);
            string body = BuildRequestBody(_cfg.VisionModel, systemPrompt, userText, dataUrl, false, true, maxTokens);
            string content = await SendAsync(body, false, null, null, "original", ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content)) throw new AiException("AI 没有返回内容，请重试。");
            return content;
        }

        // ===================== Prompt =====================

        public const string OcrSystemPrompt =
            "你是一个精准的 OCR 文字提取引擎。请提取用户图片中的所有可见文字，严格保持原文的阅读顺序、换行与段落结构。" +
            "不要翻译、不要总结、不要解释，不要添加标题、序号、Markdown 代码块标记或任何额外内容。" +
            "无法辨认的字用 □ 代替。如果图片中没有文字，只输出：(未识别到文字)";

        public const string TranslateSystemPrompt =
            "你是一名专业翻译。请把用户提供的文本翻译成「{target}」。" +
            "只输出译文本身：不要解释、不要点评、不要重复原文、不要添加 Markdown 代码块标记。" +
            "保留原文的换行与段落结构；专有名词、代码、数字、URL、@用户名 保持原样。" +
            "如果原文已经是目标语言，则输出润色后的同语言文本。";

        public const string PrivacySystemPrompt =
            "你是一个隐私信息检测器。请找出图片中所有敏感/隐私信息，包括：人脸与头像、人名、手机号、电话、身份证号、银行卡号、" +
            "邮箱、地址、微信号/QQ号、验证码、二维码、车牌号等。\n" +
            "只输出一个 JSON 对象，格式严格为：{\"regions\":[{\"x\":0.0,\"y\":0.0,\"w\":0.0,\"h\":0.0,\"type\":\"name\"}]}；" +
            "x/y 为区域左上角相对图片宽高的 0~1 小数，w/h 为区域宽高相对图片宽高的 0~1 小数，矩形必须完整覆盖目标；" +
            "type 用英文小写标签（face/name/phone/id/card/email/address/account/code/qr/car/other）。" +
            "没有敏感信息时输出 {\"regions\":[]}。只输出 JSON，不要输出任何解释或 Markdown 标记。";

        // ===================== HTTP / SSE =====================

        private string BuildRequestBody(string model, string systemPrompt, string userText, string imageDataUrl,
            bool stream, bool jsonMode, int maxTokens)
        {
            if (!string.IsNullOrEmpty(imageDataUrl) && string.IsNullOrWhiteSpace(model))
                throw new AiException("请在设置中填写支持图片输入的视觉模型，或关闭“检测头像与二维码”。");
            if (string.IsNullOrWhiteSpace(model)) model = "deepseek-flash";
            if (string.IsNullOrWhiteSpace(userText)) userText = "";
            StringBuilder sb = new StringBuilder(1024);
            sb.Append('{');
            sb.Append("\"model\":").Append(J(model)).Append(',');
            if (IsDeepSeekEndpoint)
            {
                // 非思考模式：响应更快，适合 OCR / 翻译等确定性任务
                sb.Append("\"thinking\":{\"type\":\"disabled\"},");
            }
            sb.Append("\"stream\":").Append(stream ? "true" : "false").Append(',');
            if (stream) sb.Append("\"stream_options\":{\"include_usage\":true},");
            sb.Append("\"max_tokens\":").Append(Math.Max(16, maxTokens)).Append(',');
            if (jsonMode) sb.Append("\"response_format\":{\"type\":\"json_object\"},");
            else if (!stream) sb.Append("\"temperature\":0.2,");
            sb.Append("\"messages\":[");
            if (!string.IsNullOrEmpty(systemPrompt))
            {
                sb.Append("{\"role\":\"system\",\"content\":").Append(J(systemPrompt)).Append("},");
            }
            sb.Append("{\"role\":\"user\",\"content\":");
            if (string.IsNullOrEmpty(imageDataUrl))
            {
                sb.Append(J(userText));
            }
            else
            {
                sb.Append("[{\"type\":\"text\",\"text\":").Append(J(userText)).Append("},");
                sb.Append("{\"type\":\"image_url\",\"image_url\":{\"url\":").Append(J(imageDataUrl))
                  .Append(",\"detail\":\"original\"}}]");
            }
            sb.Append("}]}");
            return sb.ToString();
        }

        private static string J(string s)
        {
            if (s == null) s = "";
            StringBuilder sb = new StringBuilder(s.Length + 16);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private async Task<string> SendAsync(string body, bool stream, Action<string> onDelta,
            Action<AiUsage> onUsage, string imageDetail, CancellationToken ct)
        {
            if (!HasApiKey)
                throw new AiException("尚未配置 API Key。请点击托盘图标 →「设置…」填写 API Key 后再试。", 401);

            ct.ThrowIfCancellationRequested();
            string endpoint = Endpoint;
            HttpWebRequest req;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(endpoint);
            }
            catch (Exception ex)
            {
                throw new AiException("API 地址无效：" + endpoint + "（" + ex.Message + "）", 0, ex);
            }

            req.Method = "POST";
            req.ContentType = "application/json; charset=utf-8";
            req.Accept = stream ? "text/event-stream" : "application/json";
            req.UserAgent = "ScreenshotHelper/" + AppInfo.Version;
            try { req.Headers["Accept-Language"] = "zh-CN,zh;q=0.9,en;q=0.6"; } catch { }
            req.KeepAlive = true;
            req.Timeout = Math.Max(10, _cfg.TimeoutSec) * 1000;
            req.ReadWriteTimeout = Math.Max(10, _cfg.TimeoutSec) * 1000;
            req.Headers["Authorization"] = "Bearer " + (_cfg.ApiKey ?? "").Trim();

            CancellationToken userToken = ct;
            var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, Math.Min(600, _cfg.TimeoutSec))));
            ct = deadline.Token;

            lock (_sync) { _current = req; }
            CancellationTokenRegistration reg = ct.Register(delegate
            {
                try { req.Abort(); } catch { }
            });

            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                byte[] payload = Encoding.UTF8.GetBytes(body);
                req.ContentLength = payload.Length;
                using (Stream reqStream = await req.GetRequestStreamAsync().ConfigureAwait(false))
                {
                    await reqStream.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
                }

                HttpWebResponse resp = null;
                try
                {
                    resp = (HttpWebResponse)await req.GetResponseAsync().ConfigureAwait(false);
                }
                catch (WebException we)
                {
                    throw await BuildWebExceptionAsync(we, ct).ConfigureAwait(false);
                }

                using (resp)
                using (Stream respStream = resp.GetResponseStream())
                {
                    if (respStream == null) throw new AiException("服务器返回空响应。", (int)resp.StatusCode);

                    if (stream)
                    {
                        using (StreamReader reader = new StreamReader(respStream, Encoding.UTF8))
                        {
                            StringBuilder all = new StringBuilder();
                            JavaScriptSerializer ser = new JavaScriptSerializer();
                            ser.MaxJsonLength = 16 * 1024 * 1024;
                            while (true)
                            {
                                ct.ThrowIfCancellationRequested();
                                string line = await reader.ReadLineAsync().ConfigureAwait(false);
                                if (line == null) break;
                                if (line.Length == 0) continue;
                                if (line[0] == ':') continue; // SSE keep-alive 注释
                                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                                string data = line.Substring(5).Trim();
                                if (data.Length == 0) continue;
                                if (data == "[DONE]") break;
                                Dictionary<string, object> chunk;
                                try { chunk = ser.Deserialize<Dictionary<string, object>>(data); }
                                catch { continue; }
                                if (chunk == null) continue;

                                AiUsage usage = ParseUsage(chunk);
                                if (usage != null)
                                {
                                    usage.ElapsedMs = (int)stopwatch.ElapsedMilliseconds;
                                    if (onUsage != null) onUsage(usage);
                                }

                                object choicesObj;
                                if (!chunk.TryGetValue("choices", out choicesObj)) continue;
                                object[] choices = AsArray(choicesObj);
                                if (choices == null || choices.Length == 0) continue;
                                Dictionary<string, object> choice = choices[0] as Dictionary<string, object>;
                                if (choice == null) continue;
                                object deltaObj;
                                if (!choice.TryGetValue("delta", out deltaObj)) continue;
                                Dictionary<string, object> delta = deltaObj as Dictionary<string, object>;
                                if (delta == null) continue;
                                object contentObj;
                                if (delta.TryGetValue("content", out contentObj) && contentObj != null)
                                {
                                    string piece = contentObj as string;
                                    if (!string.IsNullOrEmpty(piece))
                                    {
                                        all.Append(piece);
                                        if (onDelta != null) onDelta(piece);
                                    }
                                }
                            }
                            stopwatch.Stop();
                            return all.ToString();
                        }
                    }
                    else
                    {
                        string respText;
                        using (StreamReader reader = new StreamReader(respStream, Encoding.UTF8))
                            respText = await reader.ReadToEndAsync().ConfigureAwait(false);
                        JavaScriptSerializer ser = new JavaScriptSerializer();
                        ser.MaxJsonLength = 16 * 1024 * 1024;
                        Dictionary<string, object> root;
                        try { root = ser.Deserialize<Dictionary<string, object>>(respText); }
                        catch (Exception ex) { throw new AiException("服务器响应无法解析：" + Truncate(respText, 300), (int)resp.StatusCode, ex); }
                        if (root == null) throw new AiException("服务器返回空响应。", (int)resp.StatusCode);
                        AiUsage usage = ParseUsage(root);
                        if (usage != null)
                        {
                            usage.ElapsedMs = (int)stopwatch.ElapsedMilliseconds;
                            if (onUsage != null) onUsage(usage);
                        }
                        string content = ExtractMessageContent(root);
                        stopwatch.Stop();
                        return content ?? "";
                    }
                }
            }
            catch (Exception) when (deadline.IsCancellationRequested)
            {
                userToken.ThrowIfCancellationRequested();
                throw new AiException("请求超时，已停止等待，请检查网络或调整超时设置。");
            }
            catch (OperationCanceledException) { throw; }
            catch (AiException) { throw; }
            catch (WebException we)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                throw new AiException("网络请求失败：" + FriendlyWebException(we), 0, we);
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                throw new AiException("请求失败：" + ex.Message, 0, ex);
            }
            finally
            {
                reg.Dispose();
                deadline.Dispose();
                lock (_sync) { if (ReferenceEquals(_current, req)) _current = null; }
            }
        }

        private static AiUsage ParseUsage(Dictionary<string, object> root)
        {
            object usageObj;
            if (!root.TryGetValue("usage", out usageObj)) return null;
            Dictionary<string, object> usage = usageObj as Dictionary<string, object>;
            if (usage == null) return null;
            AiUsage result = new AiUsage();
            result.PromptTokens = GetInt(usage, "prompt_tokens");
            result.CompletionTokens = GetInt(usage, "completion_tokens");
            result.TotalTokens = GetInt(usage, "total_tokens");
            if (result.TotalTokens == 0) result.TotalTokens = result.PromptTokens + result.CompletionTokens;
            return result;
        }

        private static object[] AsArray(object value)
        {
            if (value == null) return null;
            object[] arr = value as object[];
            if (arr != null) return arr;
            System.Collections.ArrayList list = value as System.Collections.ArrayList;
            if (list != null) return list.ToArray();
            System.Collections.IEnumerable enumerable = value as System.Collections.IEnumerable;
            if (enumerable != null)
            {
                System.Collections.Generic.List<object> items = new System.Collections.Generic.List<object>();
                foreach (object item in enumerable) items.Add(item);
                return items.ToArray();
            }
            return null;
        }

        private static int GetInt(Dictionary<string, object> dict, string key)
        {
            object v;
            if (!dict.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToInt32(v); } catch { return 0; }
        }

        private static float GetFloat(Dictionary<string, object> dict, string key)
        {
            object v;
            if (!dict.TryGetValue(key, out v) || v == null) return 0f;
            try { return Convert.ToSingle(v); } catch { return 0f; }
        }

        private static string ExtractMessageContent(Dictionary<string, object> root)
        {
            object choicesObj;
            if (!root.TryGetValue("choices", out choicesObj)) return null;
            object[] choices = AsArray(choicesObj);
            if (choices == null || choices.Length == 0) return null;
            Dictionary<string, object> choice = choices[0] as Dictionary<string, object>;
            if (choice == null) return null;
            object messageObj;
            if (!choice.TryGetValue("message", out messageObj)) return null;
            Dictionary<string, object> message = messageObj as Dictionary<string, object>;
            if (message == null) return null;
            object content;
            if (!message.TryGetValue("content", out content)) return null;
            return content as string;
        }

        private async Task<AiException> BuildWebExceptionAsync(WebException we, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return new AiException("请求已取消。", 0, we);
            HttpWebResponse resp = we.Response as HttpWebResponse;
            if (resp != null)
            {
                int status = (int)resp.StatusCode;
                string body = "";
                try
                {
                    using (resp)
                    using (Stream s = resp.GetResponseStream())
                    {
                        if (s != null)
                        {
                            using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                                body = await r.ReadToEndAsync().ConfigureAwait(false);
                        }
                    }
                }
                catch { }
                string detail = ExtractErrorDetail(body);
                return new AiException(FriendlyStatus(status, detail), status, we);
            }
            switch (we.Status)
            {
                case WebExceptionStatus.Timeout:
                    return new AiException("请求超时（" + Math.Max(10, _cfg.TimeoutSec) + " 秒）。请检查网络，或在设置中增大超时时间。", 0, we);
                case WebExceptionStatus.NameResolutionFailure:
                case WebExceptionStatus.ProxyNameResolutionFailure:
                    return new AiException("无法解析 API 服务器地址，请检查网络或代理设置。", 0, we);
                case WebExceptionStatus.ConnectFailure:
                    return new AiException("无法连接到 API 服务器，请检查网络或代理设置。", 0, we);
                case WebExceptionStatus.SecureChannelFailure:
                case WebExceptionStatus.TrustFailure:
                    return new AiException("HTTPS/TLS 连接失败，请检查系统证书或代理。", 0, we);
                default:
                    return new AiException("网络错误：" + we.Message, 0, we);
            }
        }

        private static string ExtractErrorDetail(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> root = ser.Deserialize<Dictionary<string, object>>(body);
                if (root != null)
                {
                    object errObj;
                    if (root.TryGetValue("error", out errObj))
                    {
                        Dictionary<string, object> err = errObj as Dictionary<string, object>;
                        if (err != null)
                        {
                            object msgObj;
                            if (err.TryGetValue("message", out msgObj) && msgObj != null)
                                return Truncate(msgObj.ToString(), 220);
                        }
                        else if (errObj != null)
                        {
                            return Truncate(errObj.ToString(), 220);
                        }
                    }
                }
            }
            catch { }
            return Truncate(body.Replace("\r", " ").Replace("\n", " ").Trim(), 220);
        }

        private static string FriendlyStatus(int status, string detail)
        {
            string suffix = string.IsNullOrEmpty(detail) ? "" : "（" + detail + "）";
            switch (status)
            {
                case 400: return "请求格式错误（400）" + suffix;
                case 401: return "API Key 无效或未授权（401），请到「设置」中检查 API Key。" + suffix;
                case 402: return "AI 账户余额不足（402），请先充值后再使用。" + suffix;
                case 403: return "AI 网关拒绝了请求（403，可能被 Cloudflare/风控拦截）。请检查 Base URL、网络或稍后重试。" + suffix;
                case 422: return "请求参数错误（422）" + suffix;
                case 429: return "请求太频繁（429），请稍后再试。" + suffix;
                case 500: return "接口服务故障（500），请稍后重试。" + suffix;
                case 503: return "接口服务繁忙（503），请稍后重试。" + suffix;
                default: return "请求失败（HTTP " + status + "）" + suffix;
            }
        }

        private static string FriendlyWebException(WebException we)
        {
            switch (we.Status)
            {
                case WebExceptionStatus.Timeout: return "请求超时，请检查网络";
                case WebExceptionStatus.NameResolutionFailure: return "无法解析服务器地址";
                case WebExceptionStatus.ConnectFailure: return "无法连接服务器";
                case WebExceptionStatus.SecureChannelFailure:
                case WebExceptionStatus.TrustFailure: return "HTTPS/TLS 连接失败";
                default: return we.Message;
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static Dictionary<string, object> ParseJsonRoot(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) throw new AiException("AI 返回内容为空，请重试。");
            string json = StripCodeFence(content);
            int first = json.IndexOf('{');
            int last = json.LastIndexOf('}');
            if (first < 0 || last <= first) throw new AiException("AI 返回的内容不是 JSON，请重试。");
            json = json.Substring(first, last - first + 1);
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = 8 * 1024 * 1024;
                Dictionary<string, object> root = ser.Deserialize<Dictionary<string, object>>(json);
                if (root == null) throw new AiException("AI 返回的 JSON 为空，请重试。");
                return root;
            }
            catch (AiException) { throw; }
            catch (Exception ex)
            {
                throw new AiException("AI 返回的 JSON 解析失败，请重试。", 0, ex);
            }
        }

        private static List<ImageTextBlock> ParseTextBlocks(string content, int imageW, int imageH)
        {
            return ParseTextBlocks(ParseJsonRoot(content), imageW, imageH);
        }

        private static List<ImageTextBlock> ParseTextBlocks(Dictionary<string, object> root, int imageW, int imageH)
        {
            List<ImageTextBlock> result = new List<ImageTextBlock>();
            object listObj = null;
            if (!root.TryGetValue("lines", out listObj)) root.TryGetValue("blocks", out listObj);
            object[] arr = AsArray(listObj);
            if (arr == null) return result;
            foreach (object item in arr)
            {
                Dictionary<string, object> d = item as Dictionary<string, object>;
                if (d == null) continue;
                ImageTextBlock block = new ImageTextBlock();
                block.Text = GetString(d, "text");
                block.Translation = GetString(d, "translation");
                block.Kind = GetString(d, "type");
                if (string.IsNullOrEmpty(block.Kind)) block.Kind = GetString(d, "kind");
                block.Sensitive = GetBool(d, "sensitive") || GetBool(d, "is_sensitive") || GetBool(d, "private");
                block.X = GetFloat(d, "x");
                block.Y = GetFloat(d, "y");
                block.W = GetFloat(d, "w");
                block.H = GetFloat(d, "h");
                if (block.W <= 0f || block.H <= 0f)
                {
                    float x2 = GetFloat(d, "x2");
                    float y2 = GetFloat(d, "y2");
                    if (x2 > block.X && y2 > block.Y) { block.W = x2 - block.X; block.H = y2 - block.Y; }
                }
                NormalizeRect(ref block.X, ref block.Y, ref block.W, ref block.H, imageW, imageH);
                if (!block.HasRect) continue;
                if (string.IsNullOrEmpty(block.Text) && string.IsNullOrEmpty(block.Translation)) continue;
                result.Add(block);
            }
            return result;
        }

        private static List<FaceBox> ParseFaces(Dictionary<string, object> root, int imageW, int imageH)
        {
            var result = new List<FaceBox>();
            bool hasRegionArray = false;
            foreach (string key in new[] { "faces", "avatars", "codes", "qr" })
            {
                object value;
                if (!root.TryGetValue(key, out value)) continue;
                object[] regions = AsArray(value);
                if (regions == null) throw new AiException("视觉检测区域格式无效。");
                hasRegionArray = true;
                foreach (object item in regions)
                {
                    var region = item as Dictionary<string, object>;
                    if (region == null) throw new AiException("视觉检测区域格式无效。");
                    float x = GetRequiredCoordinate(region, "x"), y = GetRequiredCoordinate(region, "y");
                    float w = GetRequiredCoordinate(region, "w"), h = GetRequiredCoordinate(region, "h");
                    // 视觉补充只接受约定的归一化坐标，不猜百分比或像素比例。
                    if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(w) || float.IsNaN(h) ||
                        float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(w) || float.IsInfinity(h) ||
                        x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > 1.001f || y + h > 1.001f)
                        throw new AiException("视觉检测坐标超出图片范围。");
                    result.Add(new FaceBox { X = x, Y = y, W = Math.Min(w, 1f - x), H = Math.Min(h, 1f - y) });
                }
            }
            if (!hasRegionArray) throw new AiException("视觉检测响应缺少区域数组。");
            return result;
        }

        private static float GetRequiredCoordinate(Dictionary<string, object> region, string key)
        {
            object value;
            if (!region.TryGetValue(key, out value) ||
                !(value is int || value is long || value is double || value is float || value is decimal))
                throw new AiException("视觉检测坐标字段缺失或类型无效。");
            try { return Convert.ToSingle(value); }
            catch (OverflowException) { throw new AiException("视觉检测坐标超出支持范围。"); }
        }

        /// <summary>
        /// 模型有时返回 0~1、0~100、0~1000 或像素坐标。这里尝试多种比例，选几何上最合理的一种。
        /// </summary>
        private static void NormalizeRect(ref float x, ref float y, ref float w, ref float h, int imageW, int imageH)
        {
            if (imageW <= 0) imageW = 1;
            if (imageH <= 0) imageH = 1;
            float rawX = x, rawY = y, rawW = w, rawH = h;
            float bestScore = float.MinValue;
            float bx = 0, by = 0, bw = 0, bh = 0;
            // 候选缩放：0~1、0~100、0~1000、像素
            float[] sx = new float[] { 1f, 100f, 1000f, imageW };
            float[] sy = new float[] { 1f, 100f, 1000f, imageH };
            for (int i = 0; i < sx.Length; i++)
            {
                float cx = rawX / sx[i];
                float cy = rawY / sy[i];
                float cw = rawW / sx[i];
                float ch = rawH / sy[i];
                if (cw <= 0.0025f || ch <= 0.0025f || cw > 0.995f || ch > 0.995f) continue;
                if (cx < -0.02f || cy < -0.02f || cx + cw > 1.04f || cy + ch > 1.04f) continue;
                cx = Clamp01(cx); cy = Clamp01(cy); cw = Clamp01(cw); ch = Clamp01(ch);
                if (cx + cw > 1f) cw = 1f - cx;
                if (cy + ch > 1f) ch = 1f - cy;
                // 偏好：接近 0~1 的候选（模型被明确要求 0~1），其余缩放需具有较整的数值特征
                float score = 10f - i;
                // 若数值本身带小数，倾向于它是归一化/百分比，而非像素
                bool hasFraction = Math.Abs(rawX - (float)Math.Round(rawX)) > 0.0001f || Math.Abs(rawW - (float)Math.Round(rawW)) > 0.0001f;
                if (i == 3 && hasFraction) score -= 3f;
                if (i == 1 && !hasFraction && rawW >= 2f) score -= 0.5f;
                if (score > bestScore)
                {
                    bestScore = score;
                    bx = cx; by = cy; bw = cw; bh = ch;
                }
            }
            if (bestScore == float.MinValue)
            {
                // 全都不可行：按当前归一化值夹取（保底）
                bx = Clamp01(rawX); by = Clamp01(rawY); bw = Clamp01(rawW); bh = Clamp01(rawH);
                if (bx + bw > 1f) bw = Math.Max(0f, 1f - bx);
                if (by + bh > 1f) bh = Math.Max(0f, 1f - by);
            }
            x = bx; y = by; w = bw; h = bh;
        }

        private static string GetString(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return "";
            return v.ToString();
        }

        private static bool GetBool(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return false;
            if (v is bool) return (bool)v;
            string text = v.ToString().Trim().ToLowerInvariant();
            return text == "true" || text == "1" || text == "yes" || text == "是";
        }

        private static List<PrivacyRegion> ParsePrivacyRegions(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                throw new AiException("智能打码：模型没有返回识别结果，请重试。");
            string json = StripCodeFence(content);
            int first = json.IndexOf('{');
            int last = json.LastIndexOf('}');
            if (first < 0 || last <= first)
                throw new AiException("智能打码：返回内容不是有效 JSON，请重试。\n" + Truncate(content, 200));
            json = json.Substring(first, last - first + 1);
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 4 * 1024 * 1024;
            Dictionary<string, object> root;
            try { root = ser.Deserialize<Dictionary<string, object>>(json); }
            catch (Exception ex) { throw new AiException("智能打码：返回 JSON 解析失败，请重试。", 0, ex); }
            List<PrivacyRegion> regions = new List<PrivacyRegion>();
            object listObj;
            if (root == null || !root.TryGetValue("regions", out listObj) || listObj == null) return regions;
            object[] arr = AsArray(listObj);
            if (arr == null) return regions;
            foreach (object item in arr)
            {
                Dictionary<string, object> d = item as Dictionary<string, object>;
                if (d == null) continue;
                PrivacyRegion r = new PrivacyRegion();
                r.X = Clamp01(GetFloat(d, "x"));
                r.Y = Clamp01(GetFloat(d, "y"));
                r.W = Clamp01(GetFloat(d, "w"));
                r.H = Clamp01(GetFloat(d, "h"));
                object t;
                if (d.TryGetValue("type", out t) && t != null) r.Type = t.ToString();
                if (r.W <= 0.001f || r.H <= 0.001f) continue;
                regions.Add(r);
            }
            return regions;
        }

        private static string StripCodeFence(string s)
        {
            string t = s.Trim();
            if (t.StartsWith("```"))
            {
                int nl = t.IndexOf('\n');
                if (nl > 0) t = t.Substring(nl + 1);
                int end = t.LastIndexOf("```", StringComparison.Ordinal);
                if (end >= 0) t = t.Substring(0, end);
            }
            return t.Trim();
        }

        private static float Clamp01(float v)
        {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }
    }
}
