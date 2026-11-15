# EPLAN Automation Skill

**让 AI Agent 真正操作 EPLAN 画电气图** —— 进程内 AddIn + HTTP/MCP 双层，带证据校验的自动化执行层。

> Tool success is not treated as task success. 工具返回成功不等于图真的画出来了。

针对 EPLAN Electric P8 2.9.4（Windows）实测，14 个动作，其中 6 个已真机验证。

---

## 为什么要这东西

EPLAN 是封闭桌面软件：外部程序不能直接命令它。市面上 AI 能做的是"生成图纸的思路"，但落不到真实工程里。

本项目把 EPLAN 的官方 API 包装成 Agent 可调用的执行层，并且**不信任工具返回值**：

- 每个动作在 `capabilities.json` 里登记状态等级（`verified` / `pilot` / `blocked`），未登记的动作直接拒绝执行
- 每次调用后做独立校验：产物文件是否本轮真的新写、连接数是否真的从 0 涨到 3
- 校验不过就降级为 `needs_review`，绝不假装成功
- 每次调用落一行 `trace.jsonl`，事后可审计

典型的反例就在这个仓库的历史里：`ExportPdf` 曾经是个占位脚本，什么都没做却返回 `{"success": true}`。这套机制现在会把它拦下来判 `capability_gap`。

---

## 架构

```
Agent / curl / MCP 客户端
      │
      ▼
┌─────────────────────────────────────────┐
│ EplanApi  :5000  (net10.0 WebAPI)       │
│  ① 查 capabilities.json  ← 能力门禁      │
│  ② 分发到引擎                            │
│  ③ 采集证据（文件 hash / 计数 delta）    │
│  ④ 独立校验 → status                    │
│  ⑤ 落 trace.jsonl                       │
└────────────────┬────────────────────────┘
                 │ HTTP  127.0.0.1:5002
                 ▼
┌─────────────────────────────────────────┐
│ EplanApiAddin  (net48, EPLAN 进程内)     │
│  反射加载于 EPLAN 进程，直接调官方 API    │
└────────────────┬────────────────────────┘
                 ▼
        EPLAN Electric P8 2.9.4
```

插件必须由引导脚本注入（EPLAN 不会自动扫描 AddIn 目录），这是踩了几十次坑确认的。

---

## 四态状态机

返回体里的 `status` 只有四个值，`success` 已降级为"引擎没抛异常"：

| status | 含义 | 该怎么做 |
|---|---|---|
| `ok` | 通过独立校验（文件真新了 / 连接数真涨了） | 可直接交付 |
| `needs_review` | 引擎说成功但证据不足 | 人工看图，不得直接交付 |
| `capability_gap` | 当前实现做不到 | 别重试，看 `nextSteps` |
| `failed` | 执行失败 | 修前置条件后重试 |

校验模式有四种：`none` / `text_ok` / `file_delta`（扫目录看 mtime）/ `file_reported`（按引擎自报路径核实）/ `metrics_min` / `metrics_delta`。

---

## 动作清单

| 动作 | 等级 | 校验 | 说明 |
|---|---|---|---|
| `GetStatus` | verified | none | 双通道探活（WebAPI + AddIn） |
| `CreateProject` | verified | metrics_min | 从模板/既有工程建项目并打开 |
| `CreatePages` | verified | metrics_min | 建 5 页结构 |
| `DrawSchematic` | **verified** | metrics_delta | ★ 符号级绘图 + 连接物化，唯一产出真实电气连接的动作 |
| `ListSymbols` | verified | metrics_min | 查符号库可用符号 |
| `CaptureScreen` | verified | file_reported | 抓 EPLAN 主窗口一帧 |
| `ExportPdf` | pilot | file_reported | 工程导出 PDF（反射适配 `PdfProject`/`PdfPages`） |
| `DeletePages` | pilot | metrics_min | 按页名删页，带 dryRun |
| `DrawMainCircuit` | pilot | metrics_min | 宏插入路线，宏间无互联 |
| `DrawPlcIo` | pilot | metrics_min | PLC I/O 页 |
| `DrawLadder` | pilot | metrics_min | 梯形图页 |
| `DrawAbbPanel` | pilot | metrics_min | 分区宏拼贴，仅视觉近似 |
| `OpenProject` | pilot | text_ok | 走 /Runscript，可能撞 LockingStep |
| `Ping` | verified | none | 只证明 WebAPI 活着 |

---

## 快速开始

### 前置

| 项 | 要求 |
|---|---|
| 系统 | Windows 10/11 x64 |
| EPLAN | Electric P8 2.9.4（其他版本需改 DLL 引用路径） |
| .NET | SDK 10（WebAPI）+ .NET Framework 4.8 运行时（AddIn） |

### 1. 编译

```bash
dotnet build EplanAddin/EplanApiAddin.csproj -c Release
dotnet build EplanApi/EplanApi.csproj -c Debug
```

### 2. 部署插件 + 启动 API

```powershell
# 插件 DLL 放到引导脚本约定的位置
copy EplanAddin\bin\Release\EplanApiAddin.dll C:\Temp\EplanAddin\

# EPLAN 启动后双击本文件注入插件（不会自动加载）
重新加载EPLAN插件.cmd

# 启动 WebAPI
cd EplanApi ; dotnet run
```

### 3. 验证

```bash
curl --noproxy '*' http://127.0.0.1:5000/api/eplan/health
# → addinAlive=1 才说明能真画

curl --noproxy '*' http://127.0.0.1:5000/api/eplan/capabilities
```

---

## 真机实测数据（EPLAN 2.9.4 SP1）

| 调用 | 结果 |
|---|---|
| `CreateProject` | `opened=1` |
| `CreatePages` | `pagesCreated=5/5` |
| `DrawSchematic` | 2 符号 → 连接 **0→3** |
| `DrawSchematic`（二次） | before=**3** → after=**6**（证明连接已落工程库） |
| `CaptureScreen` | 2582×1550，`printwindow`，方差 70.86 |

---

## 已知限制（如实记录）

| 限制 | 状态 |
|---|---|
| 页面图框/幅面不能用 API 改 | ❌ 只能在 GUI 改 |
| 逻辑页上的符号不能 `Scale` | ❌ 用变体代替 |
| 跨页中断点不能自动生成 | ❌ 需手工落符号 |
| GUI 里已打开的工程无法再 `OpenProject` | ✅ 已改为复用 |
| `Page.Name` 返回页号而非描述 | ✅ 已同时比对 `PAGE_NAME` 属性 |
| GUI 不会因 API 建页而跳转 | ❌ 截图常抓到当前视图 |
| 改 AddIn 代码必须重启 EPLAN | ⚠️ 程序集不可变，待做热更新 |

完整清单见 `EplanApi/capabilities.json` 的 `knownGaps`。

---

## 目录

```
EplanApi/            WebAPI + 能力注册表 + 证据层
  capabilities.json  ← 唯一事实源：动作状态/限制/校验规则
  Services/          CapabilityRegistry · EvidenceService · ScriptEngine
EplanAddin/          EPLAN 进程内插件（net48）
EplanRemote/         通过 EplanCommunicationServer 注入脚本
EplanDraw/           早期符号级绘图试验工程
EplanProbe/          环境探针
物流传输系统/        三段皮带设计包 + 可交互仿真（新手入门示例）
mcp/                 MCP Server（供 Claude/Cursor 等客户端）
tools/fake_addin.py  测试替身：无 EPLAN 也能验证证据层
操作指南.md          从零上手的完整操作手册
P0_证据层.md         证据层设计与实测记录
```

---

## 文档

| 文档 | 内容 |
|---|---|
| [操作指南.md](操作指南.md) | 日常启动、调用、排错树、能力边界 |
| [P0_证据层.md](P0_证据层.md) | 四态状态机、校验规则、真机实测、踩坑记录 |
| [方案C_实施方案.md](方案C_实施方案.md) | 符号级绘图引擎的 API 事实核查 |
| [方案C_执行报告.md](方案C_执行报告.md) | 阶段 0–3 全部通过的实测报告 |

---

## License

MIT
