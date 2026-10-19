# -*- coding: utf-8 -*-
"""生成程序图标 assets/app.ico（多尺寸、透明背景）。

图标设计：
  - 微信绿（#07C160）圆角方形底，自上而下由亮绿到深绿渐变
  - 白色截图取景框（圆角矩形）+ 四角白色选中块，代表“截图”
  - 右下角白色四角星芒（AI 元素），代表 DeepSeek 文字提取 / 翻译 / 智能打码

用法（CLI 保持不变）：
    python tools/make_icon.py
输出：
    assets/app.ico
"""
import math
import os
import sys

try:
    from PIL import Image, ImageDraw
except ImportError:  # pragma: no cover
    print("需要 Pillow: python -m pip install pillow", file=sys.stderr)
    sys.exit(1)

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "assets", "app.ico")

# 微信绿渐变（上亮下深）
TOP_GREEN = (22, 210, 116, 255)     # 约 #16D274
BOTTOM_GREEN = (3, 155, 80, 255)    # 约 #039B50
WHITE = (255, 255, 255, 255)

SIZES = [(16, 16), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]


def _lerp_color(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3)) + (255,)


def _star_points(cx, cy, r_out, r_in):
    """四角星芒（8 个顶点，长尖朝 上/右/下/左）。"""
    pts = []
    for i in range(8):
        ang = -math.pi / 2 + i * math.pi / 4          # 从正上方开始
        r = r_out if i % 2 == 0 else r_in
        pts.append((cx + r * math.cos(ang), cy + r * math.sin(ang)))
    return pts


def draw(size):
    """绘制单个尺寸的图标（先按 ss 倍超采样，再缩回目标尺寸，保证边缘平滑）。"""
    ss = 8 if size <= 64 else 4
    s = size * ss
    scale = float(s) / 256.0       # 以 256 为设计基准

    # ---- 1. 渐变圆角方形底 ----
    grad = Image.new("RGBA", (1, s), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grad)
    for y in range(s):
        gd.point((0, y), fill=_lerp_color(TOP_GREEN, BOTTOM_GREEN, y / float(max(1, s - 1))))
    grad = grad.resize((s, s), Image.NEAREST)

    margin = max(1, int(round(s * 0.018)))
    radius = max(2, int(round(s * 0.225)))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [margin, margin, s - 1 - margin, s - 1 - margin], radius=radius, fill=255
    )
    icon = Image.composite(grad, Image.new("RGBA", (s, s), (0, 0, 0, 0)), mask)

    # ---- 2. 顶部柔和高光（增加质感，不改变主色） ----
    gloss = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    gloss_draw = ImageDraw.Draw(gloss)
    gloss_h = int(s * 0.52)
    for y in range(gloss_h):
        alpha = int(round(42 * (1.0 - y / float(gloss_h))))
        if alpha > 0:
            gloss_draw.line([(0, y), (s, y)], fill=(255, 255, 255, alpha))
    gloss = Image.composite(gloss, Image.new("RGBA", (s, s), (0, 0, 0, 0)), mask)
    icon = Image.alpha_composite(icon, gloss)

    d = ImageDraw.Draw(icon)

    # ---- 3. 白色截图取景框 ----
    fx0, fy0 = 0.165 * s, 0.205 * s
    fx1, fy1 = 0.625 * s, 0.565 * s
    frame_w = max(1.75 * ss, 0.048 * s)
    frame_r = max(1.0 * ss, 0.06 * s)
    d.rounded_rectangle([fx0, fy0, fx1, fy1], radius=frame_r, outline=WHITE, width=int(round(frame_w)))

    # 四角选中块（24px 以下画了会糊成一团，所以 32px 起才画）
    if size >= 32:
        hs = max(1.4 * ss, 0.062 * s)          # 半边长
        for cx, cy in ((fx0, fy0), (fx1, fy0), (fx0, fy1), (fx1, fy1)):
            d.rectangle([cx - hs, cy - hs, cx + hs, cy + hs], fill=WHITE)

    # ---- 4. 右下角 AI 星芒 ----
    scx, scy = 0.725 * s, 0.725 * s
    r_out = 0.19 * s
    r_in = r_out * 0.28
    d.polygon(_star_points(scx, scy, r_out, r_in), fill=WHITE)

    # 大尺寸额外加一颗小星芒，点缀“AI”气质（小尺寸画了会脏）
    if size >= 48:
        d.polygon(_star_points(0.855 * s, 0.79 * s, 0.07 * s, 0.02 * s), fill=WHITE)

    # 缩回目标尺寸（LANCZOS 产生平滑的抗锯齿边缘）
    return icon.resize((size, size), Image.LANCZOS)


def main():
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    frames = [draw(w) for w, _ in SIZES]

    # ICO：Pillow 会对每个尺寸使用尺寸完全一致的帧
    base = frames[-1]                     # 256x256 作为主图
    base.save(OUT, format="ICO", sizes=SIZES, append_images=frames[:-1])

    # ---- 生成后自检 ----
    if not os.path.isfile(OUT) or os.path.getsize(OUT) == 0:
        print("图标生成失败: 文件不存在或为空", file=sys.stderr)
        sys.exit(1)
    with Image.open(OUT) as ico:
        available = set(ico.ico.sizes())
    expected = set(SIZES)
    missing = sorted(expected - available)
    if missing:
        print("图标缺少尺寸: %s" % ", ".join("%dx%d" % s for s in missing), file=sys.stderr)
        sys.exit(1)

    print("图标已生成:", OUT, os.path.getsize(OUT), "bytes")
    print("包含尺寸:", ", ".join("%dx%d" % s for s in SIZES))


if __name__ == "__main__":
    main()
