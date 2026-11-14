"""
EPLAN 本地 MCP Server
=====================
把本机 EPLAN 操作包装成 MCP 工具，供 Claude Desktop / Cursor / Cline 等客户端自动调用。

链路：
  MCP 客户端 → 本 Server → http://127.0.0.1:5000 (EplanApi，默认端口)
                          → http://127.0.0.1:5002 (EPLAN AddIn, 进程内)
                          → EPLAN 官方 API

前置条件：
  1. EPLAN 已启动，且 AddIn 已加载（5002 能 Ping 通）
  2. EplanApi 服务已启动（默认 5000）

端口说明（两边必须一致）：
  EplanApi 默认监听 5000，见 EplanApi/Program.cs 与 appsettings.json 的 Eplan:ListenUrl。
  若你把服务改到了别的端口（例如 5000 被占用而改用 5001），不用改本文件，
  只设置环境变量 EPLAN_API_BASE 指向它即可，例如：
      set EPLAN_API_BASE=http://127.0.0.1:5001

★ 关键约定（P0 证据层）：返回体里 success 只代表「没抛异常」，
  真正的交付判断看 status：
    ok             = 已通过独立校验（产物文件真的新了 / 连接数真的涨了）
    needs_review   = 引擎说成功但证据不足，必须人工看图确认
    capability_gap = 当前实现做不到，别重试，改用别的动作
    failed         = 执行失败
  Agent 必须先看 status，再看 message。

启动/加载方式见 EplanApi/使用白皮书.md 第 12 节（MCP 接入）。
"""

import json
import os
import urllib.error
import urllib.parse
import urllib.request

try:
    # mcp 1.x
    from mcp.server.fastmcp import FastMCP
except ImportError:
    # mcp 2.x：FastMCP 已更名为 MCPServer
    from mcp.server.mcpserver import MCPServer as FastMCP

# EplanApi 地址：默认 5000，可用环境变量 EPLAN_API_BASE 覆盖（不要带结尾斜杠）
API_BASE = os.environ.get("EPLAN_API_BASE", "http://127.0.0.1:5000").rstrip("/")
API = API_BASE + "/api/eplan/execute"
TIMEOUT = 180

# 本机 127.0.0.1 必须绕过系统代理，否则会拿到 502 文本而不是 JSON
_OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))

mcp = FastMCP("eplan-local")

STATUS_HELP = (
    "ok=已通过独立校验 / needs_review=引擎说成功但证据不足需人工看图 / "
    "capability_gap=能力缺口勿重试 / failed=执行失败"
)


def _call(action: str, parameters: dict | None = None) -> dict:
    """向本地 EplanApi 发一个动作。"""
    body = json.dumps({"action": action, "parameters": parameters or {}}).encode("utf-8")
    req = urllib.request.Request(
        API,
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with _OPENER.open(req, timeout=TIMEOUT) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.URLError as exc:
        return {
            "success": False,
            "status": "failed",
            "message": "连不上本地 EplanApi(" + API_BASE + ")。请先启动服务："
                       "cd D:\\默认存储空间\\EPLAN\\EplanApi ; dotnet run",
            "error": str(exc),
        }
    except Exception as exc:  # noqa: BLE001
        return {"success": False, "status": "failed", "message": "调用失败", "error": str(exc)}


def _get(path: str, query: dict | None = None) -> dict:
    """GET 辅助（能力清单 / 追踪记录 / 健康检查）。"""
    url = API_BASE + path
    if query:
        url += "?" + urllib.parse.urlencode(query)
    req = urllib.request.Request(url, method="GET")
    try:
        with _OPENER.open(req, timeout=30) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except Exception as exc:  # noqa: BLE001
        return {"success": False, "status": "failed",
                "message": "GET 失败: " + url, "error": str(exc)}


# ---------------------------------------------------------------- 只读 / 自检

@mcp.tool()
def eplan_ping() -> dict:
    """自检：确认本地 EPLAN API 服务活着。操作 EPLAN 前先调这个。"""
    return _call("Ping", {})


@mcp.tool()
def eplan_health() -> dict:
    """双通道探活：WebAPI(:5000) 与 EPLAN AddIn(:5002) 分别报活。

    这是判断「能不能真画图」的唯一依据：addinAlive=1 才说明 EPLAN 在跑且插件已加载。
    """
    return _get("/api/eplan/health")


@mcp.tool()
def eplan_capabilities() -> dict:
    """读能力注册表：每个动作的状态等级(verified/pilot/blocked)、参数、限制、校验规则。

    规划任何 EPLAN 操作之前先调这个，不要凭印象挑动作。
    """
    return _get("/api/eplan/capabilities")


@mcp.tool()
def eplan_trace(limit: int = 20) -> dict:
    """读最近的调用追踪记录（新→旧），含动作、状态、耗时、产物文件与 hash。

    排查「上次到底做了什么」「为什么返回 needs_review」时用。
    """
    return _get("/api/eplan/trace", {"limit": max(1, min(limit, 200))})


# ---------------------------------------------------------------- 建工程

@mcp.tool()
def eplan_create_project() -> dict:
    """创建「物流传输_三段顺序」项目（课本三段皮带顺序控制）。

    会自动基于已有项目复制生成，并用 OpenMode.Exclusive 打开。
    前置：EPLAN 已启动且 AddIn 已加载（先跑 eplan_health 确认 addinAlive=1）。
    """
    return _call("CreateProject", {})


@mcp.tool()
def eplan_create_pages() -> dict:
    """在当前项目里建 5 页结构：封面 / 主电路 / PLC I/O / 梯形图 / 端子。

    前置：必须先执行 eplan_create_project。
    """
    return _call("CreatePages", {})


# ---------------------------------------------------------------- 画图

@mcp.tool()
def eplan_draw_schematic(project: str, spec: str, page_name: str = "") -> dict:
    """★ 唯一能产出真实电气连接的绘图动作（符号级 + 连接物化）。

    project: .elk 工程绝对路径，用正斜杠
    spec:    JSON 字符串，形如
             {"items":[{"s":"Q_3L","t":"-Q1","x":100,"y":240,"v":0}],
              "links":[{"f":"-Q1","fp":1,"t":"-M1","tp":0}]}
              s=符号名(IEC_symbol 库) t=设备编号 x/y=坐标 v=变体号
              links 的 fp/tp 是引脚序号，会按顺序自动吸附对齐
    page_name: 可选，缺省新建 SCHEMATIC 页

    判断是否真的画成功：看 evidence.metrics.connectionsAfter 与 connectionsBefore
    的差值。差值为 0 → status=needs_review（图上可能有线，项目数据里没有）。
    """
    params = {"project": project, "spec": spec}
    if page_name:
        params["pageName"] = page_name
    return _call("DrawSchematic", params)


@mcp.tool()
def eplan_list_symbols(project: str, keyword: str = "") -> dict:
    """查工程符号库里有哪些符号可用（画之前先查，别猜符号名）。

    project: .elk 工程绝对路径
    keyword: 符号名关键字，如 "Q" / "M" / "K"
    """
    params = {"project": project}
    if keyword:
        params["keyword"] = keyword
    return _call("ListSymbols", params)


@mcp.tool()
def eplan_draw_main_circuit() -> dict:
    """在主电路页插入 3 个电机控制回路宏（M1/M2/M3）。pilot 级，宏间无电气互联。

    前置：必须先执行 eplan_create_project 和 eplan_create_pages。
    """
    return _call("DrawMainCircuit", {})


@mcp.tool()
def eplan_draw_plc_io() -> dict:
    """在 PLC I/O 页插 PLC 总览 + DI + DO 宏。pilot 级。"""
    return _call("DrawPlcIo", {})


@mcp.tool()
def eplan_draw_ladder() -> dict:
    """在梯形图页插急停 + PLC 电源宏。pilot 级，非完整梯形逻辑。"""
    return _call("DrawLadder", {})


@mcp.tool()
def eplan_capture_screen(output_dir: str = "") -> dict:
    """抓 EPLAN 主窗口当前一帧存 PNG，返回文件路径与画面方差。

    用于「边画边看」：每画完一步截一张，再用视觉能力看图确认。
    注意这**不是实时流**——MCP 是请求/响应，只能轮询抓帧。
    suspectBlank=true 或 variance<2 表示抓到的是黑图（窗口最小化/遮挡）。
    """
    params = {}
    if output_dir:
        params["outputDir"] = output_dir
    return _call("CaptureScreen", params)


# ---------------------------------------------------------------- 一键流程

@mcp.tool()
def eplan_build_all() -> dict:
    """一键建出整套：建项目 → 建 5 页 → 画主电路 3 个电机回路。

    任一步 status 不是 ok 就中止（不再往下堆动作）。
    """
    results = {}
    r1 = _call("CreateProject", {})
    results["CreateProject"] = r1
    if r1.get("status") != "ok":
        return {"status": "needs_review", "message": "建项目未通过校验，后续已中止",
                "hint": STATUS_HELP, "results": results}

    r2 = _call("CreatePages", {})
    results["CreatePages"] = r2
    if r2.get("status") != "ok":
        return {"status": "needs_review", "message": "建页未通过校验，后续已中止",
                "hint": STATUS_HELP, "results": results}

    r3 = _call("DrawMainCircuit", {})
    results["DrawMainCircuit"] = r3
    return {
        "status": r3.get("status", "failed"),
        "message": "整套流程执行完毕：项目 + 5 页 + 主电路 3 电机回路",
        "hint": STATUS_HELP,
        "results": results,
    }


@mcp.tool()
def eplan_export_pdf(project: str, output_file: str = "", output_dir: str = "", schema: str = "") -> dict:
    """导出整本工程为 PDF（AddIn 进程内真实导出，不是占位脚本）。

    project:     .elk 工程绝对路径
    output_file: 可选，完整 PDF 路径；不给则写到 <工程目录>/pdf/<工程名>.pdf
    output_dir:  可选，只给目录
    schema:     可选，导出方案文件；空则用 EPLAN 当前 PDF 方案

    判定成功看 return.status 与 evidence.files（file_reported：按引擎自报路径核实文件存在且是本轮写入）。
    """
    params = {"project": project}
    if output_file:
        params["outputFile"] = output_file
    if output_dir:
        params["outputDir"] = output_dir
    if schema:
        params["schema"] = schema
    return _call("ExportPdf", params)


@mcp.tool()
def eplan_delete_pages(project: str, page_name: str, dry_run: bool = True) -> dict:
    """按页名/页描述删除页（清理重复页、画错的页）。

    project:   .elk 工程绝对路径
    page_name: 页号（如 "/6"）或页描述关键字（如 "P0验收"），模糊匹配，命中多页会全删
    dry_run:   默认 True —— 只报命中哪些页，不删。确认无误后再传 False 真删。

    注意：删除不可撤销。dry_run 调用会返回 status=needs_review（pagesDeleted=0），属预期。
    """
    return _call("DeletePages", {
        "project": project,
        "pageName": page_name,
        "dryRun": "true" if dry_run else "false",
    })


@mcp.tool()
def eplan_execute(action: str, parameters: dict | None = None) -> dict:
    """通用入口：直接指定 action 调用 EplanApi（用于新增/不常用的动作）。

    action: Ping / GetStatus / OpenProject / ExportPdf / CreateProject / CreatePages /
            DrawMainCircuit / DrawPlcIo / DrawLadder / DrawAbbPanel / DrawSchematic /
            ListSymbols / CaptureScreen / DeletePages
    parameters: 动作所需参数（OpenProject 需 {"path": "D:/xx/demo.elk"}）

    ExportPdf 已改为 AddIn 内真实导出（status=pilot，未给 schema 时依赖 EPLAN 当前方案）。
    """
    return _call(action, parameters or {})


if __name__ == "__main__":
    mcp.run()
