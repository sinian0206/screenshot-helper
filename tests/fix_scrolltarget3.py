p = r'D:\Agents\微信截图助手\tools\scroll_target.ps1'
s = open(p, encoding='utf-8').read()
old = "$hash = [uint32](($row + 1) * 2654435761) -band 0xFFFFFFFF"
new = "$hash = [uint32]((($row + 1) * 2654435761) % 4294967296)"
assert old in s
s = s.replace(old, new, 1)
open(p, 'w', encoding='utf-8').write(s)
print('fixed hash overflow')
