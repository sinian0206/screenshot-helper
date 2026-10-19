from PIL import Image
im = Image.open(r'D:\Agents\微信截图助手\tests\overlay3.png').convert('RGB')
box = (520, 840, 1300, 940)
tb = im.crop(box)
tb = tb.resize((tb.width*2, tb.height*2), Image.LANCZOS)
tb.save(r'D:\Agents\微信截图助手\tests\toolbar3_zoom.png')
# 选区左上角尺寸标签
lbl = im.crop((270, 270, 480, 320)); lbl = lbl.resize((lbl.width*3, lbl.height*3), Image.LANCZOS)
lbl.save(r'D:\Agents\微信截图助手\tests\size_label_zoom.png')
print('ok')
