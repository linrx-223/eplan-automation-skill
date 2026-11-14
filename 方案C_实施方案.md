# 方案 C 实施方案：EPLAN 符号级自动绘图引擎

> 依据：`D:\EPLAN\Platform\2.9.4\Bin\*.xml`（官方 API 文档，12.7 MB）逐条核对，非推测。
> 本文每行 API 都带出处，可直接照抄开工。

---

## 0. 结论先行

| 问题 | 结论 | 依据 |
| --- | --- | --- |
| 能不能放单个符号？ | **能。** `SymbolReference.Create(SymbolVariant, Page)` 就是官方符号级插入 API | `DataModelu.xml` → `SymbolReference.Create(...)`：「Creates a SymbolReference … placed on the page, using a given SymbolVariant」 |
| 能不能让 EPLAN 自动连线？ | **两条路都存在**，一条靠几何（不确定），一条靠显式调用（确定） | 见下节 A/B 路线对比 |
| 明确做不到的事 | ① 页面图框/幅面 API 改不动；② 别指望 `Insert.WindowMacro` 会产生任何电气连接 —— `Insert` 类只有 PageMacro / WindowMacro / SymbolMacro / PrePlanningMacro 四个重载，全是「整块贴」 | 白皮书第 14 节 + `Insert` 类型成员清单 |

**关键修正（相对原提示词）**：原方案把宝押在「对齐连接点让 EPLAN 自动生成导线」上。文档里其实有更硬的一条路 ——
`new Connection(project, pinA, pinB)` **直接创建两点之间的逻辑连接，不需要任何几何对齐**。这条路必须做主力，几何对齐降级为「让图好看」的辅助手段。两条路不冲突，可以并用。

---

## 1. API 事实核查表

### 1.1 符号放置链（核心）

| 步骤 | API | 出处 |
| --- | --- | --- |
| 打开符号库（工程内） | `new SymbolLibrary(project, "IEC_symbol")` 或 `.Initialize(project, name)` | `MasterData.SymbolLibrary.#ctor(Project,String)` / `Initialize(Project,String)` |
| 打开符号库（系统） | `library.Initialize("GRAPHICS")`（返回 true 时必须 `Close()`） | `SymbolLibrary.Initialize(System.String)` |
| 取符号 | `library.Symbols` → `Symbol[]`，或 `library.Item["名字"]` | `SymbolLibrary.Symbols` / `Item(String)` |
| 筛可用符号 | `Symbol.Type == SymbolType.Function`，`IsUsedForFunction` / `IsUsedForRouting` | `Symbol.SymbolType` 枚举（Function / Routing / TNodeUp / InterruptionPoint …） |
| 取变体 | `symbol.Variants` → `SymbolVariant[]`，或 `symbol.Item[variantNr]` | `Symbol.Variants` / `Item(Int32)` |
| **放置** | `new SymbolReference().Create(symbolVariant, page)` | `SymbolReference.Create(SymbolVariant,Page)` — 注意参数顺序是 **(变体, 页)** |
| **定位** | `symbolRef.Location = new PointD(x, y)` | `SymbolReference.Location`：「insertion point in graphical coordinate system」 |
| 设备标识 | `fn.Properties[Properties.FunctionBase.FUNC_IDENTDEVICETAG] = "-Q1"` | 属性 #20005「DT (identifying)」 |

### 1.2 连接相关的两把钥匙

| API | 用途 | 出处 |
| --- | --- | --- |
| `PinBase.Location` | **连接点相对符号插入点的偏移**（不是绝对坐标） | `PinBase.Location`：「Gets the connection point's position relative to the symbol's insertion point」 |
| `SymbolReference.GraphicalConnectionPoints` | `PinBase[]`，该符号实际放上去后的全部连接点 | `SymbolReference.GraphicalConnectionPoints` |
| `SymbolReference.GraphicalConnections` | `GraphicalConnection[]`，**EPLAN 已自动生成的连线**（含 T 节点、拐角）。这是验收集合 Stage 2 的权威判据，比看 `Connection.eod` 大小靠谱得多 | `SymbolReference.GraphicalConnections` |
| `new Connection(project, pinA, pinB)` | **显式创建一条逻辑连接**，不需要两设备相邻、不需要对齐 | `Connection.#ctor(Project,PinBase,PinBase)`：「Constructor to create not-placed Connection」 |
| `NetDefinitionPoint.AddDaisyChain(PinBase[])` | 一条松弛链：按数组顺序两两相连（母线、串联回路一步到位） | `NetDefinitionPoint.AddDaisyChain` |
| `ConnectionService.DesignateConnections(project, ...)` | 批量编号（线号） | HEServices |
| `ConnectionService.Correct(project, ...)` | 等价于 GUI「项目数据 → 连接 → 修正」，收尾用 | HEServices |

---

## 2. 两条连线技术路线（核心决策）

| | 路线 G：几何自动连线 | 路线 L：显式逻辑连接 |
| --- | --- | --- |
| 做法 | 把符号 B 的连接点坐标算到与符号 A 完全重合 | `new Connection(prj, pinA, pinB)` |
| 依赖 | EPLAN 内部的自动连线判定规则（文档未说明触发阈值，属于**黑箱**） | API 契约，白箱 |
| 确定性 | 中。可能对、可能要额外画一条线段才连上 | 高 |
| 图面上有没有线 | 有（这才是"原理图"的观感） | 创建的是「未放置连接」，页面上**不显示导线** |
| 定位 | 主验证目标 | **主数据模型手段** |

**结论：G 与 L 缺一不可。** L 负责「电气上真的连了」——有了它，报表、端子图、PLC、线号全是真的；G 负责「看起来是张原理图」。阶段 2 必须同时验证两者。

> 若阶段 2 发现几何对齐不生效，也不危及整个方案：可用「路线 L + 手动摆放」兜底，图仍有完整电气逻辑，只是没有绘制出的导线，后续可再攻。

---

## 3. 分阶段执行计划

### 阶段 0 — 环境探针（30 分钟，必须先做）

目标：把三个未知量测出来，后续所有坐标计算都依赖它们。

| 未知量 | 探针做法 |
| --- | --- |
| 页面坐标系原点与 **Y 轴方向**（向上还是向下） | 在逻辑页 `Location=(100,100)` 放一个符号，读 `GetBoundingBox()`，`Y` 大于 100 还是小于 100 |
| 页面可用范围 | 读 `Page.Size`，结合图框 `PlotFrame.Size` |
| 现有项目里可用的符号名清单 | `SymbolLibrary("IEC_symbol").Symbols` 全量 dump：名字 + `Type` + 变体数 + 连接点数 |

产物：`C:\Temp\stage0_result.txt`，内容是一份**符号字典（名字 → 变体 → 连接点偏移）**。这份字典是后面所有电路的"元件字典"，务必留档复用。

**验收**：能列出 ≥50 个 `SymbolType.Function` 的符号，且至少找到断路器/接触器线圈/触点/电机/按钮/端子各一类。

---

### 阶段 1 — 放一个符号（验证最小闭环）

```csharp
// EplanDraw/Draw.cs —— mode=place1
var pm = new ProjectManager();
var prj = pm.OpenProject(TargetPath, ProjectManager.OpenMode.Exclusive);
Page page = /* 新建或取已有 Circuit 页 */;

var lib = new SymbolLibrary(prj, "IEC_symbol");
Symbol sym = lib.Item["XXX"];            // 阶段 0 字典里挑一个
SymbolVariant v = sym.Variants[0];        // 变量 0 = 标准变体，通常安全

var sr = new SymbolReference();
sr.Create(v, page);                       // ★ 核心一行
sr.Location = new PointD(100.0, 100.0);

// 若类型确为 Function，先进性改名
var fn = sr as Function;
if (fn != null)
    fn.Properties[Eplan.EplApi.DataModel.Properties.FunctionBase.FUNC_IDENTDEVICETAG] = "-Q1";
```

**验收**：同一页 `AllPlacements` 在放之前 / 之后各计数一次，确认 `SymbolReference`/`Function` 类型计数 **+1**，且 `Location` 读回 = (100,100)。

**失败处置**（按序尝试，每步都写报告）：
1. `lib.Item["XXX"]` 抛异常 → 名字带后缀或大小写不符 → 改用遍历 `Symbols` 匹配
2. `Create` 抛 `IncorrectSymbolTypeException` → 该符号类型不适合做 `SymbolReference`，换一个 `Type==Function` 的
3. `Location` 写入抛 `BaseException` → 页面写保护，先 `page.PauseWriteProtection(true)`

---

### 阶段 2 — 两个符号 + 一条连接（★ 全局成败节点）

分两个子实验，**都做**：

**2A. 路线 L（预期必成）**
```csharp
PinBase[] pinsA = srA.GraphicalConnectionPoints;
PinBase[] pinsB = srB.GraphicalConnectionPoints;
var conn = new Connection(prj, pinsA[0], pinsB[0]);
```
**验收**：`prj` 上能枚举到新 Connection，`conn.StartPin`/`conn.EndPin` 分别指向 A/B；`conn.IsPlaced == false`（预期）。

**2B. 路线 G（不确定，要试）**
用 `PinBase.Location`（相对偏移）算绝对坐标，让 B 的第 0 点精确落到 A 的第 0 点：

```
absoluteA0 = srA.Location + pinsA[0].Location
target.insertion = absoluteA0 - pinsB[0].Location
srB.Location = target.insertion
```
注意：`PinBase.Location` 是**相对**偏移（文档原文），务必先加 `sr.Location` 再反过来减，不能直接当绝对值用。

**验收**：`srA.GraphicalConnections.Length >= 1`。

若 2B 失败，按序再试：
- 两个 PinBase 的 `Direction` 必须相对（一个左一个右 / 一个上一个下）；同向不连。挑 (check) 方向相反的一对。
- 距离不为 0：改为把 B 的连线之后再插；或先 `PlaceAsConnectionDefinitionPoint` 在两点之间落一个连接定义点。
- 仍然不行 → **记录结论，转 2A 主线**，不要在这里耗。

---

### 阶段 3 — 最小可用电路

目标回路（三相）：

```
L1/L2/L3 → -Q1 断路器 → -K1 接触器主触点 → -F1 热继电器 → -M1 电机
控制： -S1 启动 / -S2 停止 → -K1 线圈
```

输入改成数据表驱动（硬编码在 `MotorsDemo.cs` 里即可），跑一次同时启用 L+G 两路线。

**验收**：
1. 探针列该页全部 `Function.Name` / `FUNC_IDENTDEVICETAG`，应看到 `-Q1 -K1 -F1 -M1 -S1 -S2`
2. 每对设计中相邻的设备，`new Connection(...)` 后都能读回 `StartPin/EndPin`
3. `SymbolReference.GraphicalConnections` 总数 ≥ 10（若 G 生效）
4. 导出 PDF 目视检查

---

### 阶段 4 — 参数化引擎

输入 JSON：

```json
{
  "project": "D:\\EPLAN\\项目\\Linrrrx\\ABB变频控制.elk",
  "page": { "name": "5", "type": "Circuit" },
  "symbols": [
    { "id": "Q1", "library": "IEC_symbol", "symbol": "XXX", "variant": 0,
      "at": [100, 100], "deviceTag": "-Q1", "category": "CircuitBreaker" }
  ],
  "connections": [
    { "from": "Q1.1", "to": "K1.1" },
    { "from": "Q1.2", "to": "K1.3" }
  ],
  "post": ["CorrectConnections", "DesignateConnections"]
}
```

`Q1.1` = 设备 id + 连接点序号。`connections` 一律走路线 L（可靠），摆放坐标只管图面。

---

## 4. 目录结构与文件清单

```
D:\默认存储空间\EPLAN\EplanDraw\
├── EplanDraw.csproj          # 复制 EplanProbe.csproj，改 TargetName
├── Draw.cs                   # 静态入口 Draw.Run()，按 C:\Temp\draw_mode.txt 分派
├── Stage0Probe.cs            # 符号字典 / 坐标系 / 页面尺寸
├── Stage1Place.cs            # 单符号
├── Stage2Connect.cs          # 2A + 2B
├── Stage3Circuit.cs          # 三相电机回路 demo
├── Engine.cs                 # JSON → 图纸
└── out\EplanDraw.dll

C:\Temp\EplanScripts\LoadEplanDraw.cs   # 加载脚本（照抄 LoadProbe.cs）
C:\Temp\draw_mode.txt                   # stage0 / stage1 / stage2 / stage3 / engine
C:\Temp\eplan_target.txt                # 目标 .elk 路径
C:\Temp\draw_result.txt                 # 每次运行的结果报告
```

---

## 5. 迭代循环（每次改代码走一遍，全程不重启 EPLAN）

```bash
# 1. 编译
dotnet build "D:\默认存储空间\EPLAN\EplanDraw\EplanDraw.csproj" -c Release

# 2. 拷 DLL（Assembly.Load(byte[]) 会吃住文件，确保路径固定）
cp out/EplanDraw.dll /c/Temp/EplanDraw/EplanDraw.dll

# 3. 触发
"D:\默认存储空间\EPLAN\EplanRemote\bin\Release\EplanRemote.exe" \
  ExecuteScript ScriptFile C:\Temp\EplanScripts\LoadEplanDraw.cs

# 4. 看结果
cat /c/Temp/draw_result.txt
```

**硬约束（重抄一遍防踩）**
- 脚本里不写 `using`，用完全限定名；必须 `Assembly.Load(byte[])`，不能 `LoadFrom`
- 工程必须在 GUI 里关掉，走 `OpenProject(Exclusive)`；被占先执行 `XPrjActionProjectClose`（`Succeed=False` 也生效）
- 逻辑页上的符号不能 `Scale`
- 图框改不动，别试了
- 改之前备份：`<名>.edb` 目录 + `<名>.elk`
- 所有输出 UTF-8 写文件

---

## 6. 风险表

| 风险 | 概率 | 影响 | 兜底 |
| --- | --- | --- | --- |
| 路线 G（自动连线）不触发 | 中 | 图面无导线，但电气逻辑在 | 走路线 L；定义点 `PlaceAsConnectionDefinitionPoint` 补齐 |
| IEC_symbol 里找不到合适符号名 | 中 | 阶段 1 卡住 | 阶段 0 先把字典 dump 出来，用现成名字，不猜 |
| `SymbolReference.Create` 返回的是 `Function` 还是别的子类不确定 | 中 | 类型判断写错 | 运行时打印 `sr.GetType().FullName`，按实际类型 `as` 转换 |
| 之后 `OpenProject` 被 GUI 抢占 | 高 | 每次都失败 | 跑前先 `XPrjActionProjectClose` |
| 多人/Agent 同时调用 49152 | 低 | 死锁 | 串行即可 |

---

## 7. 下一步

需要我进入阶段 0 吗？回一个「跑」字，我直接建 `EplanDraw` 工程、写 `Stage0Probe`、编译并执行，结果写到 `C:\Temp\draw_result.txt`。
