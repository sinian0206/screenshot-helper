# -*- coding: utf-8 -*-
"""校验长截图结果内容连续、无重复、无错序（针对 tools/scroll_target.ps1 生成的非周期行内容）。
用法: python tests/verify_stitch.py <结果图.png>
输出: PASS/FAIL + 行号序列摘要；退出码 0/1。
"""
import sys
from PIL import Image

WIDTH_EXPECT = 660


def row_color(row):
    h = ((row + 1) * 2654435761) & 0xFFFFFFFF
    r = 40 + ((h >> 0) % 180)
    g = 40 + ((h >> 8) % 180)
    b = 40 + ((h >> 16) % 180)
    return (r, g, b)


def color_to_rows(colors):
    mapping = {}
    for row in range(100):
        mapping.setdefault(row_color(row), []).append(row)
    return mapping


def main():
    if len(sys.argv) < 2:
        print('usage: verify_stitch.py <image>')
        return 2
    img = Image.open(sys.argv[1]).convert('RGB')
    w, h = img.size
    if w != WIDTH_EXPECT:
        print(f'FAIL: 宽度 {w} != {WIDTH_EXPECT}')
        return 1
    mapping = color_to_rows(row_color)
    known = set(mapping.keys())

    def band_row(y_center, x_from, x_to):
        hits = {}
        for x in range(max(0, x_from), min(w, x_to)):
            p = img.getpixel((x, y_center))
            if p in known:
                for r in mapping[p]:
                    hits[r] = hits.get(r, 0) + 1
        if not hits:
            return None, 0
        best = max(hits, key=lambda k: hits[k])
        return best, hits[best]

    best_result = None
    for offset in range(50):
        seq = []
        blanks = 0
        for y in range(offset + 25, h - 20, 50):
            row, cnt = band_row(y, 400, w - 5)
            if row is None:
                blanks += 1
                seq.append(None)
            else:
                seq.append(row)
        # 评分：连续递增段最长
        best_run = 0
        run = 0
        prev = None
        for r in seq:
            if r is None:
                run = 0
                prev = None
                continue
            if prev is None or r == prev + 1:
                run += 1
                prev = r
                best_run = max(best_run, run)
            elif r == prev:
                # 重复行：不连续但不加分
                run = 1
                prev = r
            else:
                run = 1
                prev = r
        score = best_run
        if best_result is None or score > best_result[0]:
            best_result = (score, offset, seq, blanks)

    score, offset, seq, blanks = best_result
    rows = [r for r in seq if r is not None]
    duplicates = len(rows) - len(set(rows))
    gaps = 0
    for a, b in zip(rows, rows[1:]):
        if b != a + 1:
            gaps += 1
    summary = 'offset=%d, 命中行=%d, 最长连续=%d, 重复=%d, 跳变=%d, 空白带=%d, 序列=%s' % (
        offset, len(rows), score, duplicates, gaps, blanks,
        ','.join(str(r) for r in rows[:40]) + ('...' if len(rows) > 40 else ''))
    ok = (score >= max(3, len(rows) - 1)) and duplicates == 0 and gaps == 0 and len(rows) >= 5
    print(('PASS' if ok else 'FAIL') + ': ' + summary)
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
