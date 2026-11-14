"""生成三段皮带运输机顺序控制系统的 EPLAN 精细原理图。
输出 spec JSON（符号级 + 真实连线），通过 DrawSchematic 画入工程。

系统（据 物流传输系统/设计包.md）：
  主回路：QF1 → KM1 → FR1 → M1（1#皮带）
          QF1 → KM2 → FR2 → M2
          QF1 → KM3 → FR3 → M3
  控制回路：FU1 → SB0(急停) → SB2(停止) → SB1(启动) → KM1
            FU1 → KM1自锁 → ...
            KM1 → KM2 → KM3（逆序启动链）
            SB2 → KM1 → KM2 → KM3（顺序停止链）
"""
import json, urllib.request, sys

PROJECT = "D:/EPLAN/项目/Linrrrx/DrawTest.elk"
PAGE = "皮带三段"

# ---------- 主回路（Power circuit）：三段并列 ----------
# 每个电机一条支路：QF1 引出 → KM(接触器) → FR(热继) → M(电机)
power_items = []
power_links = []

# 断路器（一个三极的，代表总电源 QF）
power_items.append({"s": "Q_3L", "t": "-QF1", "x": 60, "y": 260})

# 三段电机支路：X 坐标错开，Y 依次下移
branches = [
    {"km": "-KM1", "fr": "-FR1", "m": "-M1", "y": 260, "x": 130},
    {"km": "-KM2", "fr": "-FR2", "m": "-M2", "y": 220, "x": 130},
    {"km": "-KM3", "fr": "-FR3", "m": "-M3", "y": 180, "x": 130},
]

for b in branches:
    power_items.append({"s": "K", "t": b["km"], "x": b["x"], "y": b["y"]})
    power_items.append({"s": "K", "t": b["fr"], "x": b["x"] + 45, "y": b["y"]})
    power_items.append({"s": "M3", "t": b["m"], "x": b["x"] + 90, "y": b["y"]})
    # QF 输出 → 接触器
    power_links.append({"f": "-QF1", "fp": 1, "t": b["km"], "tp": 0})
    # 接触器 → 热继电器
    power_links.append({"f": b["km"], "fp": 1, "t": b["fr"], "tp": 0})
    # 热继电器 → 电机
    power_links.append({"f": b["fr"], "fp": 1, "t": b["m"], "tp": 0})

# ---------- 控制回路（Control circuit） ----------
ctrl_items = []
ctrl_links = []

# 起点：控制保险 FU1（放在右侧独立列）
FU = {"x": 260, "y": 260}
ctrl_items.append({"s": "F1", "t": "-FU1", "x": FU["x"], "y": FU["y"]})

# 主控制链（垂直向下）：FU1 → SB0急停 → SB2停止 → SB1启动 → KM1线圈
chain = [
    {"s": "S", "t": "-SB0", "tag": "急停"},
    {"s": "S", "t": "-SB2", "tag": "停止"},
    {"s": "S", "t": "-SB1", "tag": "启动"},
    {"s": "K", "t": "-KM1", "tag": "KM1线圈"},
]
y = 240
prev = "-FU1"
for c in chain:
    ctrl_items.append({"s": c["s"], "t": c["t"], "x": FU["x"], "y": y})
    ctrl_links.append({"f": prev, "fp": 1, "t": c["t"], "tp": 0})
    prev = c["t"]
    y -= 40

# 逆序启动链：KM1 → KM2 → KM3（横向排列，表示 M3 先转的逻辑链）
# 工程上逆序 = 从 KM3 起步，这里画成独立的启动支路
start_chain = ["-KM3", "-KM2", "-KM1"]
x = 200
prev = "-SB1"
for tag in start_chain:
    ctrl_items.append({"s": "K", "t": tag + "_A", "x": x, "y": 100})
    ctrl_links.append({"f": prev, "fp": 1, "t": tag + "_A", "tp": 0})
    prev = tag + "_A"
    x -= 45   # 向左排，形成递进链

# 顺序停止链：KM1 → KM2 → KM3
stop_chain = ["-KM1_B", "-KM2_B", "-KM3_B"]
x = 200
prev = "-SB2"
for tag in stop_chain:
    ctrl_items.append({"s": "K", "t": tag, "x": x, "y": 40})
    ctrl_links.append({"f": prev, "fp": 1, "t": tag, "tp": 0})
    prev = tag
    x -= 45

# ---------- PLC I/O 端子（信号端子） ----------
io_items = []
io_links = []
io_map = [
    ("-I0.0", 260, 200), ("-I0.1", 280, 200), ("-I0.2", 300, 200),
    ("-I0.3", 260, 180), ("-I0.4", 280, 180), ("-I0.5", 300, 180),
    ("-I0.6", 260, 160), ("-I0.7", 280, 160),
    ("-Q0.0", 260, 140), ("-Q0.1", 280, 140), ("-Q0.2", 300, 140),
]
for tag, ix, iy in io_map:
    io_items.append({"s": "X", "t": tag, "x": ix, "y": iy})

# ---------- 合并 ----------
spec = {"items": power_items + ctrl_items + io_items,
        "links": power_links + ctrl_links + io_links}

if "--print" in sys.argv:
    print(json.dumps(spec, ensure_ascii=True))
    sys.exit(0)

body = {"action": "DrawSchematic", "parameters": {
    "project": PROJECT,
    "pageName": PAGE,
    "spec": json.dumps(spec, ensure_ascii=True)
}}

print("元件数: %d  连线数: %d" % (len(spec["items"]), len(spec["links"])))
print("项目: %s   页: %s" % (PROJECT, PAGE))

req = urllib.request.Request(
    "http://127.0.0.1:5000/api/eplan/execute",
    json.dumps(body, ensure_ascii=True).encode("utf-8"),
    {"Content-Type": "application/json"})
try:
    res = json.loads(urllib.request.urlopen(req, timeout=300).read().decode("utf-8"))
    print("结果:", res.get("message"))
    if res.get("log"):
        print("诊断:", res["log"][:600])
except Exception as e:
    print("错误:", e)
    if hasattr(e, "read"):
        print(e.read().decode("utf-8"))