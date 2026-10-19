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
    public sealed class DeepSeekClient
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
                return url + "/chat/completions";
            }
        }

        private bool IsDeepSeekEndpoint
        {
            get { return (_cfg.BaseUrl ?? "").IndexOf("deepseek", StringComparison.OrdinalIgnoreCase) >= 0; }
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

        /// <summary>文字提取（OCR）。返回完整识别文本；onDelta 为流式增量。</summary>
        public Task<string> OcrAsync(Bitmap image, Action<string> onDelta, Action<AiUsage> onUsage, CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                Stopwatch sw = Stopwatch.StartNew();
                string system = string.IsNullOrEmpty(_cfg.OcrExtraPrompt)
                    ? OcrSystemPrompt
                    : OcrSystemPrompt + "\n补充要求：" + _cfg.OcrExtraPrompt;
                string userText = "请提取这张截图中的全部文字。";
                string dataUrl = ImageUtil.ToDataUrl(image);
                string body = BuildRequestBody(_cfg.VisionModel, system, userText, dataUrl, true, false, 4096);
                string text = await SendAsync(body, true, onDelta, onUsage, "original", ct).ConfigureAwait(false);
                sw.Stop();
                if (text == null) text = "";
                return text.Trim();
            }, ct);
        }

        /// <summary>提取并翻译：先 OCR，再翻译为 targetLang。返回 [原文, 译文]。</summary>
        public Task<string[]> TranslateImageAsync(Bitmap image, string targetLang,
            Action<string> onOriginalDelta, Action<string> onTranslationDelta, Action<AiUsage> onUsage, CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                string original = await OcrAsync(image, onOriginalDelta, onUsage, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(original) || original.IndexOf("未识别到文字", StringComparison.Ordinal) >= 0)
                {
                    return new string[] { original ?? "", "(未识别到可翻译的文字)" };
                }
                string translation = await TranslateTextAsync(original, targetLang, onTranslationDelta, onUsage, ct).ConfigureAwait(false);
                return new string[] { original, translation };
            }, ct);
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
                throw new AiException("尚未配置 DeepSeek API Key。请点击托盘图标 →「设置…」填写 API Key 后再试。", 401);

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
            req.UserAgent = "WeChatShot/" + AppInfo.Version + " (Windows NT)";
            req.KeepAlive = true;
            req.Timeout = Math.Max(10, _cfg.TimeoutSec) * 1000;
            req.ReadWriteTimeout = Math.Max(10, _cfg.TimeoutSec) * 1000;
            req.Headers["Authorization"] = "Bearer " + (_cfg.ApiKey ?? "").Trim();

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
                case 402: return "DeepSeek 账户余额不足（402），请先充值后再使用。" + suffix;
                case 422: return "请求参数错误（422）" + suffix;
                case 429: return "请求太频繁（429），请稍后再试。" + suffix;
                case 500: return "DeepSeek 服务器故障（500），请稍后重试。" + suffix;
                case 503: return "DeepSeek 服务器繁忙（503），请稍后重试。" + suffix;
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
