# -*- coding: utf-8 -*-
"""生成程序图标 assets/app.ico（多尺寸）。
初版占位图标：绿色圆角底 + 白色截图框 + 选中角标。构建/文档负责人可继续美化。
"""
import os
import sys

try:
    from PIL import Image, ImageDraw
except ImportError:
    print("需要 Pillow: python -m pip install pillow", file=sys.stderr)
    sys.exit(1)

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "assets", "app.ico")

GREEN = (7, 193, 96, 255)
DARKGREEN = (5, 160, 78, 255)
WHITE = (255, 255, 255, 255)


def draw(size):
    ss = 8  # supersampling
    im = Image.new("RGBA", (size * ss, size * ss), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    s = size * ss
    pad = s * 0.045
    # 圆角方形底 + 轻微渐变（上亮下暗）
    d.rounded_rectangle([pad, pad, s - pad, s - pad], radius=s * 0.23, fill=GREEN)
    d.rounded_rectangle([pad, s * 0.5, s - pad, s - pad], radius=s * 0.23, fill=(6, 180, 89, 255))
    d.rounded_rectangle([pad, pad, s - pad, s - pad], radius=s * 0.23, fill=GREEN)
    # 截图取景框
    x0, y0, x1, y1 = s * 0.24, s * 0.28, s * 0.76, s * 0.70
    w = int(max(2, s * 0.045))
    d.rectangle([x0, y0, x1, y1], outline=WHITE, width=w)
    # 四个角上的选中方块
    hs = s * 0.075
    for cx, cy in [(x0, y0), (x1, y0), (x0, y1), (x1, y1)]:
        d.rectangle([cx - hs / 2, cy - hs / 2, cx + hs / 2, cy + hs / 2], fill=WHITE)
    # 右下角 AI 星芒（DeepSeek 元素）
    cx, cy = s * 0.73, s * 0.74
    r1, r2 = s * 0.135, s * 0.042
    pts = []
    import math
    for i in range(8):
        ang = math.pi / 4 * i - math.pi / 2
        r = r1 if i % 2 == 0 else r2
        pts.append((cx + r * math.cos(ang), cy + r * math.sin(ang)))
    d.polygon(pts, fill=WHITE)
    # 缩回目标尺寸
    im = im.resize((size, size), Image.LANCZOS)
    return im


def main():
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
    frames = [draw(w) for w, _ in sizes]
    frames[-1].save(OUT, format="ICO", sizes=sizes, append_images=frames[:-1])
    print("图标已生成:", OUT, os.path.getsize(OUT), "bytes")


if __name__ == "__main__":
    main()
