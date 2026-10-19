from PIL import Image
im = Image.open(r'D:\Agents\微信截图助手\tests\real2\c_pin.png').convert('RGB')
crop = im.crop((330, 230, 1230, 800))
crop = crop.resize((crop.width*2, crop.height*2), Image.LANCZOS)
crop.save(r'D:\Agents\微信截图助手\tests\real2\c_pin_zoom.png')
print('ok')
