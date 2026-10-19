from PIL import Image
im = Image.open(r'D:\Agents\微信截图助手\tests\overlay3.png').convert('RGB')
for y in (888, 870):
    row = [im.getpixel((x,y)) for x in range(300, 1500)]
    white = [i+300 for i,p in enumerate(row) if p[0]>235 and p[1]>235 and p[2]>235]
    if white: print('row', y, min(white), max(white), 'w=', max(white)-min(white)+1)
# 找垂直方向
col=[]
for y in range(830, 960):
    p = im.getpixel((800,y))
    if p[0]>235 and p[1]>235 and p[2]>235: col.append(y)
print('vertical at x=800:', min(col), max(col))
