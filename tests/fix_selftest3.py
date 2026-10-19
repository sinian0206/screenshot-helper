path = r'D:\Agents\微信截图助手\src\SelfTest.cs'
src = open(path, encoding='utf-8').read()
marker = "        }\n\n    }\n\n        // ============================ DeepSeek API mock 集成测试"
assert marker in src, 'marker missing'
src = src.replace(marker, "        }\n\n        // ============================ DeepSeek API mock 集成测试", 1)
# 确保结尾结构：方法结束 + 类结束 + 命名空间结束
src = src.rstrip()
if not src.endswith('    }\n}'):
    # 去掉多余尾部再补
    while src.endswith('}') and not src.endswith('    }\n}'):
        src = src[:-1].rstrip()
    src = src + '\n    }\n}'
open(path, 'w', encoding='utf-8').write(src)
print('tail:', repr(src[-80:]))
