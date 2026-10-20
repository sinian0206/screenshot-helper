using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;

namespace XiaoJie
{
    /// <summary>
    /// 智能打码的本地判定：把 AI 返回的「每行文字 + 人脸框」合并成需要打码的屏幕矩形。
    /// 除了模型给出的 sensitive 标记外，再用正则兜底识别手机号/身份证/银行卡/邮箱/地址/账号等，
    /// 并给人脸/头像框留出外扩边距，尽量提高召回。
    /// </summary>
    public static class PrivacyDetector
    {
        private static readonly Regex PhoneRegex = new Regex(@"(?<!\d)(?:\+?86)?1[3-9]\d{9}(?!\d)", RegexOptions.Compiled);
        private static readonly Regex LongNumberRegex = new Regex(@"(?<!\d)(?:\d{17}[\dXx]|\d{15,19})(?!\d)", RegexOptions.Compiled);
        private static readonly Regex EmailRegex = new Regex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
        private static readonly Regex CodeRegex = new Regex(@"(验证码|校验码|动态码|密码)\s*[:：]?\s*\d{4,8}", RegexOptions.Compiled);
        private static readonly Regex NameHintRegex = new Regex(@"(姓名|联系人|名字|昵称|收件人|用户名|账户名|开户名)(?:\s*[:：]|\s+\S)", RegexOptions.Compiled);
        private static readonly Regex AddressRegex = new Regex(@"(?:地址|住址|收货地址)\s*[:：]?\s*\S+|(?:省|市|区|县|镇|街道|路|弄|巷|小区|大厦|公寓|村).{0,18}(?:[0-9一二三四五六七八九十]+号|[0-9]+室|省|市|区|县|街道|小区|公寓)", RegexOptions.Compiled);
        private static readonly Regex SecretHintRegex = new Regex(@"(?:密码|口令|API\s*Key|Access\s*Token|Password)\s*[:：=]\s*\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AccountRegex = new Regex(@"(微信|QQ|账号|帐号|卡号|支付宝|淘宝|抖音|微博|邮箱)\s*[:：]?\s*[A-Za-z0-9_\-@\.]{3,}", RegexOptions.Compiled);

        /// <summary>本地正则判断一行文字是否敏感（用于补足模型漏判），支持传入自定义敏感词与白名单规则。</summary>
        public static bool LooksSensitive(string text, string kind, out string reason)
        {
            return LooksSensitive(text, kind, null, out reason);
        }

        public static bool LooksSensitive(string text, string kind, PrivacyRules customRules, out string reason)
        {
            reason = "";
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (customRules != null)
            {
                return customRules.IsSensitive(text, out reason);
            }
            text = text.Normalize(NormalizationForm.FormKC);
            string compact = Regex.Replace(text, @"[\s\u200B\u200C\u200D\uFEFF‐‑–—\-()（）]", "");
            string k = (kind ?? "").Trim().ToLowerInvariant();
            if (k == "name" || k == "phone" || k == "id" || k == "card" || k == "email" ||
                k == "address" || k == "account" || k == "code" || k == "qq" || k == "wechat")
            {
                reason = "模型标记类型：" + k;
                return true;
            }
            if (PhoneRegex.IsMatch(compact)) { reason = "手机号"; return true; }
            if (LongNumberRegex.IsMatch(compact)) { reason = "身份证/银行卡"; return true; }
            if (EmailRegex.IsMatch(compact)) { reason = "邮箱"; return true; }
            if (CodeRegex.IsMatch(compact)) { reason = "验证码"; return true; }
            if (SecretHintRegex.IsMatch(text)) { reason = "密钥或口令"; return true; }
            if (AccountRegex.IsMatch(text)) { reason = "账号"; return true; }
            if (AddressRegex.IsMatch(text) && text.Length >= 8) { reason = "地址"; return true; }
            if (NameHintRegex.IsMatch(text)) { reason = "姓名"; return true; }
            return false;
        }

        /// <summary>
        /// 生成需要打码的屏幕矩形（选区内 + 人脸外扩）。
        /// analysis 坐标均为相对图片的 0~1；selection 为屏幕坐标系中的选区。
        /// </summary>
        public static List<RectangleF> BuildMaskRects(Bitmap source, PrivacyAnalysis analysis, Rectangle selection, float facePadRatio, out int lineCount, out int faceCount)
        {
            return BuildMaskRects(source, analysis, selection, facePadRatio, null, out lineCount, out faceCount);
        }

        public static List<RectangleF> BuildMaskRects(Bitmap source, PrivacyAnalysis analysis, Rectangle selection, float facePadRatio, PrivacyRules customRules, out int lineCount, out int faceCount)
        {
            lineCount = 0;
            faceCount = 0;
            List<RectangleF> rects = new List<RectangleF>();
            if (selection.Width <= 0 || selection.Height <= 0) return rects;
            if (analysis == null) return rects;

            if (analysis.Lines != null)
            {
                // 先用本地行检测得到精确文字行，再把 AI 的敏感行映射上去（AI bbox 常有漂移）
                List<TextLineDetector.TextLine> detected = source != null && !analysis.HasLocalCoordinates ? TextLineDetector.Detect(source, selection) : new List<TextLineDetector.TextLine>();
                RectangleF[] mapped = TextLineDetector.MapBlocks(detected, analysis.Lines, selection);
                for (int i = 0; i < analysis.Lines.Count; i++)
                {
                    ImageTextBlock line = analysis.Lines[i];
                    if (line == null) continue;
                    string reason;
                    bool sensitive = line.Sensitive || LooksSensitive(line.Text, line.Kind, customRules, out reason);
                    if (!sensitive) continue;
                    RectangleF rect;
                    if (analysis.HasLocalCoordinates && line.HasRect)
                    {
                        rect = ToScreenRect(line.X, line.Y, line.W, line.H, selection);
                    }
                    else if (i < mapped.Length && mapped[i] != RectangleF.Empty)
                    {
                        rect = mapped[i];
                    }
                    else if (line.HasRect)
                    {
                        rect = ToScreenRect(line.X, line.Y, line.W, line.H, selection);
                        if (source != null) rect = InkRect.Refine(source, rect, selection, 0.75f);
                    }
                    else continue;
                    float growY = Math.Max(3f, rect.Height * 0.10f);
                    rect = new RectangleF(rect.X - 4f, rect.Y - growY * 0.3f, rect.Width + 8f, rect.Height + growY);
                    rect = RectangleF.Intersect(rect, selection);
                    if (rect.Width < 1 || rect.Height < 1) continue;
                    rects.Add(rect);
                    lineCount++;
                }
            }
            if (analysis.Faces != null)
            {
                foreach (FaceBox face in analysis.Faces)
                {
                    if (face == null || float.IsNaN(face.X) || float.IsNaN(face.Y) ||
                        float.IsNaN(face.W) || float.IsNaN(face.H) || float.IsInfinity(face.X) ||
                        float.IsInfinity(face.Y) || float.IsInfinity(face.W) || float.IsInfinity(face.H) ||
                        face.W * selection.Width < 1f || face.H * selection.Height < 1f) continue;
                    RectangleF rect = ToScreenRect(face.X, face.Y, face.W, face.H, selection);
                    float padX = rect.Width * Math.Max(0.05f, facePadRatio);
                    float padY = rect.Height * Math.Max(0.05f, facePadRatio);
                    rect.Inflate(padX, padY);
                    rect = RectangleF.Intersect(rect, selection);
                    if (rect.Width < 1f || rect.Height < 1f) continue;
                    rects.Add(rect);
                    faceCount++;
                }
            }
            return rects;
        }

        /// <summary>
        /// 用截图像素把 AI 给的文字框精修到实际笔画范围：
        /// 模型返回的常是“行框”（偏上/偏大/偏短），直接覆盖会露出原文字或打码不全。
        /// 纵向按墨迹行精修；横向只在附近按墨迹延伸（避免把相邻头像/图标算进来）。
        /// </summary>
        public static class InkRect
        {
            public static RectangleF Refine(Bitmap source, RectangleF approx, Rectangle clip, float verticalExpandRatio)
            {
                if (source == null || approx.Width < 2f || approx.Height < 2f) return approx;
                float h = Math.Max(4f, approx.Height);
                int xPad = (int)Math.Round(Math.Max(6f, h * 0.3f));
                int yTop = (int)Math.Floor(approx.Top - h * Math.Max(0.5f, verticalExpandRatio));
                int yBottom = (int)Math.Ceiling(approx.Bottom + h * Math.Max(0.5f, verticalExpandRatio));
                int xScanLeft = (int)Math.Floor(approx.Left - xPad);
                int xScanRight = (int)Math.Ceiling(approx.Right + xPad);
                Rectangle vertSearch = Rectangle.FromLTRB(
                    Math.Max(clip.Left, xScanLeft), Math.Max(clip.Top, yTop),
                    Math.Min(clip.Right, xScanRight), Math.Min(clip.Bottom, yBottom));
                vertSearch.Intersect(new Rectangle(0, 0, source.Width, source.Height));
                if (vertSearch.Width < 4 || vertSearch.Height < 4) return Inflate(approx, 4f, h * 0.2f, clip);

                Color background = SampleBorderColor(source, vertSearch);
                int threshold = 44;
                int minInk = Math.Max(2, vertSearch.Width / 70);
                int firstRow = -1, lastRow = -1;
                try
                {
                    for (int y = vertSearch.Top; y < vertSearch.Bottom; y++)
                    {
                        int count = 0;
                        for (int x = vertSearch.Left; x < vertSearch.Right; x++)
                        {
                            Color c = source.GetPixel(x, y);
                            int d = Math.Abs(c.R - background.R) + Math.Abs(c.G - background.G) + Math.Abs(c.B - background.B);
                            if (d > threshold) count++;
                        }
                        if (count >= minInk)
                        {
                            if (firstRow < 0) firstRow = y;
                            lastRow = y;
                        }
                    }
                }
                catch { }
                if (firstRow < 0 || lastRow <= firstRow)
                    return Inflate(approx, 4f, h * 0.22f, clip);

                // 横向：在墨迹行带内，以近似框为起点向左右延伸，允许小间隙（字间/词间空格）
                int minX = int.MaxValue, maxX = int.MinValue;
                int rowCenter = (firstRow + lastRow) / 2;
                try
                {
                    int leftLimit = Math.Max(clip.Left, (int)Math.Floor(approx.Left - Math.Max(22f, approx.Width * 0.12f)));
                    int rightLimit = Math.Min(clip.Right, (int)Math.Ceiling(approx.Right + Math.Max(40f, approx.Width * 0.5f)));
                    for (int x = leftLimit; x < rightLimit; x++)
                    {
                        if (HasInkColumn(source, x, firstRow, lastRow, background, threshold))
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                        }
                    }
                    if (minX == int.MaxValue)
                    {
                        minX = (int)approx.Left;
                        maxX = (int)approx.Right;
                    }
                    else
                    {
                        // 裁剪掉左右两侧过长的空白：从左/右向内收，直到遇到第一列有墨迹（容差 18px）
                        int l = minX;
                        while (l < maxX - 2 && !HasInkColumn(source, l, firstRow, lastRow, background, threshold)) l++;
                        int r = maxX;
                        while (r > l + 2 && !HasInkColumn(source, r, firstRow, lastRow, background, threshold)) r--;
                        minX = Math.Max(minX - 2, l - 2);
                        maxX = Math.Min(maxX + 2, r + 2);
                    }
                }
                catch { minX = (int)approx.Left; maxX = (int)approx.Right; }

                RectangleF refined = Rectangle.FromLTRB(minX - 2, firstRow - 1, maxX + 3, lastRow + 2);
                // 合理性检查：精修结果不应与近似框差太多，否则回退到“上下扩一点”
                if (refined.Height < h * 0.4f || refined.Height > h * 2.2f || refined.Width < approx.Width * 0.35f)
                    return Inflate(approx, 4f, h * 0.22f, clip);
                return refined;
            }

            private static bool HasInkColumn(Bitmap source, int x, int rowTop, int rowBottom, Color background, int threshold)
            {
                int count = 0;
                for (int y = rowTop; y <= rowBottom; y++)
                {
                    Color c = source.GetPixel(x, y);
                    int d = Math.Abs(c.R - background.R) + Math.Abs(c.G - background.G) + Math.Abs(c.B - background.B);
                    if (d > threshold) count++;
                }
                return count >= Math.Max(1, (rowBottom - rowTop + 1) / 6);
            }

            private static RectangleF Inflate(RectangleF rect, float x, float y, Rectangle clip)
            {
                rect.Inflate(x, y);
                if (rect.Left < clip.Left) rect.Width -= clip.Left - rect.Left;
                if (rect.Top < clip.Top) { rect.Height -= clip.Top - rect.Top; rect.Y = clip.Top; }
                if (rect.Right > clip.Right) rect.Width = clip.Right - rect.Left;
                if (rect.Bottom > clip.Bottom) rect.Height = clip.Bottom - rect.Top;
                if (rect.Width < 1f) rect.Width = 1f;
                if (rect.Height < 1f) rect.Height = 1f;
                return rect;
            }

            private static Color SampleBorderColor(Bitmap source, Rectangle rect)
            {
                List<int> rs = new List<int>(); List<int> gs = new List<int>(); List<int> bs = new List<int>();
                for (int x = rect.Left; x < rect.Right; x += Math.Max(1, rect.Width / 24))
                {
                    Add(rs, gs, bs, source, x, rect.Top);
                    Add(rs, gs, bs, source, x, rect.Bottom - 1);
                }
                for (int y = rect.Top; y < rect.Bottom; y += Math.Max(1, rect.Height / 12))
                {
                    Add(rs, gs, bs, source, rect.Left, y);
                    Add(rs, gs, bs, source, rect.Right - 1, y);
                }
                if (rs.Count == 0) return Color.White;
                rs.Sort(); gs.Sort(); bs.Sort();
                int m = rs.Count / 2;
                return Color.FromArgb(rs[m], gs[m], bs[m]);
            }

            private static void Add(List<int> rs, List<int> gs, List<int> bs, Bitmap source, int x, int y)
            {
                if (x < 0 || y < 0 || x >= source.Width || y >= source.Height) return;
                Color c = source.GetPixel(x, y);
                rs.Add(c.R); gs.Add(c.G); bs.Add(c.B);
            }
        }

        public static RectangleF ToScreenRect(float x, float y, float w, float h, Rectangle selection)
        {
            return new RectangleF(
                selection.Left + x * selection.Width,
                selection.Top + y * selection.Height,
                Math.Max(1f, w * selection.Width),
                Math.Max(1f, h * selection.Height));
        }

        /// <summary>把一组矩形合并成一个遮罩路径（同一笔撤消）。</summary>
        public static System.Drawing.Drawing2D.GraphicsPath BuildMaskPath(IList<RectangleF> rects)
        {
            System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
            if (rects == null) return path;
            foreach (RectangleF rect in rects)
            {
                RectangleF r = rect;
                if (r.Width < 1f || r.Height < 1f) continue;
                path.AddRectangle(r);
            }
            return path;
        }
    }
}
