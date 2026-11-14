"""测试替身：伪装成 EPLAN AddIn(5002)，用于在没有 EPLAN 的机器上验证证据层。

模拟两种情形：
  1) 旧版 AddIn —— 返回 success 但不回填 metrics  → 证据层应判 needs_review
  2) 新版 AddIn —— 回填 metrics                 → 证据层应判 ok
默认走 1)，加 --with-metrics 走 2)。
"""
import json
import os
import sys
import time
from http.server import BaseHTTPRequestHandler, HTTPServer

WITH_METRICS = "--with-metrics" in sys.argv

# 最小的合法 1x1 PNG（用于验证 file_delta 正向路径）
PNG_1PX = bytes.fromhex(
    "89504e470d0a1a0a0000000d4948445200000001000000010802000000907753"
    "de0000000c4944415408d763f8cfc000000301010018dd8db00000000049454e44ae426082"
)


class H(BaseHTTPRequestHandler):
    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(n).decode("utf-8", "ignore")
        action, params = "", {}
        try:
            req = json.loads(body)
            action = req.get("action", "")
            params = req.get("parameters") or {}
        except Exception:
            pass

        if action == "Ping":
            out = {"success": True, "message": "fake addin alive"}
        elif action == "DrawSchematic" and WITH_METRICS:
            out = {"success": True, "message": "fake 画了 7 个符号",
                   "metrics": {"itemsRequested": 7, "itemsPlaced": 7,
                               "connectionsBefore": 0, "connectionsAfter": 7}}
        elif action == "DrawSchematic":
            out = {"success": True, "message": "fake 画了 7 个符号（没回 metrics）"}
        elif action == "CaptureScreen" and WITH_METRICS:
            d = params.get("outputDir") or os.path.join(os.path.dirname(__file__), "shots")
            os.makedirs(d, exist_ok=True)
            f = os.path.join(d, "shot-fake-%d.png" % int(time.time() * 1000))
            with open(f, "wb") as fh:
                fh.write(PNG_1PX)
            out = {"success": True, "message": "fake 写了截图",
                   "metrics": {"file": f, "width": 1, "height": 1, "suspectBlank": False}}
        elif action == "CaptureScreen":
            out = {"success": True, "message": "fake 截图（没写文件）"}
        else:
            out = {"success": True, "message": "fake handled " + action}

        data = json.dumps(out, ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *a):
        pass


HTTPServer(("127.0.0.1", 5002), H).serve_forever()
