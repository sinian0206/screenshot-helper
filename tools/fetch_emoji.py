# -*- coding: utf-8 -*-
"""一次性下载 Twemoji 72x72 PNG 作为表情贴纸资源。

用法：python tools/fetch_emoji.py
输出：assets/emoji/<码点>.png（40 个常用表情），并用 Pillow 校验尺寸/透明度。
资源来源：https://github.com/twitter/twemoji （图形 CC-BY 4.0）
"""
import os
import sys
import time
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(ROOT, "assets", "emoji")

# (码点文件名, 中文名)
EMOJIS = [
    ("1f600", "微笑"),
    ("1f602", "笑哭"),
    ("1f60a", "害羞"),
    ("1f60d", "花痴"),
    ("1f61c", "调皮"),
    ("1f914", "思考"),
    ("1f644", "无语"),
    ("1f62d", "大哭"),
    ("1f621", "生气"),
    ("1f631", "惊恐"),
    ("1f633", "脸红"),
    ("1f60e", "酷"),
    ("1f929", "星星眼"),
    ("1f970", "爱心眼"),
    ("1f44d", "赞"),
    ("1f44e", "踩"),
    ("1f44c", "OK"),
    ("1f64f", "抱拳"),
    ("1f4aa", "肌肉"),
    ("1f91d", "握手"),
    ("1f525", "火"),
    ("2b50", "星"),
    ("2764", "红心"),
    ("1f389", "礼花"),
    ("1f381", "礼物"),
    ("1f339", "玫瑰"),
    ("2615", "咖啡"),
    ("1f436", "狗"),
    ("1f431", "猫"),
    ("1f4a9", "便便"),
    ("1f680", "火箭"),
    ("1f4a1", "灯泡"),
    ("2705", "对勾"),
    ("274c", "叉"),
    ("2757", "感叹"),
    ("2753", "问号"),
    ("1f4cc", "图钉"),
    ("1f4f7", "相机"),
    ("1f3af", "靶心"),
    ("1f4b0", "钱"),
]

URLS = [
    "https://cdn.jsdelivr.net/gh/twitter/twemoji@14.0.2/assets/72x72/{code}.png",
    "https://raw.githubusercontent.com/twitter/twemoji/v14.0.2/assets/72x72/{code}.png",
    "https://raw.githubusercontent.com/twitter/twemoji/master/assets/72x72/{code}.png",
]


def download(code: str) -> bytes:
    last = None
    for url in URLS:
        u = url.format(code=code)
        try:
            req = urllib.request.Request(u, headers={"User-Agent": "Mozilla/5.0 (xiaojie fetch)"})
            with urllib.request.urlopen(req, timeout=25) as resp:
                data = resp.read()
            if data[:8] == b"\x89PNG\r\n\x1a\n":
                return data
            last = "not png: " + u
        except Exception as exc:  # noqa: BLE001
            last = "%s: %s" % (u, exc)
    raise RuntimeError("全部下载源失败 " + code + " -> " + str(last))


def main() -> int:
    os.makedirs(OUT_DIR, exist_ok=True)
    try:
        from PIL import Image  # type: ignore
    except Exception:
        Image = None

    ok = 0
    fail = []
    total = 0
    for code, label in EMOJIS:
        path = os.path.join(OUT_DIR, code + ".png")
        try:
            data = download(code)
            with open(path, "wb") as f:
                f.write(data)
            total += len(data)
            if Image is not None:
                with Image.open(path) as im:
                    im.load()
                    if im.size != (72, 72):
                        raise RuntimeError("尺寸异常 %s" % (im.size,))
                    if im.mode not in ("RGBA", "P"):
                        raise RuntimeError("非透明 PNG: %s" % im.mode)
            ok += 1
            print("OK   %-8s %-6s %5d bytes" % (code, label, len(data)))
        except Exception as exc:  # noqa: BLE001
            fail.append((code, str(exc)))
            print("FAIL %-8s %s" % (code, exc))
        time.sleep(0.05)

    print("-" * 60)
    print("成功 %d / %d，总大小 %.1f KB" % (ok, len(EMOJIS), total / 1024.0))
    if fail:
        for code, err in fail:
            print("失败:", code, err)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
