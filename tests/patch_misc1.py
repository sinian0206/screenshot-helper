# 1) DeepSeekClient：流式/非流式 usage 补 ElapsedMs
p = r'D:\Agents\微信截图助手\src\DeepSeekClient.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('''                                AiUsage usage = ParseUsage(chunk);
                                if (usage != null && onUsage != null) onUsage(usage);''',
'''                                AiUsage usage = ParseUsage(chunk);
                                if (usage != null)
                                {
                                    usage.ElapsedMs = (int)stopwatch.ElapsedMilliseconds;
                                    if (onUsage != null) onUsage(usage);
                                }''', 1)
s = s.replace('''                        AiUsage usage = ParseUsage(root);
                        if (usage != null && onUsage != null) onUsage(usage);''',
'''                        AiUsage usage = ParseUsage(root);
                        if (usage != null)
                        {
                            usage.ElapsedMs = (int)stopwatch.ElapsedMilliseconds;
                            if (onUsage != null) onUsage(usage);
                        }''', 1)
open(p, 'w', encoding='utf-8').write(s)

# 2) SelfTest 去掉重复 using System.Drawing.Imaging
p2 = r'D:\Agents\微信截图助手\src\SelfTest.cs'
t = open(p2, encoding='utf-8').read()
lines = t.split('\n')
seen = set(); out = []
for line in lines:
    if line.strip() == 'using System.Drawing.Imaging;':
        if 'using System.Drawing.Imaging;' in seen:
            continue
        seen.add('using System.Drawing.Imaging;')
    out.append(line)
t = '\n'.join(out)
open(p2, 'w', encoding='utf-8').write(t)

# 3) mock 服务：--privacy-full 模式
p3 = r'D:\Agents\微信截图助手\tools\mock_deepseek_server.py'
m = open(p3, encoding='utf-8').read()
m = m.replace("    p.add_argument('--key', default='mock-key')",
              "    p.add_argument('--key', default='mock-key')\n    p.add_argument('--privacy-full', action='store_true')")
m = m.replace('''        if json_mode:
            self.send_json(completion_json(json.dumps({
                'regions': [
                    {'x': 0.10, 'y': 0.20, 'w': 0.18, 'h': 0.12, 'type': 'name'},
                    {'x': 0.55, 'y': 0.35, 'w': 0.22, 'h': 0.10, 'type': 'phone'},
                ]
            }, ensure_ascii=False)))
            return''',
'''        if json_mode:
            if ARGS.privacy_full:
                regions = [{'x': 0.02, 'y': 0.02, 'w': 0.96, 'h': 0.96, 'type': 'all'}]
            else:
                regions = [
                    {'x': 0.10, 'y': 0.20, 'w': 0.18, 'h': 0.12, 'type': 'name'},
                    {'x': 0.55, 'y': 0.35, 'w': 0.22, 'h': 0.10, 'type': 'phone'},
                ]
            self.send_json(completion_json(json.dumps({'regions': regions}, ensure_ascii=False)))
            return''')
open(p3, 'w', encoding='utf-8').write(m)
print('patched client/mock/selftest')
