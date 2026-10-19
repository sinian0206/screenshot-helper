p = r'D:\Agents\微信截图助手\VERIFICATION.md'
s = open(p, encoding='utf-8').read()
s = s.replace('- 最新 exe：`build_lead\\微信截图助手.exe`，286,208 字节（279.5 KB），SHA256 `8553583A7613A3C95943B8AF17F3650E39B86982DE639F27DB23F9ADECD742D6`',
'''- 最新 exe：`dist\\微信截图助手.exe`，286,208 字节（279.5 KB），SHA256 `8553583A7613A3C95943B8AF17F3650E39B86982DE639F27DB23F9ADECD742D6`
- 绿色版压缩包：`dist\\微信截图助手_v1.0.0_绿色版.zip`，183,185 字节，SHA256 `776497C15BDB6E0A2714A84287316BE1D98E28AEFDCC78026A79790105BE38B5`
- zip 内容（4/4 解压 SHA256 一致）：微信截图助手.exe + 使用说明.txt + 功能清单.txt + config.example.json''')
open(p, 'w', encoding='utf-8').write(s)
print('verification updated')
