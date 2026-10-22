using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace XiaoJie
{
    internal sealed class OcrFormatResult
    {
        internal string Text = "";
        internal string Warning = "";
        internal bool IsTable;
    }

    /// <summary>Formats an immutable OCR result without rerunning recognition.</summary>
    internal static class OcrTextFormatter
    {
        internal static OcrFormatResult Format(LocalOcrResult source, string format, bool cleanSpaces)
        {
            if (source == null || source.Lines == null || source.Lines.Count == 0)
            {
                return new OcrFormatResult { Text = "" };
            }

            var lines = new List<LocalOcrLine>(source.Lines);
            format = (format ?? "lines").ToLowerInvariant();

            if (format == "table")
            {
                return FormatTable(lines, cleanSpaces);
            }

            if (format == "paragraphs")
            {
                return FormatParagraphs(lines, cleanSpaces);
            }

            // 默认：保留换行
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                string text = lines[i].Text ?? "";
                if (cleanSpaces) text = CleanExtraSpaces(text);
                sb.AppendLine(text);
            }
            return new OcrFormatResult { Text = sb.ToString().TrimEnd() };
        }

        private static OcrFormatResult FormatParagraphs(List<LocalOcrLine> lines, bool cleanSpaces)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                string curr = (lines[i].Text ?? "").Trim();
                if (cleanSpaces) curr = CleanExtraSpaces(curr);
                if (curr.Length == 0) continue;

                sb.Append(curr);

                if (i < lines.Count - 1)
                {
                    char last = curr[curr.Length - 1];
                    // 句号、问号、感叹号、冒号视为段落结束，换行；否则空格连接
                    if (last == '。' || last == '！' || last == '？' || last == '：' ||
                        last == '.' || last == '!' || last == '?' || last == ':')
                    {
                        sb.AppendLine();
                    }
                    else
                    {
                        // 若是中文字符衔接，不加空格；若包含英文字符，加空格
                        if (IsChinese(last) && IsChinese(lines[i + 1].Text[0]))
                        {
                            // 直接连结
                        }
                        else
                        {
                            sb.Append(" ");
                        }
                    }
                }
            }
            return new OcrFormatResult { Text = sb.ToString() };
        }

        private static OcrFormatResult FormatTable(List<LocalOcrLine> lines, bool cleanSpaces)
        {
            // 按 Y 轴坐标将相近的行聚为同一表格行 (阈值约行高的 60%)
            var sorted = new List<LocalOcrLine>(lines);
            sorted.Sort((a, b) => a.Bounds.Top.CompareTo(b.Bounds.Top));

            var rowGroups = new List<List<LocalOcrLine>>();
            foreach (var line in sorted)
            {
                bool added = false;
                float lineCenterY = line.Bounds.Top + line.Bounds.Height / 2f;
                foreach (var group in rowGroups)
                {
                    float groupCenterY = group[0].Bounds.Top + group[0].Bounds.Height / 2f;
                    float threshold = Math.Max(12f, Math.Min(group[0].Bounds.Height, line.Bounds.Height) * 0.7f);
                    if (Math.Abs(lineCenterY - groupCenterY) < threshold)
                    {
                        group.Add(line);
                        added = true;
                        break;
                    }
                }
                if (!added)
                {
                    rowGroups.Add(new List<LocalOcrLine> { line });
                }
            }

            var sb = new StringBuilder();
            bool hasMultiColumns = false;

            foreach (var group in rowGroups)
            {
                // 同一行内的单元格按 X 坐标从左到右排序
                group.Sort((a, b) => a.Bounds.Left.CompareTo(b.Bounds.Left));
                if (group.Count > 1) hasMultiColumns = true;

                for (int c = 0; c < group.Count; c++)
                {
                    string cell = (group[c].Text ?? "").Trim();
                    if (cleanSpaces) cell = CleanExtraSpaces(cell);
                    sb.Append(cell);
                    if (c < group.Count - 1) sb.Append("\t"); // Tab 分隔列
                }
                sb.AppendLine();
            }

            return new OcrFormatResult
            {
                Text = sb.ToString().TrimEnd(),
                IsTable = hasMultiColumns,
                Warning = hasMultiColumns ? "已按表格多列提取（Tab 制表符分隔，可直接粘贴进电子表格）。" : "未检测到多列结构，已按单列输出。"
            };
        }

        private static string CleanExtraSpaces(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            // 清理中文字符之间的多余空格: "截 图 助 手" -> "截图助手"
            text = Regex.Replace(text, @"([\u4e00-\u9fa5])\s+([\u4e00-\u9fa5])", "$1$2");
            text = Regex.Replace(text, @"([\u4e00-\u9fa5])\s+([\u4e00-\u9fa5])", "$1$2");
            // 清理多余连续空格为单个空格
            text = Regex.Replace(text, @"[ \t]{2,}", " ");
            return text.Trim();
        }

        private static bool IsChinese(char c)
        {
            return c >= 0x4e00 && c <= 0x9fa5;
        }
    }
}
