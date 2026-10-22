using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace XiaoJie
{
    public sealed class HistoryItem
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        public string ImageFile = "";
        public string ThumbFile = "";
        public int Width;
        public int Height;
        public bool IsFavorite;
        public string OcrText = "";
        public string Type = "screenshot"; // screenshot / recording
    }

    public static class HistoryManager
    {
        private static readonly object _sync = new object();
        private static List<HistoryItem> _items;

        private static string GetHistoryDir(ConfigStore store)
        {
            string dir = Path.Combine(store.DataDir, "history");
            if (!Directory.Exists(dir))
            {
                try { Directory.CreateDirectory(dir); } catch { }
            }
            return dir;
        }

        private static string GetIndexFile(ConfigStore store)
        {
            return Path.Combine(GetHistoryDir(store), "history.json");
        }

        public static List<HistoryItem> GetItems(ConfigStore store)
        {
            lock (_sync)
            {
                if (_items != null) return OrderedCopy();
                _items = new List<HistoryItem>();
                string index = GetIndexFile(store);
                if (File.Exists(index))
                {
                    try
                    {
                        string json = File.ReadAllText(index, Encoding.UTF8);
                        var ser = new JavaScriptSerializer();
                        var list = ser.Deserialize<List<HistoryItem>>(json);
                        if (list != null) _items = list;
                    }
                    catch { }
                }
                return OrderedCopy();
            }
        }

        private static List<HistoryItem> OrderedCopy()
        {
            List<HistoryItem> ordered = new List<HistoryItem>(_items);
            ordered.Sort(delegate(HistoryItem left, HistoryItem right)
            {
                int favorite = right.IsFavorite.CompareTo(left.IsFavorite);
                if (favorite != 0) return favorite;
                return string.CompareOrdinal(right.Timestamp, left.Timestamp);
            });
            return ordered;
        }

        public static void SaveItem(ConfigStore store, AppConfig cfg, Bitmap image, string ocrText = "")
        {
            if (cfg == null || !cfg.HistoryEnabled || image == null) return;
            try
            {
                string dir = GetHistoryDir(store);
                string baseName = "cap_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                string imgFile = Path.Combine(dir, baseName + ".png");
                string thumbFile = Path.Combine(dir, baseName + "_thumb.jpg");

                using (Bitmap copy = new Bitmap(image))
                {
                    copy.Save(imgFile, ImageFormat.Png);

                    // 生成缩略图 (最大 240x160)
                    float ratio = Math.Min(240f / copy.Width, 160f / copy.Height);
                    int tw = Math.Max(1, (int)(copy.Width * ratio));
                    int th = Math.Max(1, (int)(copy.Height * ratio));
                    using (Bitmap thumb = new Bitmap(tw, th))
                    {
                        using (Graphics g = Graphics.FromImage(thumb))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(copy, 0, 0, tw, th);
                        }
                        thumb.Save(thumbFile, ImageFormat.Jpeg);
                    }
                }

                lock (_sync)
                {
                    if (_items == null) GetItems(store);
                    var item = new HistoryItem
                    {
                        ImageFile = imgFile,
                        ThumbFile = thumbFile,
                        Width = image.Width,
                        Height = image.Height,
                        OcrText = ocrText ?? ""
                    };
                    _items.Insert(0, item);
                    Prune(store, cfg);
                    SaveIndex(store);
                }
            }
            catch (Exception ex)
            {
                Log.Write("保存历史记录异常: " + ex.Message);
            }
        }

        public static void DeleteItem(ConfigStore store, string id)
        {
            lock (_sync)
            {
                if (_items == null) GetItems(store);
                int idx = _items.FindIndex(i => i.Id == id);
                if (idx >= 0)
                {
                    var item = _items[idx];
                    _items.RemoveAt(idx);
                    try { if (File.Exists(item.ImageFile)) File.Delete(item.ImageFile); } catch { }
                    try { if (File.Exists(item.ThumbFile)) File.Delete(item.ThumbFile); } catch { }
                    SaveIndex(store);
                }
            }
        }

        public static void ToggleFavorite(ConfigStore store, string id)
        {
            lock (_sync)
            {
                if (_items == null) GetItems(store);
                var item = _items.Find(i => i.Id == id);
                if (item != null)
                {
                    item.IsFavorite = !item.IsFavorite;
                    SaveIndex(store);
                }
            }
        }

        public static void ClearAll(ConfigStore store)
        {
            lock (_sync)
            {
                if (_items == null) GetItems(store);
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    if (!_items[i].IsFavorite)
                    {
                        try { if (File.Exists(_items[i].ImageFile)) File.Delete(_items[i].ImageFile); } catch { }
                        try { if (File.Exists(_items[i].ThumbFile)) File.Delete(_items[i].ThumbFile); } catch { }
                        _items.RemoveAt(i);
                    }
                }
                SaveIndex(store);
            }
        }

        private static void Prune(ConfigStore store, AppConfig cfg)
        {
            int maxCount = cfg.HistoryMaxCount > 0 ? cfg.HistoryMaxCount : 100;
            while (_items.Count > maxCount)
            {
                int removeIdx = _items.FindLastIndex(i => !i.IsFavorite);
                if (removeIdx < 0) break;
                var item = _items[removeIdx];
                _items.RemoveAt(removeIdx);
                try { if (File.Exists(item.ImageFile)) File.Delete(item.ImageFile); } catch { }
                try { if (File.Exists(item.ThumbFile)) File.Delete(item.ThumbFile); } catch { }
            }
            long maxBytes = Math.Max(1, cfg.HistoryMaxMB) * 1024L * 1024L;
            long total = GetTotalBytes();
            while (total > maxBytes)
            {
                int removeIdx = _items.FindLastIndex(i => !i.IsFavorite);
                if (removeIdx < 0) break;
                HistoryItem item = _items[removeIdx];
                long removedBytes = GetFileBytes(item.ImageFile) + GetFileBytes(item.ThumbFile);
                _items.RemoveAt(removeIdx);
                try { if (File.Exists(item.ImageFile)) File.Delete(item.ImageFile); } catch { }
                try { if (File.Exists(item.ThumbFile)) File.Delete(item.ThumbFile); } catch { }
                total -= removedBytes;
            }
        }

        private static long GetTotalBytes()
        {
            long total = 0;
            foreach (HistoryItem item in _items)
                total += GetFileBytes(item.ImageFile) + GetFileBytes(item.ThumbFile);
            return total;
        }

        private static long GetFileBytes(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; }
        }

        private static void SaveIndex(ConfigStore store)
        {
            try
            {
                string json = new JavaScriptSerializer().Serialize(_items);
                File.WriteAllText(GetIndexFile(store), json, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
