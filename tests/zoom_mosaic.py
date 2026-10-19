from PIL import Image
im = Image.open(r'D:\Agents\微信截图助手\tests\real3\3_smart_mosaic.png').convert('RGB')
# region1 screen (444,360)-(595,420); region2 (822,435)-(1006,485)
for idx, box in enumerate([(430,340,620,440),(800,420,1030,510)], 1):
    c = im.crop(box); c = c.resize((c.width*4, c.height*4), Image.NEAREST); c.save(fr'D:\Agents\微信截图助手\tests\real3\mosaic_region{idx}_zoom.png')
    print('saved', idx, box)
