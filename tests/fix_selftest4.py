path = r'D:\Agents\微信截图助手\src\SelfTest.cs'
src = open(path, encoding='utf-8').read()
old = '''                for (int i = 0; i < 18; i++)
                {
                    int x = 100 + random.Next(width - 300);
                    int y = 280 + random.Next(height - 420);
                    int w = 40 + random.Next(160);
                    int h = 18 + random.Next(60);'''
new = '''                int areaW = Math.Max(40, width - 140);
                int areaH = Math.Max(30, height - 140);
                for (int i = 0; i < 18; i++)
                {
                    int w = 30 + random.Next(Math.Max(20, Math.Min(160, areaW / 2)));
                    int h = 14 + random.Next(Math.Max(10, Math.Min(60, areaH / 4)));
                    int x = 70 + random.Next(Math.Max(10, areaW - w - 10));
                    int y = 70 + random.Next(Math.Max(10, areaH - h - 10));'''
assert old in src
src = src.replace(old, new, 1)
open(path, 'w', encoding='utf-8').write(src)
print('patched')
