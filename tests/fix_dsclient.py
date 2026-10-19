path = r'D:\Agents\微信截图助手\src\DeepSeekClient.cs'
src = open(path, encoding='utf-8').read()

# 1) 插入 AsArray helper（放在 GetInt 之前）
helper = '''        private static object[] AsArray(object value)
        {
            if (value == null) return null;
            object[] arr = value as object[];
            if (arr != null) return arr;
            System.Collections.ArrayList list = value as System.Collections.ArrayList;
            if (list != null) return list.ToArray();
            System.Collections.IEnumerable enumerable = value as System.Collections.IEnumerable;
            if (enumerable != null)
            {
                System.Collections.Generic.List<object> items = new System.Collections.Generic.List<object>();
                foreach (object item in enumerable) items.Add(item);
                return items.ToArray();
            }
            return null;
        }

        private static int GetInt(Dictionary<string, object> dict, string key)'''
src = src.replace('        private static int GetInt(Dictionary<string, object> dict, string key)', helper, 1)

# 2) 替换三处 as object[]
src = src.replace('''                                if (!chunk.TryGetValue("choices", out choicesObj)) continue;
                                object[] choices = choicesObj as object[];''',
'''                                if (!chunk.TryGetValue("choices", out choicesObj)) continue;
                                object[] choices = AsArray(choicesObj);''', 1)
src = src.replace('''            if (!root.TryGetValue("choices", out choicesObj)) return null;
            object[] choices = choicesObj as object[];''',
'''            if (!root.TryGetValue("choices", out choicesObj)) return null;
            object[] choices = AsArray(choicesObj);''', 1)
src = src.replace('''            if (root == null || !root.TryGetValue("regions", out listObj) || listObj == null) return regions;
            object[] arr = listObj as object[];''',
'''            if (root == null || !root.TryGetValue("regions", out listObj) || listObj == null) return regions;
            object[] arr = AsArray(listObj);''', 1)

assert 'as object[]' not in src.replace('object[] arr = value as object[]', ''), 'still has as object[]'
open(path, 'w', encoding='utf-8').write(src)
print('patched DeepSeekClient')
