path = r'D:\Agents\微信截图助手\src\SelfTest.cs'
src = open(path, encoding='utf-8').read()

call = '''                // ===== 离屏 UI 测试（不干扰用户桌面）=====
                RunUiTests(report, Path.GetDirectoryName(outPath), check);
'''
new_call = '''                // ===== DeepSeek API mock 测试（仅当传入 --mock-url 时）=====
                string mockUrl = Program.GetArgValue(args, "--mock-url", "");
                if (!string.IsNullOrEmpty(mockUrl))
                {
                    RunApiTests(report, check, mockUrl, Program.GetArgValue(args, "--mock-key", "mock-key"));
                }

                // ===== 离屏 UI 测试（不干扰用户桌面）=====
                RunUiTests(report, Path.GetDirectoryName(outPath), check);
'''
assert call in src
src = src.replace(call, new_call, 1)

api_methods = r'''
        // ============================ DeepSeek API mock 集成测试 ============================

        private static void RunApiTests(StringBuilder report, Action<string, bool, string> check, string baseUrl, string apiKey)
        {
            report.AppendLine();
            report.AppendLine("DeepSeek API mock 测试: " + baseUrl);
            AppConfig cfg = new AppConfig();
            cfg.BaseUrl = baseUrl;
            cfg.ApiKey = apiKey;
            cfg.VisionModel = "deepseek-flash";
            cfg.TextModel = "deepseek-flash";
            cfg.TimeoutSec = 30;
            DeepSeekClient client = new DeepSeekClient(cfg);
            Bitmap image = BuildTestScreenshot(420, 220);
            try
            {
                using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(40)))
                {
                    AiUsage usage = RunSync(client.TestConnectionAsync(cts.Token));
                    check("API 测试连接", usage != null, usage == null ? "" : usage.ToString());

                    StringBuilder deltas = new StringBuilder();
                    string ocr = RunSync(client.OcrAsync(image, delegate (string piece) { deltas.Append(piece); }, null, cts.Token));
                    check("API 文字提取(流式)", !string.IsNullOrEmpty(ocr) && deltas.Length > 0,
                        "结果=" + Shorten(ocr) + "；流式增量长度=" + deltas.Length);

                    StringBuilder originalDeltas = new StringBuilder();
                    StringBuilder translationDeltas = new StringBuilder();
                    string[] translated = RunSync(client.TranslateImageAsync(image, "英文",
                        delegate (string piece) { originalDeltas.Append(piece); },
                        delegate (string piece) { translationDeltas.Append(piece); }, null, cts.Token));
                    bool translateOk = translated != null && translated.Length == 2 &&
                                       !string.IsNullOrEmpty(translated[0]) && !string.IsNullOrEmpty(translated[1]) &&
                                       translationDeltas.Length > 0;
                    check("API 图片翻译(流式)", translateOk,
                        translateOk ? "原文=" + Shorten(translated[0]) + "；译文=" + Shorten(translated[1]) :
                        "translated=" + (translated == null ? "null" : string.Join(" | ", translated)));

                    List<PrivacyRegion> regions = RunSync(client.DetectPrivacyAsync(image, cts.Token));
                    check("API 一键打码检测", regions != null && regions.Count >= 2,
                        regions == null ? "null" : "区域数=" + regions.Count + "，首个=" + (regions.Count > 0 ? regions[0].ToString() : ""));

                    AppConfig badCfg = cfg.Clone();
                    badCfg.ApiKey = "wrong-key";
                    DeepSeekClient badClient = new DeepSeekClient(badCfg);
                    bool gotError = false;
                    string errorMessage = "";
                    try
                    {
                        RunSync(badClient.TestConnectionAsync(cts.Token));
                    }
                    catch (AiException ex)
                    {
                        gotError = true;
                        errorMessage = ex.Message;
                    }
                    check("API 401 错误提示", gotError && errorMessage.Contains("API Key"), Shorten(errorMessage));
                }
            }
            catch (Exception ex)
            {
                check("API mock 测试", false, ex.Message);
            }
            finally
            {
                image.Dispose();
            }
        }

        private static T RunSync<T>(Task<T> task)
        {
            return task.GetAwaiter().GetResult();
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string oneLine = text.Replace("\r", " ").Replace("\n", " ");
            return oneLine.Length <= 60 ? oneLine : oneLine.Substring(0, 60) + "…";
        }
'''
# 插入到类结束之前
src = src.rstrip()
assert src.endswith('}')
src = src[:-1] + api_methods + '    }\n}\n'
open(path, 'w', encoding='utf-8').write(src)
print('patched SelfTest with API tests')
