using System;
using System.Text;

namespace XiaoJie
{
    internal static class PrivacyRulesRegression
    {
        [STAThread]
        private static int Main()
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            try
            {
                string reason;
                var rules = new PrivacyRules("客户甲\nProject Alpha", "");
                Check(rules.IsSensitive("正在联系客户甲", out reason), "自定义中文敏感词未命中。");
                Check(rules.IsSensitive("Status: Project Alpha", out reason), "自定义英文敏感词未命中。");
                Console.WriteLine("[PASS] 字面敏感词优先检测");
                return 0;
            }
            catch (Exception error) { Console.WriteLine("[FAIL] " + error.Message); return 1; }
        }

        private static void Check(bool value, string message)
        {
            if (!value) throw new Exception(message);
        }
    }
}
