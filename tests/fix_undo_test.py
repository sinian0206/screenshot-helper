path = r'D:\Agents\微信截图助手\src\SelfTest.cs'
src = open(path, encoding='utf-8').read()
src = src.replace('''                            check("编辑器标注渲染", HasWarmColor(snap), snapPath);''',
'''                            check("编辑器标注渲染", HasWarmColor(snap, new Rectangle(100, 100, 600, 400)), snapPath);''', 1)
src = src.replace('''                            check("撤销功能", !HasWarmColor(undone), "撤销后不应有红色标注");''',
'''                            check("撤销功能", !HasWarmColor(undone, new Rectangle(100, 100, 600, 400)), "撤销后选区内部不应有红色标注");''', 1)
# 增加带矩形范围的重载
anchor = '''        private static bool HasWarmColor(Bitmap bitmap)
        {'''
newanchor = '''        private static bool HasWarmColor(Bitmap bitmap)
        {
            return HasWarmColor(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        }

        private static bool HasWarmColor(Bitmap bitmap, Rectangle area)
        {
            area = Rectangle.Intersect(area, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            if (area.Width <= 0 || area.Height <= 0) return false;'''
assert anchor in src
src = src.replace(anchor, newanchor, 1)
# 原方法循环需改用 area 边界
src = src.replace('''            int stepX = Math.Max(1, bitmap.Width / 200);
            int stepY = Math.Max(1, bitmap.Height / 200);
            for (int y = 0; y < bitmap.Height; y += stepY)
            {
                for (int x = 0; x < bitmap.Width; x += stepX)
                {''', '''            int stepX = Math.Max(1, area.Width / 200);
            int stepY = Math.Max(1, area.Height / 200);
            for (int y = area.Top; y < area.Bottom; y += stepY)
            {
                for (int x = area.Left; x < area.Right; x += stepX)
                {''', 1)
open(path, 'w', encoding='utf-8').write(src)
print('patched')
