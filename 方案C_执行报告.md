# 方案 C 执行报告：符号级自动绘图引擎（阶段 0–3 全通）

> 实测环境：EPLAN Electric P8 2.9.4.14642，target = `D:\EPLAN\项目\Linrrrx\DrawTest.elk`（由 `新项目.edb` 复制的干净副本）
> 全部结论来自实跑，不是文档推断。原始日志：`C:\Temp\draw_result.txt`
> 阶段 3 已连续两次干净复现（重置测试工程后重跑，结果完全一致），结论稳定。

---

## 一、最终成果：一段由 API 生成的真图纸

在空白工程里用 API 一次性生成：

```
主回路(三相):  -Q1 断路器 Q_3L  ──┬──  -M1 电机 M3
                                └ 三根线，一次成型

控制回路:  -F1 熔断器 ─→ -S1 停止 ─→ -S2 启动 ─→ -K1 接触器线圈 ─→ -X1 端子
```

探针读回的结果：

| 指标 | 结果 |
| --- | --- |
| 页面上放置对象 | 7 个 |
| Function 数量 | 7，设备标识全部正确：`=+-Q1` `=+-M1` `=+-F1` `=+-S1` `=+-S2` `=+-K1` `=+-X1` |
| **生成的连接数** | **7 条，全部 `IsPlaced=True`** |
| 其中三相主回路 | 3 条（Q1→M1 的 L1/L2/L3） |
| 控制回路 | 4 条，串联成链 |

**这是真正的原理图，不是器件拼贴。** 每组连接都是 EPLAN 自己算出来的，落到项目数据里。

---

## 二、四条已实测确认的关键事实

### 1. 符号级插入可行（阶段 1）

```csharp
SymbolReference sr = SymbolReference.Create(variant, page);  // 静态方法，返回 SymbolReference
sr.Location = new PointD(x, y);
```

- 一次性实测通过 7 种符号：`X` `K` `S` `Q_3L` `M3` `F1` `KM2`
- 返回对象运行时类型是 `Eplan.EplApi.DataModel.Function`（端子是 `EObjects.Terminal`，继承自 Function），可直接 `as Function`
- `Location` 读回精确

### 2. `PinBase.Location` 是**相对插入点的偏移**（不是绝对坐标）

这是对齐算法的核心。绝对坐标必须自己算：

```
absolute = symbol.Location + pin.Location
```

### 3. 自动连线：两点坐标重合即可，且**同一直线上的轴向对准也会自动连**

实测中发现：放在 (100,200) 的 F1 和放在 (100,100) 的另一符号相隔 100 mm，**EPLAN 仍然自动画了连线**。
意味着排布不需要严丝合缝，同一列/同一行对齐即可。

但注意：**此时它们还不是"真正的连接"**，只是图形。见下条。

### 4. ★ 决定性发现：必须调 `PlaceConnectionDefinitionPoints` 才会物化

| 时刻 | `GetConnections(null)` 计数 |
| --- | --- |
| 刚画完（图形连线已存在） | **0** |
| `new Connection(prj, pinA, pinB)` 之后 | 1（但 `IsPlaced=False`，图上无线） |
| `PlaceConnectionDefinitionPoints(prj, "", true)` 之后 | **1 → 3**，图形连线全部转成 `IsPlaced=True` 的真实连接 |

> `bRegenrateConnections = true` 是关键参数：它会把图形自动连线 recalculate 成正式连接对象。
> 这一条如果不调，画出来看着连上了、但项目数据里一根线都没有 —— 报表、端子图全是空的。

---

## 三、设备标识：唯一可用的写法

| 写法 | 结果 |
| --- | --- |
| `fn.Name = "-Q1"` | ✅ 成功，读回 `=+-Q1` |
| `fn.Properties[FUNC_IDENTDEVICETAG] = ...` | ❌ `S063113 设置一个新属性值的操作失败` |

**不要用属性写法。** 直接用 `Function.Name` setter。

---

## 四、符号库现状（阶段 0 字典）

| 库 | 符号数 | Function 数 |
| --- | --- | --- |
| `IEC_symbol` | 1017 | **1012** ← 主力，用它 |
| SPECIAL | 443 | — |
| PID_ESS | 871 | — |
| PNE1ESS | 810 | — |
| GRAPHICS | 183 | — |
| IEC_topology_symbol_M20 | 74 | — |

`IEC_symbol` 只在工程的 `.edb` 里，**必须用工程模式打开**：`new SymbolLibrary(project, "IEC_symbol")`。

已确认可用的名字符号（`IEC_symbol` / variant 0）：

| 名字 | 连接点相对偏移 | 用途 |
| --- | --- | --- |
| `Q_3L` | (0,4)Up (0,-8)Down (8,4)Up (8,-8)Down (16,4)Up (16,-8)Down | 三极断路器 |
| `M3` | (0,8)Up (8,8)Up (16,8)Up (16,-8)Down | 三相电机 |
| `K` `S` `F1` `KM2` | (0,4)Up (0,-4)Down | 两引脚通用器件 |
| `X` | (0,2)Up (0,-0.75)Down | 端子 |

每个符号 **9 个变体**（variant 0–8，即标准 IEC 旋转/镜像方向组），variant 0 = 标准方向。

---

## 五、引擎的核心算法（已实现）

```csharp
// 把新符号的 newPinIdx 连接点，精确落到已放符号 prevPinIdx 连接点上
PointD abs = prev.Location + prev.GraphicalConnectionPoints[prevPinIdx].Location;
PointD off = newSymbol.Variants[0].ConnectionPoints[newPinIdx].Location;
PointD insert = abs - off;
SymbolReference sr = SymbolReference.Create(newSymbol.Variants[0], page);
sr.Location = insert;
((Function)sr).Name = tag;
```

收尾两步（缺一不可）：

```csharp
cs.PlaceConnectionDefinitionPoints(prj, "", true);   // 物化：图形连线 → 真实连接
cs.DesignateConnections(prj, "", DesignateOverwrition.All, false, true);  // 线号编号
```

---

## 六、怎么复现

```bash
# 编译
dotnet build "D:\默认存储空间\EPLAN\EplanDraw\EplanDraw.csproj" -c Release

# 部署 + 执行（全程不重启 EPLAN）
cp EplanDraw/bin/Release/EplanDraw.dll "D:\默认存储空间\EPLAN\EplanApi\scratch\EplanDraw\EplanDraw.dll"
"D:\默认存储空间\EPLAN\EplanRemote\bin\Release\EplanRemote.exe" \
  ExecuteScript ScriptFile "D:\默认存储空间\EPLAN\EplanApi\scratch\EplanScripts\LoadEplanDraw.cs"

# 看结果
cat "D:\默认存储空间\EPLAN\EplanApi\scratch\draw_result.txt"
```

模式由 `D:\默认存储空间\EPLAN\EplanApi\scratch\draw_mode.txt` 切换：`stage0`（只读字典）/ `stage1` / `stage2` / `stage2b` / `stage3`
目标工程由 `D:\默认存储空间\EPLAN\EplanApi\scratch\eplan_target.txt` 指定。

---

## 七、路径 shortcut

| 路径 | 内容 |
| --- | --- |
| `D:\默认存储空间\EPLAN\EplanDraw\` | 引擎源码 |
| `D:\默认存储空间\EPLAN\EplanApi\scratch\EplanScripts\LoadEplanDraw.cs` | 注入脚本（原 C:\Temp，已迁） |
| `D:\默认存储空间\EPLAN\EplanApi\scratch\EplanDraw\EplanDraw.dll` | 部署的引擎 DLL |
| `D:\默认存储空间\EPLAN\EplanApi\scratch\draw_result.txt` | 每次运行的结果日志 |
| `D:\默认存储空间\EPLAN\EplanApi\scratch\` | 全部临时产物归集地（DLL/脚本/日志/配置，已迁出 C:\Temp） |
| `D:\EPLAN\项目\Linrrrx\DrawTest.elk` | 测试工程（schema demo 已画在上面） |

---

## 八、下一步（阶段 4）

已经跑通的是**硬编码 demo**。要变成通用引擎只需做一层：

1. JSON 输入：元件清单（符号名 / 变体 / 锚点坐标 / 设备标识）+ 连接表（`from = 设备id.引脚号`, `to = ...`）
2. 布局求解：目前是手写坐标，可加一个简单列/行自动排布（同列垂直对齐即可触发自动连线）
3. 多页支持：`Page.Create(prj, DocumentType.Circuit, ppl)`
4. 跨页/中断点：用 `new Connection(prj, pinA, pinB)` 补 `IsPlaced=False` 的逻辑连接

**已知仍做不到的**：页面图框/幅面 API 改不动（只能在 GUI 右键「页面」行改属性）；逻辑页上的符号不能 `Scale`。
