using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace XiaoJie
{
    internal static class WorkflowRegression
    {
        private static int _failures;

        [STAThread]
        private static int Main()
        {
            Application.EnableVisualStyles();
            Run("快捷动作默认值和独立禁用持久化", TestShortcuts);
            Run("录制格式与音频模式持久化及旧配置迁移", TestRecordingConfig);
            Run("审阅候选可取消、分组关闭、后台合并不恢复误判", TestReview);
            Console.WriteLine("失败数: " + _failures);
            return _failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try { test(); Console.WriteLine("[PASS] " + name); }
            catch (Exception error) { _failures++; Console.WriteLine("[FAIL] " + name + ": " + error.GetBaseException().Message); }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static FieldInfo Field(string name)
        {
            var field = typeof(AppConfig).GetField(name);
            Check(field != null, "缺少配置字段 " + name);
            return field;
        }

        private static ConfigStore FixtureStore()
        {
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures");
            Directory.CreateDirectory(directory);
            var store = new ConfigStore();
            typeof(ConfigStore).GetProperty("ConfigPath").GetSetMethod(true).Invoke(store,
                new object[] { Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json") });
            return store;
        }

        private static void TestShortcuts()
        {
            var config = new AppConfig();
            string[] fields = { "OcrHotkey", "TranslateHotkey", "PrivacyHotkey" };
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { config.Hotkey, config.RecordHotkey };
            foreach (string name in fields)
            {
                string value = (string)Field(name).GetValue(config);
                HotkeySpec spec;
                Check(HotkeyUtil.TryParse(value, out spec) && seen.Add(value), "默认快捷键须有效且不重复");
                Field(name).SetValue(config, "");
            }
            var store = FixtureStore();
            store.Save(config);
            var loaded = store.Load();
            foreach (string name in fields) Check((string)Field(name).GetValue(loaded) == "", "空快捷键必须保留禁用状态");
        }

        private static void TestRecordingConfig()
        {
            var config = new AppConfig();
            Check((string)Field("RecordFormat").GetValue(config) == "mp4", "新配置默认 MP4");
            Check((string)Field("RecordAudioMode").GetValue(config) == "none", "默认不录制音频");
            Field("RecordFormat").SetValue(config, "avi");
            Field("RecordAudioMode").SetValue(config, "mixed");
            var store = FixtureStore(); store.Save(config);
            var loaded = store.Load();
            Check((string)Field("RecordFormat").GetValue(loaded) == "avi" && (string)Field("RecordAudioMode").GetValue(loaded) == "mixed", "保留用户录制选择");
            File.WriteAllText(store.ConfigPath, "{\"Version\":1,\"FirstRunDone\":true}");
            loaded = store.Load();
            Check((string)Field("RecordFormat").GetValue(loaded) == "avi", "旧配置保留已有 AVI 行为");
            Check((string)Field("RecordAudioMode").GetValue(loaded) == "none", "旧配置不自动启用麦克风");
        }

        private static void TestReview()
        {
            Type type = typeof(AppConfig).Assembly.GetType("XiaoJie.PrivacyReviewSession");
            Check(type != null, "缺少隐私候选审阅模型");
            object session = Activator.CreateInstance(type, true);
            var analysis = new PrivacyAnalysis();
            typeof(PrivacyAnalysis).GetField("HasLocalCoordinates", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(analysis, true);
            analysis.Lines.Add(new ImageTextBlock { Text = "手机：13812345678", Kind = "phone", Sensitive = true,
                X = .1f, Y = .1f, W = .4f, H = .1f });
            analysis.Lines.Add(new ImageTextBlock { Text = "姓名：测试姓名", Kind = "name", Sensitive = true,
                X = .1f, Y = .4f, W = .4f, H = .1f });
            Action merge = () => Call(session, "Merge", analysis, new Rectangle(0, 0, 500, 300));
            merge(); Check((int)Call(session, "get_Count") == 2, "生成两个候选");
            Call(session, "SetCandidateEnabled", 0, false);
            Check(((System.Collections.ICollection)Call(session, "BuildRects")).Count == 1, "可以取消单处误判");
            merge(); Check((int)Call(session, "get_Count") == 2, "分阶段结果不重复");
            Check(((System.Collections.ICollection)Call(session, "BuildRects")).Count == 1, "后续结果不能重新勾选误判");
            Call(session, "SetCategoryEnabled", "name", false);
            Check(((System.Collections.ICollection)Call(session, "BuildRects")).Count == 0, "类别开关移除该类别遮挡");
            Call(session, "SetCategoryEnabled", "name", true);
            Check(((System.Collections.ICollection)Call(session, "BuildRects")).Count == 1, "类别恢复不恢复其他误判");
        }

        private static object Call(object instance, string name, params object[] args)
        {
            return instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(instance, args);
        }
    }
}
