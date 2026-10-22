using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

internal static class FeatureConfigTests
{
    private static Assembly app;
    private static int failed;

    [STAThread]
    private static int Main(string[] args)
    {
        app = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        Application.EnableVisualStyles();
        Run("录屏默认快捷键为 Ctrl+Shift+P", delegate {
            Require((string)Get(Config(), "RecordHotkey") == "Ctrl+Shift+P", "录屏默认组合键不符。");
        });
        Run("旧默认录屏热键迁移而自定义与禁用保留", delegate {
            Require((string)Get(Load("Alt+Shift+R", args[1]), "RecordHotkey") == "Ctrl+Shift+P", "旧默认未迁移。");
            Require((string)Get(Load("Ctrl+Alt+F9", args[1]), "RecordHotkey") == "Ctrl+Alt+F9", "自定义热键被覆盖。");
            Require((string)Get(Load("", args[1]), "RecordHotkey") == "", "已禁用热键被重新启用。");
        });
        Run("新增本地功能配置有有界默认值", delegate {
            object cfg = Config();
            Require((int)Get(cfg, "HistoryMaxItems") > 0 && (int)Get(cfg, "HistoryMaxItems") <= 200, "历史默认条数无界。");
            Require((int)Get(cfg, "HistoryMaxMegabytes") > 0 && (int)Get(cfg, "HistoryMaxMegabytes") <= 512, "历史默认容量无界。");
            Require((int)Get(cfg, "CaptureDelaySec") == 0 && (int)Get(cfg, "RecordCountdownSec") == 0, "倒计时默认不应增加等待。");
            Require((string)Get(cfg, "PrivacySensitiveTerms") == "" && (string)Get(cfg, "PrivacyIgnoredTerms") == "", "新配置不得预置个人词条。");
        });
        Run("新增配置往返持久化且私钥不明文", delegate {
            object cfg = Config();
            Set(cfg, "HistoryEnabled", false); Set(cfg, "HistoryMaxItems", 25); Set(cfg, "HistoryMaxMegabytes", 80);
            Set(cfg, "PrivacySensitiveTerms", "测试客户\n测试账号"); Set(cfg, "PrivacyIgnoredTerms", "示例姓名");
            Set(cfg, "OcrCopyFormat", "table"); Set(cfg, "OcrCleanSpaces", true);
            Set(cfg, "CaptureDelaySec", 5); Set(cfg, "RecordCountdownSec", 3);
            Set(cfg, "RecordMicrophoneDeviceId", "synthetic-mic"); Set(cfg, "RecordSystemDeviceId", "synthetic-output");
            Set(cfg, "ApiKey", "synthetic-feature-key");
            object store = Store(args[1]);
            store.GetType().GetMethod("Save").Invoke(store, new[] { cfg });
            object loaded = store.GetType().GetMethod("Load").Invoke(store, null);
            foreach (string field in new[] { "HistoryEnabled", "HistoryMaxItems", "HistoryMaxMegabytes", "PrivacySensitiveTerms", "PrivacyIgnoredTerms", "OcrCopyFormat", "OcrCleanSpaces", "CaptureDelaySec", "RecordCountdownSec", "RecordMicrophoneDeviceId", "RecordSystemDeviceId", "ApiKey" })
                Require(object.Equals(Get(cfg, field), Get(loaded, field)), field + " 未保持往返。");
            string path = (string)store.GetType().GetProperty("ConfigPath").GetValue(store);
            Require(!File.ReadAllText(path).Contains("synthetic-feature-key"), "私钥明文落盘。");
        });
        Run("工具栏不再出现微信转发入口", delegate {
            Type type = app.GetType("XiaoJie.EditorToolbar", true);
            using (Control bar = (Control)Activator.CreateInstance(type, new object[] { 1f }))
            {
                var items = (System.Collections.IEnumerable)type.GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(bar);
                foreach (object item in items)
                {
                    string tip = (string)item.GetType().GetField("Tip").GetValue(item);
                    Require(tip == null || (!tip.Contains("微信") && !tip.Contains("转发")), "仍存在转发入口。");
                }
            }
        });
        Console.WriteLine("结果: " + (5 - failed) + " PASS / " + failed + " FAIL");
        return failed == 0 ? 0 : 1;
    }

    private static object Config() { return Activator.CreateInstance(app.GetType("XiaoJie.AppConfig", true)); }
    private static object Get(object cfg, string name) { return cfg.GetType().GetField(name).GetValue(cfg); }
    private static void Set(object cfg, string name, object value) { cfg.GetType().GetField(name).SetValue(cfg, value); }
    private static object Store(string directory)
    {
        Directory.CreateDirectory(directory);
        object store = Activator.CreateInstance(app.GetType("XiaoJie.ConfigStore", true));
        store.GetType().GetProperty("ConfigPath").GetSetMethod(true).Invoke(store, new object[] { Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json") });
        return store;
    }
    private static object Load(string hotkey, string directory)
    {
        object store = Store(directory);
        string path = (string)store.GetType().GetProperty("ConfigPath").GetValue(store);
        File.WriteAllText(path, "{\"Version\":2,\"RecordHotkey\":\"" + hotkey + "\"}");
        return store.GetType().GetMethod("Load").Invoke(store, null);
    }
    private static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.GetBaseException().Message); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
