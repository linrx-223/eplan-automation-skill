# 任务：在 EPLAN 2.9 上实现「符号级自动绘图引擎」（方案 C）

> 这份提示词是自包含的：接手者不需要任何前置对话，读完就能开工。
> **动手前请先读 `D:\默认存储空间\EPLAN\EplanApi\使用白皮书.md` 第 14 节**（一整天的踩坑记录）。

---

## 一、最终目标

用 EPLAN 官方 API **逐个放置符号**（而不是插入窗口宏），并让符号的连接点精确对齐，使 **EPLAN 自动生成电气连接**，从而产出**真正的原理图**。

背景：现有系统只能 `Insert.WindowMacro` 把整块宏贴到页面上，**块与块之间一根线都没有**，画出来的是「器件拼贴」而非原理图。本任务要突破这个天花板。

---

## 二、环境（已确认的事实，不要重新验证）

### 机器与软件
- Windows，**EPLAN Electric P8 2.9 SP1**（内部版本 **2.9.4.14642**），中文界面
- EPLAN 安装目录：`D:\EPLAN\Platform\2.9.4\Bin\`
- 工程站点：`D:\EPLAN\项目\Linrrrx\`
- 现有测试工程：`ABB变频控制`（A0 幅面，已有一页）
- 空白工程可复制源：`D:\EPLAN\项目\Linrrrx\新项目.edb`

### EPLAN API 程序集（.NET Framework 4.8）
目录 `D:\EPLAN\Platform\2.9.4\Bin\`：

| DLL | 用途 |
| --- | --- |
| `Eplan.EplApi.DataModelu.dll` | 核心数据模型（7 MB） |
| `Eplan.EplApi.Baseu.dll` | 基础类型（PointD 等） |
| `Eplan.EplApi.AFu.dll` | ApplicationFramework |
| `Eplan.EplApi.HEServicesu.dll` | **Insert / Generate / Edit 等高级服务（本任务重点）** |
| `Eplan.EplApi.EServicesu.dll` | 扩展服务 |
| `Eplan.EplApi.RemoteClientu.dll` + `Eplan.EplApi.Remotingu.dll` | 进程外调用 |

**同名 `.xml` 文件是完整 API 文档。`Eplan.EplApi.DataModelu.xml` 有 12.7 MB —— 这是最权威的参考资料，不确定的方法签名去里面查，不要猜。**

### 编译方式（已验证可行）
net48 类库，csproj 关键配置：

```xml
<TargetFramework>net48</TargetFramework>
<FrameworkPathOverride>C:\Windows\Microsoft.NET\Framework64\v4.0.30319</FrameworkPathOverride>
<DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>
<EnableDefaultAssemblyReferences>false</EnableDefaultAssemblyReferences>
<AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
<NoWarn>CS1701;CS1702;NU1701</NoWarn>
```

- 基础引用指向 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\` 下的 `mscorlib.dll` / `System.dll` / `System.Core.dll`（本机没有 net48 targeting pack，用运行时目录当引用程序集）
- EPLAN 程序集用 `<HintPath>` 指向绝对路径 + `<Private>false</Private>`
- `dotnet build -c Release` 可直接编译成功

---

## 三、代码怎么进 EPLAN（★ 关键：全程不用重启 EPLAN）

EPLAN 脚本环境**不能引用 DataModel 程序集**（会报 CS0234），所以走「脚本加载 DLL」这条路：

### 1. 把能力写成 net48 DLL
对外暴露一个静态入口，例如 `public static void Run()`。

### 2. 用加载脚本把它注入 EPLAN 进程
模板（参考 `C:\Temp\EplanScripts\LoadProbe.cs`）：

```csharp
public class LoadEplanDraw
{
    [Start]
    public void Run()
    {
        string log = "";
        try
        {
            byte[] b = System.IO.File.ReadAllBytes(@"C:\Temp\EplanDraw\EplanDraw.dll");
            System.Reflection.Assembly asm = System.Reflection.Assembly.Load(b);
            System.Type t = asm.GetType("EplanDraw.Draw");
            System.Reflection.MethodInfo mi = t.GetMethod("Run");
            mi.Invoke(null, null);
            log = "invoked ok";
        }
        catch (System.Exception ex)
        {
            log = "ERR:" + ex.Message;
            if (ex.InnerException != null) log += " | INNER:" + ex.InnerException.Message;
        }
        try { System.IO.File.WriteAllText(@"C:\Temp\draw_load_result.txt", log); } catch { }
    }
}
```

**要点**：
- 脚本文件里**不要写 `using`**（EPLAN 会自动注入），全部用完全限定名
- 必须用 `Assembly.Load(byte[])`，**不能用 `Assembly.LoadFrom`**（同路径会走缓存，改了代码不生效）
- 脚本里的方法要标 `[Start]`

### 3. 用现成工具触发（不需要重启 EPLAN）

```
D:\默认存储空间\EPLAN\EplanRemote\bin\Release\EplanRemote.exe ExecuteScript ScriptFile C:\Temp\EplanScripts\LoadEplanDraw.cs
```

它自动发现本机正在运行的 EPLAN 服务，通过 **`EplanCommunicationServer`（TCP 49152）** 在 EPLAN 进程内执行动作。

用法：`EplanRemote.exe <动作名> [参数名 参数值]`
- 三个参数：执行动作并带一个参数
- 一个参数：按无参动作执行
- 源码见 `D:\默认存储空间\EPLAN\EplanRemote\Program.cs`

### 4. 结果写文件
EPLAN 里没有控制台，**所有输出写到文件**（如 `C:\Temp\draw_result.txt`，UTF-8）。

---

## 四、EPLAN 的已知限制（都是实测踩过的，别重复浪费时间）

1. **工程不能开在 GUI 里**：API 需要 `ProjectManager.OpenProject(path, ProjectManager.OpenMode.Exclusive)`。若工程已被 GUI 打开 → 报 `S063045 不能打开项目`。
   此时可执行 EPLAN 动作 `XPrjActionProjectClose` 关闭它（**返回值 `Succeed=False`，但确实生效**）。
2. **工程关着时**，`OpenProjects` / `CurrentProject` / `GetProject` 会报 `S063110 不能生成 LockingStep 类别的对象` —— 只有 `OpenProject(Exclusive)` 可用。
3. **逻辑页的符号不能缩放**：`Placement.Scale` 在非图形页上对符号引用抛异常（文档原话：*Symbol references on pages of type other than graphical cannot be scaled*）。
4. **页面幅面（图框）API 改不动**：写 `Properties.Page.PAGE_FORMPLOT` 会被忽略（穷举 12 个图框名、建页时指定、关掉重开验证，全部回退到 `FN1_013`）。**只能让用户在 GUI 里改**（左侧页导航器 → **右键「页面」那一行** → 属性 → 图框名称）。
5. **动作返回值不可信**：`Succeed=False` 未必失败。**必须读回状态校验**。
6. **`Group.Move(PointD)` 有 Y 轴偏差**：X 精确，Y 会少走一个对象的高度；移动宏框时要 `dy += boxH`。
7. **保存**：EPLAN 在关闭工程时会写盘；`XGedStoreAllPagesAction` 试过没成功。GUI 里 `Ctrl+S` 会弹「存储页?」，回车确认。
8. **PowerShell 脚本必须纯 ASCII**：含中文的 `.ps1` 会被按 GBK 读，直接语法错误。
9. **`.elk` 文件格式**：UTF-16（带 BOM），内容两行：`<工程名>.edb` 和 `13.0`。
10. **命令行 `/Runscript` 无效**：EPLAN 2.9.4 命令行把参数当「操作名」（报 `S085021 无法找到操作`），试过 4 种写法全部失败。只能用 49152 通信服务。
11. `Connection.eod` 的大小可以作为「有没有连线」的粗略指标（1 字节 = 无连接）。

---

## 五、任务分阶段（每阶段都必须可验证，不要跳过）

### 阶段 1 ★ 先做这个：验证「单个符号能不能放」

**要查清的事**（去 XML 文档里查）：
- `Eplan.EplApi.HEServices.Insert` 有哪些方法能放**单个符号**（不是窗口宏）
- `SymbolLibrary` / `SymbolVariant` / `Function` / `SymbolReference` 的创建方式
- 符号库位置：工程里有 `IEC_symbol.sdb`，系统符号库路径变量是 `$(MD_SYMBOLS)`

**做什么**：在 A3 逻辑页上放 **1 个符号**（例如 IEC 符号库里的一个断路器或端子），结果写报告文件。

**验收标准**：用探针读出该页 `AllPlacements` 的类型计数，确认真的多了一个符号对象。

> ⚠️ 如果这一步就发现**符号级插入在逻辑页上不可行**（比如 API 只支持窗口宏），**立刻停下写结论**，不要硬试——这本身就是有价值的答案。

### 阶段 2：两个符号 + 一条自动连线

放两个符号，**让它们的连接点精确对齐**，看 EPLAN 是否自动生成连接。

- 查连接点相关 API：`PinBase` / `GraphicalConnectionPoints` / 连接点坐标获取
- 页面栅格是 **4.00 mm**（页面属性「栅格」）
- 验证方式：读 `Connection` 数量，或对比 `Connection.eod` 大小变化

**验收标准**：连接数从 0 变成 ≥1，或 `Connection.eod` 明显变大。

### 阶段 3：画一个最小可用电路

用符号级 API 画一个完整小回路，例如：

```
三相进线 → 断路器 → 接触器主触点 → 热继电器 → 电机
                     ↑
          控制回路：启动/停止按钮 → 接触器线圈
```

要求：
- 符号有正确的**设备标识**（如 `-Q1` `-K1` `-F1` `-M1`）
- 连接点对齐，EPLAN 自动生成导线
- 输出到指定工程的新页

**验收标准**：探针能列出该页所有 Function 名称，且连接数符合预期。

### 阶段 4：参数化 + 通用化

抽象成「给一份元件清单 + 连接表，就能画出原理图」的引擎。输入格式自定（JSON 或 C# 数据结构都行）。

---

## 六、本机现成参考资料

| 路径 | 内容 |
| --- | --- |
| `D:\EPLAN\Platform\2.9.4\Bin\Eplan.EplApi.DataModelu.xml` | **12.7 MB 完整 API 文档，最权威** |
| `D:\EPLAN\Platform\2.9.4\Bin\Eplan.EplApi.HEServicesu.xml` | Insert / Generate / Edit 等服务 |
| `D:\EPLAN\Platform\2.9.4\Cfg\regPlatform.xml` | EPLAN 注册的全部动作名（4436 个），可用 EplanRemote 直接执行 |
| `D:\默认存储空间\EPLAN\EplanProbe\Probe.cs` | 现成探针：读工程结构 / 量尺寸 / 查对象 / 删对象 / 移宏框。**写法与套路直接参考它** |
| `D:\默认存储空间\EPLAN\EplanProbe\Probe.csproj` | 可用的 net48 编译配置 |
| `D:\默认存储空间\EPLAN\EplanRemote\Program.cs` | 远程调用工具源码（49152 通信服务） |
| `D:\默认存储空间\EPLAN\EplanApi\使用白皮书.md` | **第 14 节 = 完整踩坑记录，先读它** |
| `C:\Temp\EplanScripts\LoadProbe.cs` | 加载 DLL 的脚本模板 |
| `C:\Temp\EplanScripts\LoadAddIn.cs` | 同上，另一个例子 |

探针的现有能力（可直接复用其思路）：
- `inventory` 列出页面所有对象类型与数量
- `boxes` 列出每个宏框与它框住内容的坐标偏差
- `region` 按矩形区域列对象
- `findtext` 按文字内容搜对象
- `junk` 判定并删除「孤儿对象」（不在任何宏框引用列表里的对象）

---

## 七、工作方式要求

1. **改一次代码就走一次循环**：编译 → 拷贝 DLL 到 `C:\Temp\EplanDraw\` → `EplanRemote.exe ExecuteScript ScriptFile ...` → 读结果文件。**不要重启 EPLAN。**
2. **每个阶段先验证再往下走**，不要一次写完 4 个阶段。
3. 所有结论写到文件（UTF-8）。
4. 修改任何工程前**先复制备份**（复制 `<名>.edb` 目录 + `<名>.elk` 文件即可）。
5. API 方法签名不确定就**去 XML 文档查**，不要猜。
6. 不要用 PowerShell 写含中文的 `.ps1`（会被按 GBK 读而报错）。

---

## 八、交付物

1. 新的 net48 类库项目 `D:\默认存储空间\EPLAN\EplanDraw\`，对外暴露静态入口
2. 配套加载脚本 `C:\Temp\EplanScripts\LoadEplanDraw.cs`
3. 每个阶段一份结果报告（文本）
4. 一份《符号级绘图可行性结论》，必须回答：
   - 能不能放单个符号？
   - 能不能让 EPLAN 自动连线？成立条件是什么？
   - 有什么明确做不到的？
   - 如果可行，最小可用示例的代码位置与运行命令
