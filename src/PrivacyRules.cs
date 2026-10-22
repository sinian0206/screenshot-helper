using System;
using System.Collections.Generic;
using System.Text;

namespace XiaoJie
{
    /// <summary>Applies bounded literal privacy rules without evaluating user regular expressions.</summary>
    public sealed class PrivacyRules
    {
        public const int MaximumTerms = 200;
        public const int MaximumTermLength = 128;
        public const int MaximumInputLength = 26000;
        private readonly List<string> _sensitive;
        private readonly List<string> _ignored;

        public PrivacyRules(string sensitiveTerms, string ignoredTerms)
        {
            _sensitive = Parse(sensitiveTerms);
            _ignored = Parse(ignoredTerms);
        }

        internal bool IsSensitive(string text, out string reason)
        {
            string normalized = Normalize(text);
            foreach (string term in _sensitive)
            {
                if (normalized.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    reason = "自定义敏感词";
                    return true;
                }
            }
            return PrivacyDetector.LooksSensitive(RemoveIgnored(normalized), null, out reason);
        }

        internal string RemoveIgnored(string text)
        {
            string remaining = Normalize(text);
            foreach (string term in _ignored)
            {
                int offset = 0;
                while (offset < remaining.Length)
                {
                    int index = remaining.IndexOf(term, offset, StringComparison.OrdinalIgnoreCase);
                    if (index < 0) break;
                    // Keep character positions and separate adjacent tokens after removing a match.
                    remaining = remaining.Substring(0, index) + new string(' ', term.Length) +
                        remaining.Substring(index + term.Length);
                    offset = index + term.Length;
                }
            }
            return remaining;
        }

        public static string Canonicalize(string input)
        {
            return string.Join(Environment.NewLine, Parse(input));
        }

        private static List<string> Parse(string input)
        {
            input = input ?? "";
            if (input.Length > MaximumInputLength)
                throw new ArgumentException("规则文本超过 26000 个字符，请减少词条。");
            var result = new List<string>();
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in input.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string term = Normalize(line).Trim();
                if (term.Length == 0) continue;
                if (term.Length > MaximumTermLength)
                    throw new ArgumentException("每条规则最多 128 个字符。");
                if (!unique.Add(term)) continue;
                if (result.Count >= MaximumTerms)
                    throw new ArgumentException("每组规则最多 200 条。");
                result.Add(term);
            }
            // Longest literals first keep overlapping exclusions deterministic.
            result.Sort((left, right) => right.Length.CompareTo(left.Length));
            return result;
        }

        private static string Normalize(string text)
        {
            return (text ?? "").Normalize(NormalizationForm.FormKC);
        }
    }
}
