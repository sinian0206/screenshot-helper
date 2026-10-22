using System;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace XiaoJie
{
    internal static class OfficialDefaultsRegression
    {
        private static int _failures;

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Run("新配置使用官方地址和当前模型", TestNewConfiguration);
            Run("官方和自定义端点正确拼接", TestEndpoints);
            Run("官方特有参数只用于官方域名", TestOfficialHost);
            Run("配置往返保留自定义端点和密钥", TestConfigRoundTrip);
            Run("恢复默认不转移第三方密钥", TestReset);
            Run("视觉模型为空时仍可保存文本配置", TestOptionalVision);
            Run("未配置视觉模型时拒绝图片请求", TestMissingVisionModel);
            Run("示例配置使用官方默认", delegate { TestExample(args[0]); });
            Console.WriteLine(_failures == 0 ? "全部通过" : "失败数: " + _failures);
            return _failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try { test(); Console.WriteLine("[PASS] " + name); }
            catch (Exception ex)
            {
                _failures++;
                Console.WriteLine("[FAIL] " + name + ": " + ex.GetBaseException().Message);
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void TestNewConfiguration()
        {
            var config = new AppConfig();
            Check(config.BaseUrl == "https://api.deepseek.com", "默认 Base URL 应为不带 v1 的官方地址");
            Check(config.TextModel == "deepseek-flash", "默认文本模型应为 deepseek-flash");
            Check(config.VisionModel == "" && !config.VisionPrivacy, "视觉模型默认可空且图片检测默认关闭");
            Check(config.ApiKey == "", "新配置不含预置密钥");
        }

        private static void TestEndpoints()
        {
            string[] values = { "", "https://api.deepseek.com", "https://api.deepseek.com/", "api.deepseek.com", "https://api.deepseek.com/chat/completions" };
            foreach (string value in values)
                Check(new DeepSeekClient(new AppConfig { BaseUrl = value }).Endpoint == "https://api.deepseek.com/chat/completions", "官方根地址直接拼接 chat/completions");
            string[] legacyValues = { "https://api.deepseek.com/v1", "https://api.deepseek.com/v1/", "https://api.deepseek.com/v1/chat/completions" };
            foreach (string value in legacyValues)
                Check(new DeepSeekClient(new AppConfig { BaseUrl = value }).Endpoint == "https://api.deepseek.com/v1/chat/completions", "保留用户显式配置的 v1 路径");
            Check(new DeepSeekClient(new AppConfig { BaseUrl = "https://gateway.example/custom/v1" }).Endpoint == "https://gateway.example/custom/v1/chat/completions", "保留自定义路径");
            Check(new DeepSeekClient(new AppConfig { BaseUrl = "https://gateway.example" }).Endpoint == "https://gateway.example/v1/chat/completions", "第三方根地址保留兼容拼接行为");
            Check(new DeepSeekClient(new AppConfig { BaseUrl = "https://api.deepseek.com.gateway.example" }).Endpoint == "https://api.deepseek.com.gateway.example/v1/chat/completions", "相似域名不能使用官方根地址规则");
        }

        private static string BuildBody(string url, string image)
        {
            var client = new DeepSeekClient(new AppConfig { BaseUrl = url });
            var method = typeof(DeepSeekClient).GetMethod("BuildRequestBody", BindingFlags.Instance | BindingFlags.NonPublic);
            return (string)method.Invoke(client, new object[] { "deepseek-flash", "system", "test", image, false, false, 32 });
        }

        private static void TestOfficialHost()
        {
            Check(BuildBody("https://api.deepseek.com/v1", null).Contains("\"thinking\""), "官方端点应关闭思考以减少等待");
            Check(BuildBody("", null).Contains("\"thinking\""), "空配置的官方回退也应关闭思考");
            Check(!BuildBody("https://gateway.example/deepseek/v1", null).Contains("\"thinking\""), "路径中的 deepseek 不能判定为官方");
            Check(!BuildBody("https://api.deepseek.com.gateway.example/v1", null).Contains("\"thinking\""), "相似域名不能判定为官方");
            Check(!BuildBody("https://api.deepseek.com@gateway.example/v1", null).Contains("\"thinking\""), "用户信息部分不能判定为官方域名");
        }

        private static void TestConfigRoundTrip()
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config-fixture");
            Directory.CreateDirectory(dir);
            var store = new ConfigStore();
            typeof(ConfigStore).GetProperty("ConfigPath").GetSetMethod(true).Invoke(store, new object[] { Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json") });
            var noFile = store.Load();
            Check(noFile.BaseUrl == "https://api.deepseek.com" && noFile.ApiKey == "", "无配置文件使用官方默认且无密钥");
            var custom = new AppConfig { BaseUrl = "https://gateway.example/custom/v1", ApiKey = "synthetic-regression-key", TextModel = "custom-text", VisionModel = "", SemanticPrivacy = false };
            store.Save(custom);
            var loaded = store.Load();
            Check(loaded.BaseUrl == custom.BaseUrl && loaded.ApiKey == custom.ApiKey, "已有自定义端点和密钥必须保持配对");
            Check(loaded.TextModel == custom.TextModel && loaded.VisionModel == "", "自定义文本模型与空视觉模型应保持");
            Check(!File.ReadAllText(store.ConfigPath).Contains(custom.ApiKey), "磁盘不应包含明文密钥");
        }

        private static void TestReset()
        {
            var method = typeof(AppConfig).GetMethod("CreateDefaultsForReset", BindingFlags.Static | BindingFlags.NonPublic);
            Check(method != null, "恢复默认需显式处理端点与密钥配对");
            var custom = (AppConfig)method.Invoke(null, new object[] { "https://gateway.example/v1", "synthetic-regression-key" });
            Check(custom.BaseUrl == "https://api.deepseek.com" && custom.ApiKey == "", "切回官方不得沿用第三方密钥");
            var official = (AppConfig)method.Invoke(null, new object[] { "https://api.deepseek.com/v1", "synthetic-official-key" });
            Check(official.ApiKey == "synthetic-official-key", "保留当前官方端点的密钥");
            var unspecified = (AppConfig)method.Invoke(null, new object[] { "", "synthetic-regression-key" });
            Check(unspecified.ApiKey == "", "未指定原服务时不保留来源不明的密钥");
            Check(custom.TextModel == "deepseek-flash" && custom.FirstRunDone && !custom.VisionPrivacy, "其余字段恢复默认");
        }

        private static void TestOptionalVision()
        {
            var config = new AppConfig { ApiKey = "synthetic-regression-key", VisionModel = "", VisionPrivacy = false };
            using (var form = new SettingsForm(config, new ConfigStore()))
            {
                var method = typeof(SettingsForm).GetMethod("ValidateInputs", BindingFlags.Instance | BindingFlags.NonPublic);
                Check((bool)method.Invoke(form, null), "关闭图片检测时不要求视觉模型");
                var field = typeof(SettingsForm).GetField("_baseUrlBox", BindingFlags.Instance | BindingFlags.NonPublic);
                Check(((TextBox)field.GetValue(form)).Text == "https://api.deepseek.com", "设置界面显示不带 v1 的官方默认端点");
            }
        }

        private static void TestExample(string path)
        {
            var dto = new JavaScriptSerializer().Deserialize<ConfigDto>(File.ReadAllText(path));
            Check(dto.BaseUrl == "https://api.deepseek.com" && dto.TextModel == "deepseek-flash", "示例配置应与运行默认一致");
            Check(dto.VisionModel == "" && dto.VisionPrivacy == false && dto.ApiKeyEnc == "", "示例不含密钥且图片检测关闭");
        }

        private static void TestMissingVisionModel()
        {
            var client = new DeepSeekClient(new AppConfig());
            var method = typeof(DeepSeekClient).GetMethod("BuildRequestBody", BindingFlags.Instance | BindingFlags.NonPublic);
            try
            {
                method.Invoke(client, new object[] { "", "system", "test", "data:image/png;base64,AA==", false, true, 32 });
                throw new InvalidOperationException("不得用文本默认模型隐式代替未配置的视觉模型");
            }
            catch (TargetInvocationException ex)
            {
                Check(ex.InnerException is AiException && ex.InnerException.Message.Contains("视觉模型"), "应报告视觉模型配置错误");
            }
        }
    }
}
