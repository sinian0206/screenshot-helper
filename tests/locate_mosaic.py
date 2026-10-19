from PIL import Image
screen = Image.open(r'D:\Agents\微信截图助手\tests\real3\3_selection.png').convert('RGB')
clip = Image.open(r'D:\Agents\微信截图助手\tests\real3\3_clipboard.png').convert('RGB')
ox, oy = 360, 260
diffs = []
for j in range(0, 500, 2):
    for i in range(0, 840, 2):
        a = clip.getpixel((i,j)); b = screen.getpixel((i+ox, j+oy))
        d = abs(a[0]-b[0])+abs(a[1]-b[1])+abs(a[2]-b[2])
        if d > 12: diffs.append((i,j,d))
print('changed sample pixels:', len(diffs))
if diffs:
    xs=[p[0] for p in diffs]; ys=[p[1] for p in diffs]
    print('bbox', min(xs), min(ys), max(xs), max(ys))
    print('top diffs', sorted(diffs, key=lambda t:-t[2])[:10])
# 再检查 3_smart_mosaic 全屏图中选区内部是否出现块状马赛克（对比截取）
full = Image.open(r'D:\Agents\微信截图助手\tests\real3\3_smart_mosaic.png').convert('RGB')
crop_sel = full.crop((360,260,1200,760))
diff2 = 0
for j in range(0,500,10):
    for i in range(0,840,10):
        a=crop_sel.getpixel((i,j)); b=screen.getpixel((i+ox,j+oy))
        diff2 += abs(a[0]-b[0])+abs(a[1]-b[1])+abs(a[2]-b[2])
print('selected-in-screen vs selection png diff avg:', diff2/(50*84))
