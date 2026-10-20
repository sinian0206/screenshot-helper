using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace XiaoJie
{
    /// <summary>
    /// 所有可配置项。修改后请调用 ConfigStore.Save() 持久化。
    /// 注意：API Key 在磁盘上使用 Windows DPAPI 加密保存，不会明文落盘。
    /// </summary>
    public class AppConfig
    {
        // ===== DeepSeek API =====
        public string ApiKey = "";
        public string BaseUrl = "https://api.deepseek.com";
        public string VisionModel = "";
        public string TextModel = "deepseek-flash";
        public int TimeoutSec = 120;
        public bool SemanticPrivacy = true;        // 只发送 OCR 文字，补充隐私分类。
        public bool VisionPrivacy = false;         // 可选：上传选区识别头像、二维码。

        // ===== 截图 =====
        public string Hotkey = "Alt+A";
        public string OcrHotkey = "Alt+Shift+O";
        public string TranslateHotkey = "Alt+Shift+T";
        public string PrivacyHotkey = "Alt+Shift+M";
        public string SaveDir = "";
        public string SaveFormat = "png";          // png / jpg
        public string TranslateTarget = "中文";     // 翻译目标语言
        public bool AutoCopy = true;                // 点击“完成”后复制到剪贴板
        public bool AutoSave = false;               // 点击“完成”后同时保存文件
        public bool ShowColorValue = true;          // 选图时显示鼠标处色值
        public bool ShowDimensions = true;          // 选区左上角显示像素尺寸
        public bool PinAlwaysOnTop = true;          // 钉图窗口默认置顶
        public float MosaicBlock = 24f;             // 马赛克像素块大小（图像像素，默认更粗以防文字可读）
        public bool ShowUsage = true;               // AI 面板显示 token 用量
        public string OcrExtraPrompt = "";          // 文字提取附加提示词（可空）
        public string TranslateExtraPrompt = "";    // 翻译附加提示词（可空）

        // ===== 视频录制 =====
        public string RecordHotkey = "Ctrl+Shift+P";
        public int RecordFps = 15;
        public int RecordQuality = 85;
        public bool RecordIncludeCursor = true;
        public string RecordFormat = "mp4";
        public string RecordAudioMode = "none";    // none / system / microphone / mixed

        // ===== 启动 =====
        public bool StartWithWindows = false;
        public bool FirstRunDone = false;

        // ===== 截图历史与高级工具 =====
        public bool HistoryEnabled = true;
        public int HistoryMaxCount = 100;
        public int HistoryMaxMB = 500;
        public string CustomSensitiveTerms = "";
        public string CustomIgnoredTerms = "";
        public string LastCaptureRect = "";
        public int CountdownSeconds = 3;

        // ===== 运行时状态（不参与 JSON 序列化语义，仅内存使用）=====
        [ScriptIgnore]
        public bool ApiKeyDecryptFailed = false;

        public AppConfig Clone()
        {
            return (AppConfig)MemberwiseClone();
        }

        internal static bool IsOfficialDeepSeekUrl(string value)
        {
            string url = (value ?? "").Trim();
            if (url.Length == 0) return false;
            if (!url.Contains("://")) url = "https://" + url;
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) &&
                uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
                string.Equals(uri.Host, "api.deepseek.com", StringComparison.OrdinalIgnoreCase);
        }

        internal static AppConfig CreateDefaultsForReset(string currentBaseUrl, string currentKey)
        {
            var defaults = new AppConfig { FirstRunDone = true };
            // 密钥属于当前服务；恢复官方地址时不能沿用第三方服务的凭据。
            if (IsOfficialDeepSeekUrl(currentBaseUrl)) defaults.ApiKey = currentKey ?? "";
            return defaults;
        }

        /// <summary>把 other 的所有配置字段复制到当前实例（不复制运行时标记）。</summary>
        public void CopyFrom(AppConfig other)
        {
            if (other == null) return;
            foreach (FieldInfo fi in typeof(AppConfig).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                fi.SetValue(this, fi.GetValue(other));
            }
        }

        public string EffectiveSaveDir()
        {
            string dir = (SaveDir ?? "").Trim();
            if (dir.Length > 0) return dir;
            return DefaultSaveDir();
        }

        public static string DefaultSaveDir()
        {
            string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrEmpty(pics))
                pics = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
            return Path.Combine(pics, "截图助手");
        }

        /// <summary>当前配置的“保存文件夹”（已展开默认目录）。</summary>
        public string EnsureSaveDir()
        {
            string dir = EffectiveSaveDir();
            try { Directory.CreateDirectory(dir); } catch (Exception ex) { Log.Write("创建保存目录失败: " + ex.Message); }
            return dir;
        }
    }

    /// <summary>config.json 的磁盘格式（API Key 加密后的字段）。</summary>
    internal class ConfigDto
    {
        public int Version = 2;
        public string ApiKeyEnc = "";
        public string BaseUrl;
        public string VisionModel;
        public string TextModel;
        public int TimeoutSec;
        public bool? SemanticPrivacy;
        public bool? VisionPrivacy;
        public string Hotkey;
        public string OcrHotkey;
        public string TranslateHotkey;
        public string PrivacyHotkey;
        public string SaveDir;
        public string SaveFormat;
        public string TranslateTarget;
        public bool AutoCopy;
        public bool AutoSave;
        public bool ShowColorValue;
        public bool ShowDimensions;
        public bool PinAlwaysOnTop;
        public float MosaicBlock;
        public bool ShowUsage;
        public string OcrExtraPrompt;
        public string TranslateExtraPrompt;
        public string RecordHotkey;
        public int RecordFps;
        public int RecordQuality;
        public bool RecordIncludeCursor;
        public string RecordFormat;
        public string RecordAudioMode;
        public bool StartWithWindows;
        public bool FirstRunDone;
        public bool? HistoryEnabled;
        public int HistoryMaxCount;
        public int HistoryMaxMB;
        public string CustomSensitiveTerms;
        public string CustomIgnoredTerms;
        public string LastCaptureRect;
        public int CountdownSeconds;
    }

    /// <summary>
    /// 配置读写。优先把 config.json 放在程序（exe）同目录，实现绿色便携；
    /// 若程序目录不可写（例如放在 Program Files），自动降级到 %APPDATA%\截图助手\。
    /// </summary>
    public sealed class ConfigStore
    {
        private const string FileName = "config.json";

        public string ConfigPath { get; private set; }
        public string DataDir { get; private set; }
        public bool Portable { get; private set; }

        public ConfigStore()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            exeDir = exeDir != null ? exeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : "";
            if (exeDir.Length > 0 && IsWritableDir(exeDir))
            {
                Portable = true;
                DataDir = exeDir;
            }
            else
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                DataDir = Path.Combine(appData, "截图助手");
                Portable = false;
                try { Directory.CreateDirectory(DataDir); } catch { }
            }
            ConfigPath = Path.Combine(DataDir, FileName);
        }

        private static bool IsWritableDir(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".xj_write_test_" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public string LogPath
        {
            get { return Path.Combine(DataDir, "截图助手.log"); }
        }

        public AppConfig Load()
        {
            AppConfig cfg = new AppConfig();
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    ConfigDto dto = ser.Deserialize<ConfigDto>(json);
                    if (dto != null)
                    {
                        if (!string.IsNullOrWhiteSpace(dto.BaseUrl)) cfg.BaseUrl = dto.BaseUrl.Trim();
                        if (!string.IsNullOrWhiteSpace(dto.VisionModel)) cfg.VisionModel = dto.VisionModel.Trim();
                        if (!string.IsNullOrWhiteSpace(dto.TextModel)) cfg.TextModel = dto.TextModel.Trim();
                        if (dto.TimeoutSec >= 10) cfg.TimeoutSec = dto.TimeoutSec;
                        if (!string.IsNullOrWhiteSpace(dto.Hotkey)) cfg.Hotkey = dto.Hotkey;
                        if (dto.OcrHotkey != null) cfg.OcrHotkey = dto.OcrHotkey;
                        if (dto.TranslateHotkey != null) cfg.TranslateHotkey = dto.TranslateHotkey;
                        if (dto.PrivacyHotkey != null) cfg.PrivacyHotkey = dto.PrivacyHotkey;
                        cfg.SaveDir = dto.SaveDir ?? "";
                        if (!string.IsNullOrWhiteSpace(dto.SaveFormat)) cfg.SaveFormat = dto.SaveFormat;
                        if (!string.IsNullOrWhiteSpace(dto.TranslateTarget)) cfg.TranslateTarget = dto.TranslateTarget;
                        cfg.AutoCopy = dto.AutoCopy;
                        cfg.AutoSave = dto.AutoSave;
                        cfg.ShowColorValue = dto.ShowColorValue;
                        cfg.ShowDimensions = dto.ShowDimensions;
                        cfg.PinAlwaysOnTop = dto.PinAlwaysOnTop;
                        if (dto.MosaicBlock >= 2 && dto.MosaicBlock <= 64) cfg.MosaicBlock = dto.MosaicBlock;
                        cfg.ShowUsage = dto.ShowUsage;
                        cfg.OcrExtraPrompt = dto.OcrExtraPrompt ?? "";
                        cfg.TranslateExtraPrompt = dto.TranslateExtraPrompt ?? "";
                        cfg.SemanticPrivacy = dto.SemanticPrivacy ?? true;
                        cfg.VisionPrivacy = dto.VisionPrivacy ?? false;
                        cfg.RecordHotkey = dto.RecordHotkey != null ? dto.RecordHotkey : cfg.RecordHotkey;
                        if (dto.RecordFps >= 5 && dto.RecordFps <= 60) cfg.RecordFps = dto.RecordFps;
                        if (dto.RecordQuality >= 40 && dto.RecordQuality <= 100) cfg.RecordQuality = dto.RecordQuality;
                        cfg.RecordIncludeCursor = dto.RecordIncludeCursor;
                        // 已有配置保留 AVI；只对首次启动采用 MP4，避免无提示改变旧工作流。
                        cfg.RecordFormat = string.Equals(dto.RecordFormat, "mp4", StringComparison.OrdinalIgnoreCase) ? "mp4" : "avi";
                        string audio = (dto.RecordAudioMode ?? "none").ToLowerInvariant();
                        cfg.RecordAudioMode = audio == "system" || audio == "microphone" || audio == "mixed" ? audio : "none";
                        cfg.FirstRunDone = dto.FirstRunDone;
                        if (dto.HistoryEnabled.HasValue) cfg.HistoryEnabled = dto.HistoryEnabled.Value;
                        if (dto.HistoryMaxCount >= 10 && dto.HistoryMaxCount <= 1000) cfg.HistoryMaxCount = dto.HistoryMaxCount;
                        if (dto.HistoryMaxMB >= 50 && dto.HistoryMaxMB <= 5000) cfg.HistoryMaxMB = dto.HistoryMaxMB;
                        if (dto.CustomSensitiveTerms != null) cfg.CustomSensitiveTerms = dto.CustomSensitiveTerms;
                        if (dto.CustomIgnoredTerms != null) cfg.CustomIgnoredTerms = dto.CustomIgnoredTerms;
                        if (dto.LastCaptureRect != null) cfg.LastCaptureRect = dto.LastCaptureRect;
                        if (dto.CountdownSeconds >= 1 && dto.CountdownSeconds <= 30) cfg.CountdownSeconds = dto.CountdownSeconds;

                        if (!string.IsNullOrEmpty(dto.ApiKeyEnc))
                        {
                            string key = Unprotect(dto.ApiKeyEnc);
                            if (key != null) cfg.ApiKey = key;
                            else cfg.ApiKeyDecryptFailed = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("读取配置失败: " + ex.Message);
            }
            // 开机自启以注册表实际状态为准
            try { cfg.StartWithWindows = Autostart.IsEnabled(); } catch { }
            return cfg;
        }

        public void Save(AppConfig cfg)
        {
            try
            {
                ConfigDto dto = new ConfigDto();
                dto.ApiKeyEnc = Protect(cfg.ApiKey);
                dto.BaseUrl = cfg.BaseUrl;
                dto.VisionModel = cfg.VisionModel;
                dto.TextModel = cfg.TextModel;
                dto.TimeoutSec = cfg.TimeoutSec;
                dto.SemanticPrivacy = cfg.SemanticPrivacy;
                dto.VisionPrivacy = cfg.VisionPrivacy;
                dto.Hotkey = cfg.Hotkey;
                dto.OcrHotkey = cfg.OcrHotkey;
                dto.TranslateHotkey = cfg.TranslateHotkey;
                dto.PrivacyHotkey = cfg.PrivacyHotkey;
                dto.SaveDir = cfg.SaveDir;
                dto.SaveFormat = cfg.SaveFormat;
                dto.TranslateTarget = cfg.TranslateTarget;
                dto.AutoCopy = cfg.AutoCopy;
                dto.AutoSave = cfg.AutoSave;
                dto.ShowColorValue = cfg.ShowColorValue;
                dto.ShowDimensions = cfg.ShowDimensions;
                dto.PinAlwaysOnTop = cfg.PinAlwaysOnTop;
                dto.MosaicBlock = cfg.MosaicBlock;
                dto.ShowUsage = cfg.ShowUsage;
                dto.OcrExtraPrompt = cfg.OcrExtraPrompt;
                dto.TranslateExtraPrompt = cfg.TranslateExtraPrompt;
                dto.StartWithWindows = cfg.StartWithWindows;
                dto.RecordHotkey = cfg.RecordHotkey;
                dto.RecordFps = cfg.RecordFps;
                dto.RecordQuality = cfg.RecordQuality;
                dto.RecordIncludeCursor = cfg.RecordIncludeCursor;
                dto.RecordFormat = cfg.RecordFormat;
                dto.RecordAudioMode = cfg.RecordAudioMode;
                dto.FirstRunDone = cfg.FirstRunDone;
                dto.HistoryEnabled = cfg.HistoryEnabled;
                dto.HistoryMaxCount = cfg.HistoryMaxCount;
                dto.HistoryMaxMB = cfg.HistoryMaxMB;
                dto.CustomSensitiveTerms = cfg.CustomSensitiveTerms;
                dto.CustomIgnoredTerms = cfg.CustomIgnoredTerms;
                dto.LastCaptureRect = cfg.LastCaptureRect;
                dto.CountdownSeconds = cfg.CountdownSeconds;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(dto);
                string tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(ConfigPath))
                {
                    try { File.Replace(tmp, ConfigPath, null); }
                    catch { File.Copy(tmp, ConfigPath, true); File.Delete(tmp); }
                }
                else
                {
                    File.Move(tmp, ConfigPath);
                }
                Log.Write("配置已保存: " + ConfigPath);
            }
            catch (Exception ex)
            {
                Log.Write("保存配置失败: " + ex.Message);
                throw;
            }
        }

        private static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(plain);
                byte[] enc = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(enc);
            }
            catch (Exception ex)
            {
                Log.Write("API Key 加密失败: " + ex.Message);
                return "";
            }
        }

        private static string Unprotect(string enc)
        {
            try
            {
                byte[] data = Convert.FromBase64String(enc);
                byte[] dec = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch
            {
                return null;
            }
        }
    }
}
