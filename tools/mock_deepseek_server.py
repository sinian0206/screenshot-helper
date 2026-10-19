# -*- coding: utf-8 -*-
"""DeepSeek API 本地 mock 服务（开发/QA 用，不联网）。

用法: python tools/mock_deepseek_server.py [--port 18765] [--log tools/mock_requests.jsonl] [--key mock-key]
接口: POST /chat/completions （OpenAI 兼容，支持流式 SSE / JSON）
行为:
  - Authorization 必须为 Bearer mock-key，否则 401
  - 请求体 stream=true：根据 system prompt 返回 OCR 文本或翻译文本（SSE，含 usage 与 [DONE]）
  - 请求体含 response_format json_object：返回一键打码隐私区域 JSON
  - 其它：返回普通非流式 chat completion JSON
"""
import json
import sys
import time
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ARGS = None


def parse_args():
    p = argparse.ArgumentParser()
    p.add_argument('--port', type=int, default=18765)
    p.add_argument('--log', default='tools/mock_requests.jsonl')
    p.add_argument('--key', default='mock-key')
    p.add_argument('--privacy-full', action='store_true')
    return p.parse_args()


def log_request(path, headers, body):
    try:
        rec = {
            'time': time.strftime('%Y-%m-%d %H:%M:%S'),
            'path': path,
            'auth': headers.get('Authorization'),
            'body_len': len(body),
            'body': body[:20000],
        }
        with open(ARGS.log, 'a', encoding='utf-8') as f:
            f.write(json.dumps(rec, ensure_ascii=False) + '\n')
    except Exception as e:
        print('log error', e, file=sys.stderr)


def extract(req):
    model = req.get('model', '')
    messages = req.get('messages', [])
    text_parts = []
    image_count = 0
    system = ''
    for msg in messages:
        role = msg.get('role')
        content = msg.get('content')
        if isinstance(content, str):
            if role == 'system':
                system = content
            else:
                text_parts.append(content)
        elif isinstance(content, list):
            for part in content:
                if not isinstance(part, dict):
                    continue
                if part.get('type') == 'text':
                    if role == 'system':
                        system += part.get('text', '')
                    else:
                        text_parts.append(part.get('text', ''))
                elif part.get('type') == 'image_url':
                    image_count += 1
    return model, system, '\n'.join(text_parts), image_count


def completion_json(content, usage=None):
    return {
        'id': 'mock-chatcmpl-1',
        'object': 'chat.completion',
        'created': int(time.time()),
        'model': 'deepseek-flash',
        'choices': [{
            'index': 0,
            'message': {'role': 'assistant', 'content': content},
            'finish_reason': 'stop',
        }],
        'usage': usage or {'prompt_tokens': 100, 'completion_tokens': 20, 'total_tokens': 120},
    }


class Handler(BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'

    def log_message(self, fmt, *args):
        pass

    def send_json(self, obj, status=200):
        data = json.dumps(obj, ensure_ascii=False).encode('utf-8')
        self.send_response(status)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def stream_sse(self, pieces):
        self.send_response(200)
        self.send_header('Content-Type', 'text/event-stream; charset=utf-8')
        self.send_header('Cache-Control', 'no-cache')
        self.send_header('Connection', 'keep-alive')
        self.end_headers()

        def send_chunk(delta, finish=None, usage=None):
            chunk = {
                'id': 'mock-chunk',
                'object': 'chat.completion.chunk',
                'created': int(time.time()),
                'model': 'deepseek-flash',
                'choices': [{'index': 0, 'delta': delta, 'finish_reason': finish}],
            }
            if usage is not None:
                chunk['usage'] = usage
            payload = ('data: ' + json.dumps(chunk, ensure_ascii=False) + '\n\n').encode('utf-8')
            self.wfile.write(payload)
            self.wfile.flush()

        send_chunk({'role': 'assistant', 'content': ''})
        for piece in pieces:
            send_chunk({'content': piece})
            time.sleep(0.01)
        send_chunk({}, 'stop', {'prompt_tokens': 123, 'completion_tokens': 45, 'total_tokens': 168})
        self.wfile.write(b'data: [DONE]\n\n')
        self.wfile.flush()

    def do_POST(self):
        length = int(self.headers.get('Content-Length', '0'))
        body = self.rfile.read(length).decode('utf-8', errors='replace')
        log_request(self.path, self.headers, body)
        if self.path != '/chat/completions':
            self.send_json({'error': {'message': 'not found'}}, 404)
            return
        if self.headers.get('Authorization') != 'Bearer ' + ARGS.key:
            self.send_json({'error': {'message': 'Authentication Fails, invalid api key'}}, 401)
            return
        try:
            req = json.loads(body)
        except Exception:
            self.send_json({'error': {'message': 'invalid json'}}, 400)
            return

        model, system, user, image_count = extract(req)
        stream = bool(req.get('stream'))
        json_mode = isinstance(req.get('response_format'), dict)

        if json_mode:
            if ARGS.privacy_full:
                regions = [{'x': 0.02, 'y': 0.02, 'w': 0.96, 'h': 0.96, 'type': 'all'}]
            else:
                regions = [
                    {'x': 0.10, 'y': 0.20, 'w': 0.18, 'h': 0.12, 'type': 'name'},
                    {'x': 0.55, 'y': 0.35, 'w': 0.22, 'h': 0.10, 'type': 'phone'},
                ]
            self.send_json(completion_json(json.dumps({'regions': regions}, ensure_ascii=False)))
            return

        if stream:
            if 'OCR' in system or '文字提取' in system or '识别引擎' in system:
                pieces = ['第一行识别文字', '\n', '第二行 Hello 世界', '\n', 'Third line 12345']
            elif '翻译' in system or '翻译' in user:
                pieces = ['Hello', ' world', '! 这是', '译文。']
            else:
                pieces = ['ok']
            self.stream_sse(pieces)
            return

        self.send_json(completion_json('连接成功'))


def main():
    global ARGS
    ARGS = parse_args()
    server = ThreadingHTTPServer(('127.0.0.1', ARGS.port), Handler)
    print('mock DeepSeek server listening on http://127.0.0.1:%d' % ARGS.port, flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == '__main__':
    main()
