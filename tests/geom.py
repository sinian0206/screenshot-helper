from PIL import Image
im = Image.open(r'D:\Agents\微信截图助手\tests\overlay3.png').convert('RGB')
# find white toolbar pill row
for y in range(860, 930):
    row = [im.getpixel((x,y)) for x in range(400, 1400)]
    white = [i+400 for i,p in enumerate(row) if p[0]>240 and p[1]>240 and p[2]>240]
    if len(white) > 300:
        print('row', y, 'white from', min(white), 'to', max(white), 'count', len(white))
        break
# scan vertical extent at x=600
col = []
for y in range(840, 950):
    p = im.getpixel((600,y))
    if p[0]>240 and p[1]>240 and p[2]>240: col.append(y)
print('pill vertical:', min(col), max(col))
