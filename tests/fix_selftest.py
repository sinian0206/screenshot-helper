path = r'D:\Agents\微信截图助手\src\SelfTest.cs'
src = open(path, encoding='utf-8').read()
src = src.replace("        }\n    }\n\n        // ============================ 离屏 UI", "        }\n\n        // ============================ 离屏 UI", 1)
src = src.rstrip()
assert src.endswith("}"), src[-50:]
src = src[:-1] + "    }\n}\n"
open(path, 'w', encoding='utf-8').write(src)
print('fixed')
