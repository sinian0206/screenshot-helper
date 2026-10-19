from PIL import Image
screen = Image.open(r'D:\Agents\微信截图助手\tests\real3\3_selection.png').convert('RGB')
clip = Image.open(r'D:\Agents\微信截图助手\tests\real3\3_clipboard.png').convert('RGB')
ox, oy = 360, 260
regions = [(0.10,0.20,0.18,0.12), (0.55,0.35,0.22,0.10)]
for (rx, ry, rw, rh) in regions:
    x = int(rx*840); y = int(ry*500); w = max(1,int(rw*840)); h = max(1,int(rh*500))
    total = 0; n = 0; maxd = 0
    for j in range(y, min(y+h, 500), 2):
        for i in range(x, min(x+w, 840), 2):
            a = clip.getpixel((i,j))
            b = screen.getpixel((i+ox, j+oy))
            d = abs(a[0]-b[0]) + abs(a[1]-b[1]) + abs(a[2]-b[2])
            total += d; n += 1
            if d > maxd: maxd = d
    print(f'region x={x} y={y} w={w} h={h}: avg_diff={total/max(n,1):.1f} max={maxd}')
# 输出放大裁剪便于肉眼检查
crop = clip.crop((60, 80, 280, 180)).resize((880, 400), Image.NEAREST)
crop.save(r'D:\Agents\微信截图助手\tests\real3\3_clip_mosaic_zoom.png')
crop2 = clip.crop((430, 160, 680, 240)).resize((1000, 320), Image.NEAREST)
crop2.save(r'D:\Agents\微信截图助手\tests\real3\3_clip_mosaic_zoom2.png')
