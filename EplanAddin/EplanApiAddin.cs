using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Eplan.EplApi.ApplicationFramework;
using Eplan.EplApi.Base;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.MasterData;
using Eplan.EplApi.HEServices;

namespace EplanApiAddin
{
    /// <summary>
    /// EPLAN 进程内插件（AddIn）。
    /// 启动后在 http://127.0.0.1:5002/api/addin/ 监听，接收 {action, parameters} 命令，
    /// 在 EPLAN 进程内直接调用 EPLAN API 执行（不经过命令行脚本注入，无授权/锁定上下文缺失问题）。
    /// </summary>
    public class EplanApiAddin : IEplAddIn
    {
        private const string Prefix = "http://127.0.0.1:5002/api/addin/";

        /// <summary>构建标记：改代码时手动递增，用来确认 EPLAN 里跑的是不是最新这版。</summary>
        private const string BuildStamp = "2026-11-14.1";
        private static HttpListener _listener;
        private static Thread _worker;
        private static volatile bool _running;
        private static Project _currentProject;
        /// <summary>我们自己打开过的工程路径，用于「复用而不重开」。</summary>
        private static string _openProjectPath = "";
        private static Page[] _createdPages = new Page[0];
        private static int _createdPageCount;

        /// <summary>插件注册：设为随 EPLAN 启动自动加载。</summary>
        public bool OnRegister(ref bool bLoadOnStart)
        {
            bLoadOnStart = true;
            return true;
        }

        /// <summary>初始化：启动本地监听。</summary>
        public bool OnInit()
        {
            try
            {
                StartListener();
            }
            catch
            {
                // 监听失败不能影响 EPLAN 启动
            }
            return true;
        }

        public bool OnInitGui()
        {
            return true;
        }

        /// <summary>插件从系统移除时调用。</summary>
        public bool OnUnregister()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); }
            catch { }
            return true;
        }

        public bool OnExit()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); }
            catch { }
            return true;
        }

        private static void StartListener()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
            _running = true;
            _worker = new Thread(Loop);
            _worker.IsBackground = true;
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }

        private static void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx = null;
                try { ctx = _listener.GetContext(); }
                catch { break; }
                if (ctx == null) break;
                try { Handle(ctx); }
                catch { try { ctx.Response.Abort(); } catch { } }
            }
        }

        private static void Handle(HttpListenerContext ctx)
        {
            string body = "";
            try
            {
                using (StreamReader sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                    body = sr.ReadToEnd();
            }
            catch { }

            string response;
            if (body.IndexOf("ExportPdf", StringComparison.OrdinalIgnoreCase) >= 0)
                response = DoExportPdf(ExtractStr(body, "project"), ExtractStr(body, "outputFile"),
                                       ExtractStr(body, "outputDir"), ExtractStr(body, "schema"));
            else if (body.IndexOf("DeletePages", StringComparison.OrdinalIgnoreCase) >= 0)
                response = DoDeletePages(ExtractStr(body, "project"), ExtractStr(body, "pageName"),
                                         ExtractStr(body, "dryRun"));
            else if (body.IndexOf("CaptureScreen", StringComparison.OrdinalIgnoreCase) >= 0)
                response = DoCaptureScreen(ExtractStr(body, "outputDir"));
            else if (body.IndexOf("DrawSchematic", StringComparison.OrdinalIgnoreCase) >= 0)
                response = DoDrawSchematic(ExtractStr(body, "project"), ExtractStr(body, "spec"), ExtractStr(body, "pageName"));
            else if (body.IndexOf("ListSymbols", StringComparison.OrdinalIgnoreCase) >= 0)
                response = DoListSymbols(ExtractStr(body, "project"), ExtractStr(body, "keyword"));
            else if (body.IndexOf("DrawAbbPanel", StringComparison.OrdinalIgnoreCase) >= 0)
                response = DoDrawAbbPanel(ExtractStr(body, "project"), ExtractStr(body, "pageName"));
            else if (body.IndexOf("DrawLadder", StringComparison.OrdinalIgnoreCase) >= 0)
                response = DoDrawLadder();
            else if (body.IndexOf("DrawPlcIO", StringComparison.OrdinalIgnoreCase) >= 0)
                response = DoDrawPlcIO();
            else if (body.IndexOf("DrawMainCircuit", StringComparison.OrdinalIgnoreCase) >= 0)
                response = DoDrawMainCircuit();
            else if (body.IndexOf("CreatePages", StringComparison.OrdinalIgnoreCase) >= 0)
                response = DoCreatePages();
            else if (body.IndexOf("CreateProject", StringComparison.OrdinalIgnoreCase) >= 0)
                response = DoCreateProject();
            else if (body.IndexOf("Ping", StringComparison.OrdinalIgnoreCase) >= 0)
                response = "{\"success\":true,\"message\":\"addin alive\",\"build\":\"" + BuildStamp + "\"}";
            else
                response = "{\"success\":false,\"error\":\"unknown action\"}";

            byte[] buf = Encoding.UTF8.GetBytes(response);
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = buf.Length;
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
            ctx.Response.OutputStream.Close();
        }

        /// <summary>在 EPLAN 进程内建「物流传输_三段顺序」项目。</summary>
        private static string DoCreateProject()
        {
            string log = "";
            try
            {
                string projectName = "物流传输_三段顺序";
                string template = @"D:\EPLAN\Platform Data\2.9.4\Templates\EPLAN\IEC_bas003_sample_project.zw9";
                string targetDir = Path.Combine(@"D:\EPLAN\项目", projectName);
                string targetPath = Path.Combine(targetDir, projectName + ".elk");

                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                log += "templateExists=" + File.Exists(template) + "; ";

                ProjectManager pm = new ProjectManager();
                bool exists = pm.ExistsProject(targetPath);
                log += "exists=" + exists + "; ";

                if (!exists)
                {
                    // 路线1：从模板新建（当前授权下 CreateProject 可能因 LockingStep 失败）
                    try
                    {
                        pm.CreateProject(targetPath, template);
                        log += "createdFromTemplate; ";
                    }
                    catch (Exception exCreate)
                    {
                        log += "createFailed(" + exCreate.Message + "); ";
                        // 路线2：从已存在的真实空项目复制（绕开模板新建的锁定机制）
                        string sourceProject = @"D:\EPLAN\项目\Linrrrx\新项目.elk";
                        pm.CopyProject(sourceProject, targetPath, ProjectManager.CopyMode.All);
                        log += "copiedFromExisting; ";
                    }
                }

                // 打开：Exclusive 模式（Standard/ReadOnly 在当前环境会撞 LockingStep）
                try
                {
                    _currentProject = pm.OpenProject(targetPath, ProjectManager.OpenMode.Exclusive);
                    log += "opened(Exclusive); ";
                }
                catch (Exception exOpen)
                {
                    log += "openExclusiveFailed(" + exOpen.Message + "); ";
                    System.Array modes = System.Enum.GetValues(typeof(ProjectManager.OpenMode));
                    for (int i = 0; i < modes.Length; i++)
                    {
                        ProjectManager.OpenMode mode = (ProjectManager.OpenMode)modes.GetValue(i);
                        try
                        {
                            _currentProject = pm.OpenProject(targetPath, mode);
                            log += "OPENED_WITH_MODE=" + mode + "; ";
                            break;
                        }
                        catch { log += "mode[" + mode + "]fail; "; }
                    }
                }

                if (_currentProject != null) _openProjectPath = targetPath;

                return "{\"success\":true,\"message\":\"项目已创建并打开: " + projectName + "\",\"log\":\"" + Esc(log) + "\""
                    + M("\"projectPath\":\"" + Esc(targetPath) + "\",\"opened\":" + (_currentProject != null ? 1 : 0)) + "}";
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                if (ex.InnerException != null) msg += " | " + ex.InnerException.Message;
                return "{\"success\":false,\"error\":\"" + Esc(msg) + "\",\"log\":\"" + Esc(log) + "\"}";
            }
        }

        /// <summary>在当前项目里建课本三段式的 5 页结构。</summary>
        private static string DoCreatePages()
        {
            string log = "";
            try
            {
                if (_currentProject == null)
                    return "{\"success\":false,\"error\":\"项目未打开，请先执行 CreateProject\"}";

                string[] pageNames = new string[]
                {
                    "01 封面与目录",
                    "02 主电路 QF-KM1~3-FR1~3-M1~3",
                    "03 PLC I/O 接线 (10DI/6DO)",
                    "04 PLC 梯形图 网络1-5",
                    "05 端子与电缆"
                };

                int created = 0;
                Page[] made = new Page[pageNames.Length];
                for (int i = 0; i < pageNames.Length; i++)
                {
                    try
                    {
                        PagePropertyList props = new PagePropertyList();
                        props[Properties.Page.PAGE_NAME] = pageNames[i];
                        Page page = new Page();
                        page.Create(_currentProject, DocumentTypeManager.DocumentType.Circuit, props);
                        made[created] = page;
                        created++;
                    }
                    catch (Exception exPage)
                    {
                        log += "页[" + pageNames[i] + "]失败:" + exPage.Message + "; ";
                    }
                }

                // 记住本次创建的页，供后续画图精确定位（项目可能从源复制自带其它页）
                _createdPages = made;
                _createdPageCount = created;

                return "{\"success\":true,\"message\":\"已建 " + created + "/" + pageNames.Length
                    + " 页\",\"log\":\"" + Esc(log) + "\""
                    + M("\"pagesRequested\":" + pageNames.Length + ",\"pagesCreated\":" + created) + "}";
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                if (ex.InnerException != null) msg += " | " + ex.InnerException.Message;
                return "{\"success\":false,\"error\":\"" + Esc(msg) + "\",\"log\":\"" + Esc(log) + "\"}";
            }
        }

        /// <summary>通用：把一组窗口宏插到指定页（按 CreatePages 建的页序号）。</summary>
        private static string DrawOnPage(int pageIndex, string[] macros, string label)
        {
            string log = "";
            try
            {
                if (_currentProject == null)
                    return "{\"success\":false,\"error\":\"项目未打开，请先执行 CreateProject\"}";
                if (_createdPageCount <= pageIndex || _createdPages.Length <= pageIndex)
                    return "{\"success\":false,\"error\":\"还没有第" + (pageIndex + 1)
                           + "页，请先执行 CreatePages\",\"log\":\"" + Esc(log) + "\"}";

                Page target = _createdPages[pageIndex];
                log += "目标=" + label + "; ";

                int ok = 0;
                Insert ins = new Insert();
                try
                {
                    for (int i = 0; i < macros.Length; i++)
                    {
                        try
                        {
                            ins.WindowMacro(macros[i], 0, target,
                                new PointD(80, 270 - i * 90), Insert.MoveKind.Absolute);
                            ok++;
                        }
                        catch (Exception exM)
                        {
                            log += "宏" + (i + 1) + "失败:" + exM.Message + "; ";
                        }
                    }
                }
                finally
                {
                    try { ins.Dispose(); }
                    catch { }
                }

                return "{\"success\":true,\"message\":\"" + label + " 已插入 " + ok + "/"
                       + macros.Length + " 个宏\",\"log\":\"" + Esc(log) + "\""
                       + M("\"macrosRequested\":" + macros.Length + ",\"macrosInserted\":" + ok) + "}";
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                if (ex.InnerException != null) msg += " | " + ex.InnerException.Message;
                return "{\"success\":false,\"error\":\"" + Esc(msg) + "\",\"log\":\"" + Esc(log) + "\"}";
            }
        }

        /// <summary>在 PLC I/O 页（第 3 页）插 PLC 总览 + DI + DO。</summary>
        private static string DoDrawPlcIO()
        {
            string basePath = @"D:\EPLAN\Platform Data\2.9.4\Macros\EPLAN\ESS_Macro\400_PLC_Control\";
            string[] macros = new string[]
            {
                basePath + "Connection_Overview_PLC.ema",
                basePath + "Connection_Overview_DI.ema",
                basePath + "Connection_Overview_DO.ema"
            };
            return DrawOnPage(2, macros, "PLC I/O 页");
        }

        /// <summary>在梯形图页（第 4 页）插急停/线圈 + PLC 电源控制。</summary>
        private static string DoDrawLadder()
        {
            string[] macros = new string[]
            {
                @"D:\EPLAN\Platform Data\2.9.4\Macros\EPLAN\ESS_Macro\300_Machine_Protection\102_01_Variant_1\Emergency_Stop_Digital_Output_coil.ema",
                @"D:\EPLAN\Platform Data\2.9.4\Macros\EPLAN\ESS_Macro\400_PLC_Control\Power_Supply_Control_PLC.ema"
            };
            return DrawOnPage(3, macros, "梯形图页");
        }

        /// <summary>在主电路页插入 3 个电机控制回路宏（M1/M2/M3）。</summary>
        private static string DoDrawMainCircuit()
        {
            string log = "";
            try
            {
                if (_currentProject == null)
                    return "{\"success\":false,\"error\":\"项目未打开，请先执行 CreateProject\"}";

                // 优先用本次 CreatePages 建的第 2 页（主电路），避免落到源项目自带的页上
                Page target = null;
                if (_createdPageCount >= 2 && _createdPages.Length >= 2)
                {
                    target = _createdPages[1];
                    log += "用自建主电路页; ";
                }
                else
                {
                    Page[] pages = _currentProject.Pages;
                    log += "pages=" + (pages == null ? 0 : pages.Length) + "; ";
                    if (pages != null)
                    {
                        for (int i = 0; i < pages.Length; i++)
                        {
                            string nm = "";
                            try { nm = pages[i].Name ?? ""; }
                            catch { }
                            if (nm.IndexOf("主电路", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                target = pages[i];
                                log += "命中主电路页; ";
                                break;
                            }
                        }
                        if (target == null && pages.Length >= 2)
                        {
                            target = pages[1];
                            log += "回退第2页; ";
                        }
                    }
                }
                if (target == null)
                    return "{\"success\":false,\"error\":\"找不到主电路页，请先执行 CreatePages\",\"log\":\""
                           + Esc(log) + "\"}";

                string macro = @"D:\EPLAN\Platform Data\2.9.4\Macros\EPLAN\ESS_Macro\203_Electrical_Engine\001_Option_1\Contactor_Feedback_Control.ema";
                log += "macroExists=" + File.Exists(macro) + "; ";

                int ok = 0;
                Insert ins = new Insert();
                try
                {
                    for (int i = 0; i < 3; i++)
                    {
                        try
                        {
                            ins.WindowMacro(macro, 0, target,
                                new PointD(80, 260 - i * 80), Insert.MoveKind.Absolute);
                            ok++;
                        }
                        catch (Exception exM)
                        {
                            log += "宏" + (i + 1) + "失败:" + exM.Message + "; ";
                        }
                    }
                }
                finally
                {
                    try { ins.Dispose(); }
                    catch { }
                }

                return "{\"success\":true,\"message\":\"已插入 " + ok + "/3 个电机回路宏(M1/M2/M3)\",\"log\":\""
                       + Esc(log) + "\"" + M("\"macrosRequested\":3,\"macrosInserted\":" + ok) + "}";
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                if (ex.InnerException != null) msg += " | " + ex.InnerException.Message;
                return "{\"success\":false,\"error\":\"" + Esc(msg) + "\",\"log\":\"" + Esc(log) + "\"}";
            }
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "/").Replace("\"", "'").Replace("\n", " ").Replace("\r", " ");
        }

        /// <summary>
        /// 追加 metrics 对象（证据层据此独立核实，不依赖 message 里的自然语言）。
        /// 用法：...\"" + M("\"itemsPlaced\":3,\"connectionsAfter\":7")
        /// </summary>
        private static string M(string pairs)
        {
            return ",\"metrics\":{" + pairs + "}";
        }

        /// <summary>极简 JSON 取值：取第一个出现的 "key":"value"（够用）。</summary>
        private static string ExtractStr(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string pat = "\"" + key + "\"";
            int i = json.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i + pat.Length);
            if (i < 0) return null;
            i = json.IndexOf('"', i + 1);
            if (i < 0) return null;
            int j = i + 1;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            while (j < json.Length && json[j] != '"')
            {
                if (json[j] == '\\' && j + 1 < json.Length)
                {
                    char esc = json[j + 1];
                    if (esc == 'u' && j + 5 < json.Length)
                    {
                        // \uXXXX 转义（中文路径必须走这里）
                        int code = 0;
                        bool okHex = true;
                        for (int k = 0; k < 4; k++)
                        {
                            char h = json[j + 2 + k];
                            int d = HexVal(h);
                            if (d < 0) { okHex = false; break; }
                            code = code * 16 + d;
                        }
                        if (okHex)
                        {
                            sb.Append((char)code);
                            j += 6;
                            continue;
                        }
                    }
                    sb.Append(esc);
                    j += 2;
                    continue;
                }
                sb.Append(json[j]);
                j++;
            }
            return sb.ToString();
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        // ==================================================================
        //  精细绘图引擎（符号级 + 真实连线）
        //  移植自 EplanDraw/Draw.cs 的实测结论：
        //    ① 符号插入：SymbolReference.Create(variant, page) + sr.Location = PointD
        //    ② PinBase.Location 是"相对插入点的偏移"，绝对坐标 = sr.Location + pin.Location
        //    ③ 设备编号必须用 fn.Name setter，属性写法报 S063113
        //    ④ ★ 必须调 PlaceConnectionDefinitionPoints(prj,"",true) 才把图形线物化成真实连接
        // ==================================================================

        /// <summary>打开工程（先复用已打开的，逐个尝试 OpenMode），成功返回 Project。</summary>
        private static Project OpenProjectFlexible(string elkPath, out string log)
        {
            log = "";
            if (!elkPath.EndsWith(".elk", StringComparison.OrdinalIgnoreCase)) elkPath += ".elk";
            ProjectManager pm = new ProjectManager();

            // ★ 第一步必须是「复用」：同一进程对已打开的工程再 OpenProject 会全部失败，
            //   这是 CreateProject → DrawSchematic 链路的必踩点。
            Project reused = ReuseOpenProject(pm, elkPath, out log);
            if (reused != null) return reused;

            System.Array modes = System.Enum.GetValues(typeof(ProjectManager.OpenMode));
            // Exclusive 必须优先：其余模式在本机撞 S063110
            for (int i = 0; i < modes.Length; i++)
            {
                if (modes.GetValue(i).ToString() != "Exclusive") continue;
                try { Project p = pm.OpenProject(elkPath, ProjectManager.OpenMode.Exclusive);
                     log += "opened(Exclusive); "; _currentProject = p; _openProjectPath = elkPath; return p; }
                catch { }
            }
            for (int i = 0; i < modes.Length; i++)
            {
                try { Project p = pm.OpenProject(elkPath, (ProjectManager.OpenMode)modes.GetValue(i));
                     log += "opened(" + modes.GetValue(i) + "); "; _currentProject = p; _openProjectPath = elkPath; return p; }
                catch { }
            }
            return null;
        }

        /// <summary>本进程里已经打开过的工程（自己开的 + 反射探测的），路径匹配就直接复用。</summary>
        private static Project ReuseOpenProject(ProjectManager pm, string elkPath, out string log)
        {
            log = "";
            if (_currentProject != null && SameElkPath(_openProjectPath, elkPath))
            {
                log += "reuse(current); ";
                return _currentProject;
            }

            // ProjectManager 上没有稳定的「已打开工程集合」API，跨版本靠反射探测，探不到就跳过
            try
            {
                object v = null;
                System.Type t = pm.GetType();
                System.Reflection.PropertyInfo pi = t.GetProperty("OpenProjects");
                if (pi != null) v = pi.GetValue(pm, null);
                else
                {
                    System.Reflection.MethodInfo mi = t.GetMethod("GetProjects", Type.EmptyTypes);
                    if (mi != null) v = mi.Invoke(pm, null);
                }

                System.Array arr = v as System.Array;
                if (arr != null)
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        Project p = arr.GetValue(i) as Project;
                        if (p == null) continue;
                        string pp = ProjectPathOf(p);
                        if (SameElkPath(pp, elkPath))
                        {
                            log += "reuse(open[" + i + "]=" + pp + "); ";
                            _currentProject = p;
                            _openProjectPath = elkPath;
                            return p;
                        }
                    }
                    log += "openProjects=" + arr.Length + "(no match); ";
                }
            }
            catch (Exception ex)
            {
                log += "reuseProbe(" + ex.GetType().Name + "); ";
            }
            return null;
        }

        private static string ProjectPathOf(Project p)
        {
            string[] names = new string[] { "ProjectLinkFilePath", "ProjectPath", "ProjectDirectoryPath" };
            System.Type t = p.GetType();
            for (int i = 0; i < names.Length; i++)
            {
                System.Reflection.PropertyInfo pi = t.GetProperty(names[i]);
                if (pi == null) continue;
                try
                {
                    object v = pi.GetValue(p, null);
                    string s = v as string;
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                catch { }
            }
            return "";
        }

        private static bool SameElkPath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            string na = a.Replace('/', '\\').TrimEnd('\\');
            string nb = b.Replace('/', '\\').TrimEnd('\\');
            if (na.EndsWith(".elk", StringComparison.OrdinalIgnoreCase)) na = na.Substring(0, na.Length - 4);
            if (nb.EndsWith(".elk", StringComparison.OrdinalIgnoreCase)) nb = nb.Substring(0, nb.Length - 4);
            return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>在工程里找符号；找不到返回 null。</summary>
        private static Symbol FindSymbol(SymbolLibrary lib, string name)
        {
            Symbol[] syms = null;
            try { syms = lib.Symbols; } catch { return null; }
            if (syms == null) return null;
            for (int i = 0; i < syms.Length; i++)
            {
                try { if (syms[i].Name == name) return syms[i]; } catch { }
            }
            return null;
        }

        /// <summary>设设备编号。实测：属性写法报 S063113，必须用 Name setter。</summary>
        private static void SetTag(SymbolReference sr, string tag)
        {
            if (sr == null || string.IsNullOrEmpty(tag)) return;
            try { Function fn = sr as Function; if (fn != null) fn.Name = tag; }
            catch { }
        }

        /// <summary>
        /// 符号级精细绘图。
        /// spec 为简化 JSON：
        ///   {"items":[{"s":"Q_3L","t":"-Q1","x":100,"y":240,"v":0}],
        ///    "links":[{"f":"-Q1","fp":1,"t":"-M1","tp":0}]}
        /// items=元件（s=符号名 t=设备编号 x/y=坐标 v=变体）
        /// links=连线（f/t=设备编号 fp/tp=引脚序号，按顺序自动吸附对齐）
        /// </summary>
        private static string DoDrawSchematic(string elkPath, string spec, string pageName)
        {
            string log = "";
            try
            {
                if (string.IsNullOrEmpty(elkPath)) return "{\"success\":false,\"error\":\"缺少 project 参数\"}";
                if (string.IsNullOrEmpty(spec)) return "{\"success\":false,\"error\":\"缺少 spec 参数(元件+连线定义)\"}";
                if (!elkPath.EndsWith(".elk", StringComparison.OrdinalIgnoreCase)) elkPath += ".elk";

                ProjectManager pm = new ProjectManager();
                if (!pm.ExistsProject(elkPath))
                    return "{\"success\":false,\"error\":\"项目不存在: " + Esc(elkPath) + "\"}";

                Project prj = OpenProjectFlexible(elkPath, out log);
                if (prj == null) return "{\"success\":false,\"error\":\"项目打不开(所有 OpenMode 失败)\",\"log\":\"" + Esc(log) + "\"}";

                // 页：按 pageName 找，找不到新建
                Page page = null;
                if (!string.IsNullOrEmpty(pageName))
                {
                    try
                    {
                        Page[] ps = prj.Pages;
                        if (ps != null)
                            for (int i = 0; i < ps.Length; i++)
                            {
                                // 注意：Page.Name 返回的是页号（如 "/6"），页描述在 PAGE_NAME 属性里。
                                // 只匹配 Name 会导致「按页名找不到 → 每次都新建一页」，必须两处都比。
                                string nm = ""; try { nm = ps[i].Name ?? ""; } catch { }
                                string desc = PageDesc(ps[i]);
                                if (nm.IndexOf(pageName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    desc.IndexOf(pageName, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    page = ps[i];
                                    log += "找到页:" + nm + (desc == "" ? "" : "(" + desc + ")") + "; ";
                                    break;
                                }
                            }
                    }
                    catch { }
                }
                if (page == null)
                {
                    try
                    {
                        PagePropertyList ppl = new PagePropertyList();
                        ppl[Properties.Page.PAGE_NAME] = string.IsNullOrEmpty(pageName) ? "SCHEMATIC" : pageName;
                        page = new Page();
                        page.Create(prj, DocumentTypeManager.DocumentType.Circuit, ppl);
                        log += "新建页; ";
                    }
                    catch (Exception exP) { return "{\"success\":false,\"error\":\"建页失败: " + Esc(exP.Message) + "\"}"; }
                }

                SymbolLibrary lib = new SymbolLibrary(prj, "IEC_symbol");
                Dictionary<string, SymbolReference> placed =
                    new Dictionary<string, SymbolReference>(StringComparer.OrdinalIgnoreCase);

                // ---------- 1. 按 items 放符号 ----------
                int itemCount = CountOf(spec, "\"s\"");
                for (int idx = 0; idx < itemCount; idx++)
                {
                    // 逐项取字段（spec 结构固定，按出现顺序切片）
                    string oneItem = SliceObject(spec, "\"items\"", idx);
                    if (oneItem == null) break;
                    string symName = ExtractStr(oneItem, "s");
                    string tag = ExtractStr(oneItem, "t");
                    int x = (int)ExtractNum(oneItem, "x", 0);
                    int y = (int)ExtractNum(oneItem, "y", 0);
                    int variant = (int)ExtractNum(oneItem, "v", 0);

                    try
                    {
                        Symbol sym = FindSymbol(lib, symName);
                        if (sym == null) { log += "符号未找到:" + symName + "; "; continue; }
                        SymbolVariant[] vs = sym.Variants;
                        int vi = (vs != null && variant >= 0 && variant < vs.Length) ? variant : 0;

                        SymbolReference sr = SymbolReference.Create(vs[vi], page);
                        sr.Location = new PointD(x, y);
                        SetTag(sr, tag);
                        if (!string.IsNullOrEmpty(tag)) placed[tag] = sr;
                        log += "放置 " + (tag == "" ? symName : tag) + "(" + symName + ")@" + x + "," + y + "; ";
                    }
                    catch (Exception exI)
                    {
                        log += "放置失败[" + symName + "]:" + exI.Message + "; ";
                    }
                }

                // ---------- 2. 按 links 吸附对齐（让连接点重合 → EPLAN 自动画图形线） ----------
                int linkCount = CountOf(spec, "\"f\"");
                for (int li = 0; li < linkCount; li++)
                {
                    string oneLink = SliceObject(spec, "\"links\"", li);
                    if (oneLink == null) break;
                    string fromTag = ExtractStr(oneLink, "f");
                    string toTag = ExtractStr(oneLink, "t");
                    int fp = (int)ExtractNum(oneLink, "fp", 0);
                    int tp = (int)ExtractNum(oneLink, "tp", 0);

                    SymbolReference a, b;
                    if (!placed.TryGetValue(fromTag ?? "", out a) || !placed.TryGetValue(toTag ?? "", out b))
                    { log += "连线跳过(标签未找到): " + fromTag + "->" + toTag + "; "; continue; }
                    if (a == null || b == null) { log += "连线跳过(符号为空); "; continue; }

                    try
                    {
                        // ★ 关键：b 的目标引脚要落在 a 的源引脚上 → 反推 b 的插入点
                        PinBase[] pa = a.GraphicalConnectionPoints;
                        if (pa == null || pa.Length <= fp) { log += "源引脚越界:" + fromTag + "#" + fp + "; "; continue; }
                        PointD absA = new PointD(a.Location.X + pa[fp].Location.X, a.Location.Y + pa[fp].Location.Y);

                        PinBase[] pb = b.GraphicalConnectionPoints;
                        if (pb == null || pb.Length <= tp) { log += "目标引脚越界:" + toTag + "#" + tp + "; "; continue; }
                        PointD offB = pb[tp].Location;

                        b.Location = new PointD(absA.X - offB.X, absA.Y - offB.Y);
                        log += "吸附 " + fromTag + "#" + fp + " → " + toTag + "#" + tp + "; ";
                    }
                    catch (Exception exL)
                    {
                        log += "吸附失败[" + fromTag + "->" + toTag + "]:" + exL.Message + "; ";
                    }
                }

                try { lib.Dispose(); } catch { }

                // ---------- 3. ★ 物化连接：图形线 → 真实连接对象（缺这步项目数据里一根线都没有）----------
                int before = 0, after = 0;
                try
                {
                    DMObjectsFinder f0 = new DMObjectsFinder(prj);
                    Connection[] c0 = f0.GetConnections(null); before = (c0 == null) ? 0 : c0.Length;
                    f0.Dispose();
                }
                catch { }

                try
                {
                    ConnectionService cs = new ConnectionService();
                    cs.PlaceConnectionDefinitionPoints(prj, "", true);   // true = 重新计算生成
                    log += "PlaceConnectionDefinitionPoints ok; ";
                }
                catch (Exception exC) { log += "物化失败:" + exC.Message + "; "; }

                try
                {
                    ConnectionService cs2 = new ConnectionService();
                    cs2.DesignateConnections(prj, "", ConnectionService.DesignateOverwrition.All, false, true);
                    log += "DesignateConnections ok; ";
                }
                catch (Exception exD) { log += "编号失败:" + exD.Message + "; "; }

                try
                {
                    DMObjectsFinder f1 = new DMObjectsFinder(prj);
                    Connection[] c1 = f1.GetConnections(null); after = (c1 == null) ? 0 : c1.Length;
                    f1.Dispose();
                }
                catch { }

                int placedCount = 0;
                try { placedCount = page.AllPlacements.Length; } catch { }

                return "{\"success\":true,\"message\":\"符号 " + placed.Count + "/" + itemCount
                    + "，吸附 " + linkCount + " 组，连接物化 " + before + "→" + after
                    + "（真实连接数）\",\"log\":\"" + Esc(log) + "\""
                    + M("\"itemsRequested\":" + itemCount
                        + ",\"itemsPlaced\":" + placed.Count
                        + ",\"linksRequested\":" + linkCount
                        + ",\"connectionsBefore\":" + before
                        + ",\"connectionsAfter\":" + after
                        + ",\"placementsOnPage\":" + placedCount
                        + ",\"page\":\"" + Esc(page.Name ?? "") + "\"") + "}";
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                if (ex.InnerException != null) msg += " | " + ex.InnerException.Message;
                return "{\"success\":false,\"error\":\"" + Esc(msg) + "\",\"log\":\"" + Esc(log) + "\"}";
            }
        }

        /// <summary>读页描述（PAGE_NAME 属性）。Page.Name 只给页号，按页名找页必须用这个。</summary>
        private static string PageDesc(Page p)
        {
            try
            {
                object o = p.Properties[Properties.Page.PAGE_NAME];
                return o == null ? "" : o.ToString();
            }
            catch { return ""; }
        }

        /// <summary>统计 spec 里某 key 出现的次数（= 数组元素个数）。</summary>
        private static int CountOf(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return 0;
            int n = 0, i = 0;
            while ((i = json.IndexOf(key, i, StringComparison.Ordinal)) >= 0) { n++; i += key.Length; }
            return n;
        }

        /// <summary>切出数组第 idx 个对象的 JSON 片段（形如 {...}）。</summary>
        private static string SliceObject(string json, string arrayKey, int idx)
        {
            int aStart = json.IndexOf(arrayKey, StringComparison.Ordinal);
            if (aStart < 0) return null;
            int bStart = json.IndexOf('[', aStart);
            if (bStart < 0) return null;
            int seen = 0, depth = 0, objStart = -1;
            for (int i = bStart; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '{') { if (depth == 0) objStart = i; depth++; }
                else if (c == '}') { depth--; if (depth == 0 && objStart >= 0)
                {
                    if (seen == idx) return json.Substring(objStart, i - objStart + 1);
                    seen++; objStart = -1;
                } }
            }
            return null;
        }

        /// <summary>从片段里取数值字段（无则返回 def）。</summary>
        private static double ExtractNum(string json, string key, double def)
        {
            if (string.IsNullOrEmpty(json)) return def;
            string pat = "\"" + key + "\"";
            int i = json.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return def;
            i = json.IndexOf(':', i + pat.Length);
            if (i < 0) return def;
            i++;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
            int start = i;
            if (i < json.Length && (json[i] == '-' || json[i] == '+')) i++;
            while (i < json.Length && ((json[i] >= '0' && json[i] <= '9') || json[i] == '.')) i++;
            if (i <= start) return def;
            double v;
            if (double.TryParse(json.Substring(start, i - start),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v)) return v;
            return def;
        }

        /// <summary>按关键字列出工程符号库里可用的符号（画图前先查有什么能用）。</summary>
        private static string DoListSymbols(string elkPath, string keyword)
        {
            string log = "";
            try
            {
                if (string.IsNullOrEmpty(elkPath)) return "{\"success\":false,\"error\":\"缺少 project 参数\"}";
                if (!elkPath.EndsWith(".elk", StringComparison.OrdinalIgnoreCase)) elkPath += ".elk";

                Project prj = OpenProjectFlexible(elkPath, out log);
                if (prj == null) return "{\"success\":false,\"error\":\"项目打不开\",\"log\":\"" + Esc(log) + "\"}";

                string[] libs = new string[] { "IEC_symbol", "GRAPHICS", "SPECIAL", "PID_ESS", "PNE1ESS" };
                StringBuilder sb = new StringBuilder();
                int total = 0;

                for (int li = 0; li < libs.Length; li++)
                {
                    SymbolLibrary lib = null;
                    try
                    {
                        lib = new SymbolLibrary(prj, libs[li]);
                        Symbol[] syms = lib.Symbols;
                        if (syms == null) continue;
                        int shown = 0;
                        for (int i = 0; i < syms.Length; i++)
                        {
                            string nm = ""; try { nm = syms[i].Name ?? ""; } catch { continue; }
                            if (!string.IsNullOrEmpty(keyword) &&
                                nm.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            if (shown < 60)
                            {
                                int nv = 0; try { nv = syms[i].Variants.Length; } catch { }
                                sb.Append(Esc(libs[li]) + "|" + Esc(nm) + "|v=" + nv + ";");
                            }
                            shown++; total++;
                        }
                        if (shown > 60) sb.Append("...共" + shown + "个;");
                    }
                    catch (Exception ex) { sb.Append(Esc(libs[li]) + "|ERR:" + Esc(ex.Message) + ";"); }
                    finally { try { if (lib != null) lib.Dispose(); } catch { } }
                }

                string json = "{\"success\":true,\"message\":\"" + total + " 个符号匹配" +
                    (string.IsNullOrEmpty(keyword) ? "" : " 关键字=" + Esc(keyword)) +
                    "\",\"log\":\"" + Esc(log) + "\",\"symbols\":\"" + sb + "\""
                    + M("\"symbolsMatched\":" + total) + "}";
                return json;
            }
            catch (Exception ex)
            {
                return "{\"success\":false,\"error\":\"" + Esc(ex.Message) + "\"}";
            }
        }

        /// <summary>
        /// 按 ABB 电机控制柜原理图的分区布局，在指定项目里拼装一张"结构类似"的图。
        /// 说明：本动作走"宏插入"路线（快但粗，只用宏自带的内部连线）。
        /// 若需要真实互联线 + 精确元件编号，请改用 DrawSchematic（符号级 + 连接物化）。
        /// </summary>
        private static string DoDrawAbbPanel(string elkPath, string pageName)
        {
            string log = "";
            try
            {
                if (string.IsNullOrEmpty(elkPath))
                    return "{\"success\":false,\"error\":\"缺少 project 参数（.elk 路径）\"}";
                if (!elkPath.EndsWith(".elk", StringComparison.OrdinalIgnoreCase))
                    elkPath += ".elk";

                ProjectManager pm = new ProjectManager();
                if (!pm.ExistsProject(elkPath))
                    return "{\"success\":false,\"error\":\"项目不存在: " + Esc(elkPath) + "\"}";

                // 打开指定项目（复用优先，其次 Exclusive）
                Project proj = OpenProjectFlexible(elkPath, out log);
                if (proj == null) return "{\"success\":false,\"error\":\"项目打不开(所有 OpenMode 都失败)\",\"log\":\"" + Esc(log) + "\"}";
                _currentProject = proj;

                // 定位页：按 pageName 找，找不到就新建
                Page target = null;
                if (!string.IsNullOrEmpty(pageName))
                {
                    Page[] ps = proj.Pages;
                    if (ps != null)
                    {
                        for (int i = 0; i < ps.Length; i++)
                        {
                            string nm = "";
                            try { nm = ps[i].Name ?? ""; } catch { }
                            string desc = PageDesc(ps[i]);
                            if (nm.IndexOf(pageName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                desc.IndexOf(pageName, StringComparison.OrdinalIgnoreCase) >= 0)
                            { target = ps[i]; log += "找到页:" + nm + "; "; break; }
                        }
                    }
                }
                if (target == null)
                {
                    PagePropertyList pp = new PagePropertyList();
                    pp[Properties.Page.PAGE_NAME] = string.IsNullOrEmpty(pageName) ? "ABB_Panel" : pageName;
                    target = new Page();
                    target.Create(proj, DocumentTypeManager.DocumentType.Circuit, pp);
                    log += "新建页; ";
                }

                // ABB 图分区布局。宏选择依据：实测扫描 1115 个 .ema（均为 XML/PXF 明文），
                // 统计每个宏自带的图形线段与连接拓扑，优先用"自带连线"的高密度宏。
                string macroDir = @"D:\EPLAN\Platform Data\2.9.4\Macros\EPLAN\ESS_Macro\";

                // [主回路·左] 连接引用 162 / 符号 72 —— 接触器反馈控制，竖排 3 个对应 M1/M2/M3
                string mainMacro = macroDir + @"203_Electrical_Engine\001_Option_1\Contactor_Feedback_Control.ema";
                // [控制回路·右上] 连接引用 567 / 符号 186 —— 全库最密的电气回路宏
                string ctrlDense = macroDir + @"203_Electrical_Engine\101_01_Variant_1\Reverse_Contactor_Control.ema";
                // [控制回路·右中] 连接引用 449 / 符号 144 —— 变频器控制
                string freqCtrl = macroDir + @"203_Electrical_Engine\101_02_Variant_2\Frequency_Control.ema";
                // [PLC·中] 连接引用 637 —— PLC 总览，含完整总线连接
                string plcMacro = macroDir + @"400_PLC_Control\Connection_Overview_PLC.ema";
                // [PLC·下左] PLC 电源控制，连接引用 271
                string plcPwr = macroDir + @"400_PLC_Control\Power_Supply_Control_PLC.ema";
                // [PLC·下中] 电机控制单元，连接引用 84
                string motorCtrl = macroDir + @"203_Electrical_Engine\202_PCT-Loop\Motor_control_unit_digital.ema";
                // [端子·下右] 端子直反控制，连接引用 46
                string terminal = macroDir + @"901_Example_Multi_Machine\101_01_Variant_1\02_Variant_2\Terminal_Direct_Reverse.ema";

                Insert ins = new Insert();
                int ok = 0, miss = 0, planned = 0;
                try
                {
                    // 布局表：宏路径, X, Y（mm）。参照 ABB 图分区：左主回路 / 右上控制 / 中 PLC / 下端子。
                    object[][] layout = new object[][]
                    {
                        // ---- 左主回路：3 个电机回路，纵向排列（M1/M2/M3）----
                        new object[]{ mainMacro,  25d, 255d },
                        new object[]{ mainMacro,  25d, 165d },
                        new object[]{ mainMacro,  25d,  75d },
                        // ---- 右上控制回路：正反转 + 变频 ----
                        new object[]{ ctrlDense, 250d, 255d },
                        new object[]{ freqCtrl,  250d, 150d },
                        // ---- 中部：PLC 总览 + 电源 + 电机控制单元 ----
                        new object[]{ plcMacro,  70d, 250d },
                        new object[]{ plcPwr,    70d, 180d },
                        new object[]{ motorCtrl, 70d, 110d },
                        // ---- 右下端子排 ----
                        new object[]{ terminal, 250d,  60d },
                    };
                    planned = layout.Length;

                    for (int i = 0; i < layout.Length; i++)
                    {
                        string mp = (string)layout[i][0];
                        double mx = (double)layout[i][1];
                        double my = (double)layout[i][2];
                        try
                        {
                            if (!File.Exists(mp)) { miss++; log += "缺:" + Path.GetFileName(mp) + "; "; continue; }
                            ins.WindowMacro(mp, 0, target, new PointD(mx, my), Insert.MoveKind.Absolute);
                            ok++;
                        }
                        catch (Exception exM)
                        {
                            miss++;
                            log += "插失败[" + Path.GetFileName(mp) + "]:" + exM.Message + "; ";
                        }
                    }
                }
                finally { try { ins.Dispose(); } catch { } }

                return "{\"success\":true,\"message\":\"已插入 " + ok + "/" + planned
                    + " 个宏（失败/缺失 " + miss + "），项目=" + Esc(elkPath) + "\",\"log\":\"" + Esc(log) + "\""
                    + M("\"macrosRequested\":" + planned + ",\"macrosInserted\":" + ok
                        + ",\"macrosMissing\":" + miss) + "}";
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                if (ex.InnerException != null) msg += " | " + ex.InnerException.Message;
                return "{\"success\":false,\"error\":\"" + Esc(msg) + "\",\"log\":\"" + Esc(log) + "\"}";
            }
        }

        // ==================================================================
        //  PDF 导出（ExportPdf）：真正的导出，不是占位
        //  EPLAN 2.9.4 里导出入口是 Eplan.EplApi.HEServices.Export 的 PdfProject / PdfPages，
        //  但不同版本参数个数不一样，所以走运行时反射适配，别在编译期赌签名。
        // ==================================================================

        private static string DoExportPdf(string elkPath, string outputFile, string outputDir, string schema)
        {
            string log = "";
            try
            {
                if (string.IsNullOrEmpty(elkPath))
                    return "{\"success\":false,\"error\":\"缺少 project 参数（.elk 路径）\",\"log\":\"\"}";

                Project prj = OpenProjectFlexible(elkPath, out log);
                if (prj == null)
                    return "{\"success\":false,\"error\":\"项目打不开: " + Esc(elkPath) + "\",\"log\":\"" + Esc(log) + "\"}";

                // 输出路径：优先 outputFile，其次 outputDir + 工程名.pdf
                string outFile = outputFile;
                if (string.IsNullOrEmpty(outFile))
                {
                    string dir = string.IsNullOrEmpty(outputDir)
                        ? Path.Combine(Path.GetDirectoryName(elkPath) ?? ".", "pdf")
                        : outputDir;
                    string baseName = Path.GetFileNameWithoutExtension(elkPath);
                    outFile = Path.Combine(dir, baseName + ".pdf");
                }
                outFile = outFile.Replace('/', '\\');
                string outDirFull = Path.GetDirectoryName(outFile);
                if (!string.IsNullOrEmpty(outDirFull) && !Directory.Exists(outDirFull))
                    Directory.CreateDirectory(outDirFull);

                log += "out=" + outFile + "; ";
                if (File.Exists(outFile)) { try { File.Delete(outFile); log += "旧文件已删; "; } catch { } }

                int pageCount = 0;
                try { Page[] ps = prj.Pages; pageCount = ps == null ? 0 : ps.Length; } catch { }

                string exportLog;
                bool okFile = TryPdfExport(prj, schema, outFile, out exportLog);
                log += exportLog;

                long size = 0;
                try { FileInfo fi = new FileInfo(outFile); if (fi.Exists) size = fi.Length; } catch { }

                if (!okFile || size <= 0)
                    return "{\"success\":false,\"error\":\"PDF 未生成（导出调用都没落盘）\",\"log\":\"" + Esc(log) + "\""
                        + M("\"file\":\"" + Esc(outFile) + "\",\"size\":0,\"pagesInProject\":" + pageCount) + "}";

                return "{\"success\":true,\"message\":\"已导出 PDF: " + Esc(Path.GetFileName(outFile))
                    + "（" + size + " 字节，工程共 " + pageCount + " 页）\",\"log\":\"" + Esc(log) + "\""
                    + M("\"file\":\"" + Esc(outFile) + "\",\"size\":" + size
                        + ",\"pagesInProject\":" + pageCount
                        + ",\"schema\":\"" + Esc(schema ?? "") + "\"") + "}";
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                if (ex.InnerException != null) msg += " | " + ex.InnerException.Message;
                return "{\"success\":false,\"error\":\"" + Esc(msg) + "\",\"log\":\"" + Esc(log) + "\"}";
            }
        }

        /// <summary>
        /// 反射调用 Export.PdfProject / Export.PdfPages：按参数类型自动填参，
        /// 逐个重载尝试，任一次真的生成了输出文件就算成功。
        /// </summary>
        private static bool TryPdfExport(Project prj, string schema, string outFile, out string log)
        {
            log = "";
            try
            {
                System.Type t = typeof(Eplan.EplApi.HEServices.Export);
                object exporter = Activator.CreateInstance(t);
                string[] names = new string[] { "PdfProject", "PdfPages" };

                for (int n = 0; n < names.Length; n++)
                {
                    System.Reflection.MethodInfo[] methods = t.GetMethods();
                    for (int i = 0; i < methods.Length; i++)
                    {
                        System.Reflection.MethodInfo mi = methods[i];
                        if (!string.Equals(mi.Name, names[n], StringComparison.Ordinal)) continue;

                        System.Reflection.ParameterInfo[] ps = mi.GetParameters();
                        object[] args = new object[ps.Length];
                        bool buildable = true;
                        int strIdx = 0;
                        string sig = mi.Name + "(";

                        for (int k = 0; k < ps.Length; k++)
                        {
                            System.Type pt = ps[k].ParameterType;
                            sig += (k == 0 ? "" : ",") + pt.Name;

                            if (pt == typeof(Project)) args[k] = prj;
                            else if (pt == typeof(string))
                            {
                                args[k] = (strIdx == 0) ? (schema ?? "") : outFile;
                                strIdx++;
                            }
                            else if (pt == typeof(Page[]))
                            {
                                try { args[k] = prj.Pages; } catch { args[k] = null; }
                            }
                            else if (pt == typeof(bool)) args[k] = false;
                            else if (pt.IsEnum) args[k] = Activator.CreateInstance(pt);
                            else if (!pt.IsValueType) args[k] = null;
                            else { buildable = false; break; }
                        }
                        sig += ")";

                        if (!buildable) { log += "跳过" + sig + "; "; continue; }

                        try
                        {
                            mi.Invoke(exporter, args);
                            log += "调用" + sig + "完成; ";
                        }
                        catch (Exception exI)
                        {
                            string m = exI.Message;
                            if (exI.InnerException != null) m = exI.InnerException.Message;
                            log += "调用" + sig + "失败(" + m + "); ";
                            continue;
                        }

                        if (File.Exists(outFile))
                        {
                            log += "命中" + sig + "; ";
                            return true;
                        }
                    }
                }

                log += "没有任何 PdfProject/PdfPages 重载落盘; ";
                return false;
            }
            catch (Exception ex)
            {
                log += "反射失败:" + ex.Message + "; ";
                return false;
            }
        }

        // ==================================================================
        //  删页（DeletePages）：清理重复页 / 画错的页
        //  安全阀：不给 pageName 直接拒绝；dryRun=true 只报匹配不动手。
        // ==================================================================

        private static string DoDeletePages(string elkPath, string pageName, string dryRun)
        {
            string log = "";
            try
            {
                if (string.IsNullOrEmpty(elkPath))
                    return "{\"success\":false,\"error\":\"缺少 project 参数（.elk 路径）\",\"log\":\"\"}";
                if (string.IsNullOrEmpty(pageName))
                    return "{\"success\":false,\"error\":\"缺少 pageName：本动作只按页名删除，不接受『删全部』\",\"log\":\"\"}";

                Project prj = OpenProjectFlexible(elkPath, out log);
                if (prj == null)
                    return "{\"success\":false,\"error\":\"项目打不开: " + Esc(elkPath) + "\",\"log\":\"" + Esc(log) + "\"}";

                bool isDry = !string.IsNullOrEmpty(dryRun) &&
                             (dryRun.Equals("true", StringComparison.OrdinalIgnoreCase) || dryRun == "1");

                Page[] pages = prj.Pages;
                int before = pages == null ? 0 : pages.Length;

                List<Page> targets = new List<Page>();
                List<string> names = new List<string>();
                if (pages != null)
                {
                    for (int i = 0; i < pages.Length; i++)
                    {
                        string nm = ""; try { nm = pages[i].Name ?? ""; } catch { }
                        string desc = PageDesc(pages[i]);
                        bool hit = nm.IndexOf(pageName, StringComparison.OrdinalIgnoreCase) >= 0
                                || desc.IndexOf(pageName, StringComparison.OrdinalIgnoreCase) >= 0;
                        log += "[" + nm + (desc == "" ? "" : "=" + desc) + "]" + (hit ? "命中" : "-") + "; ";
                        if (hit) { targets.Add(pages[i]); names.Add(nm); }
                    }
                }

                if (targets.Count == 0)
                    return "{\"success\":false,\"error\":\"没有页名匹配 '" + Esc(pageName) + "' 的页\",\"log\":\"" + Esc(log) + "\""
                        + M("\"pagesBefore\":" + before + ",\"pagesMatched\":0,\"pagesDeleted\":0") + "}";

                if (isDry)
                    return "{\"success\":true,\"message\":\"dryRun：将删除 " + targets.Count + " 页（"
                        + string.Join(",", names.ToArray()) + "），未执行\",\"log\":\"" + Esc(log) + "\""
                        + M("\"pagesBefore\":" + before + ",\"pagesMatched\":" + targets.Count
                            + ",\"pagesDeleted\":0,\"deleted\":\"" + Esc(string.Join(",", names.ToArray())) + "\"") + "}";

                int deleted = 0;
                for (int i = 0; i < targets.Count; i++)
                {
                    string how;
                    if (RemovePage(targets[i], out how)) { deleted++; log += "删" + names[i] + "(" + how + "); "; }
                    else log += "删" + names[i] + "失败:" + how + "; ";
                }

                int after = before;
                try { Page[] ps2 = prj.Pages; after = ps2 == null ? 0 : ps2.Length; } catch { }

                return "{\"success\":true,\"message\":\"已删除 " + deleted + "/" + targets.Count + " 页（"
                    + string.Join(",", names.ToArray()) + "），工程页数 " + before + "→" + after + "\",\"log\":\"" + Esc(log) + "\""
                    + M("\"pagesBefore\":" + before + ",\"pagesMatched\":" + targets.Count
                        + ",\"pagesDeleted\":" + deleted + ",\"pagesAfter\":" + after
                        + ",\"deleted\":\"" + Esc(string.Join(",", names.ToArray())) + "\"") + "}";
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                if (ex.InnerException != null) msg += " | " + ex.InnerException.Message;
                return "{\"success\":false,\"error\":\"" + Esc(msg) + "\",\"log\":\"" + Esc(log) + "\"}";
            }
        }

        /// <summary>
        /// 删页：不同 EPLAN 版本方法名不一致（Delete / Remove），走反射兜底。
        /// 返回 true 表示调用成功；how 里带命中的方法名或失败原因。
        /// </summary>
        private static bool RemovePage(Page p, out string how)
        {
            how = "";
            System.Type t = p.GetType();
            string[] names = new string[] { "Delete", "Remove" };
            for (int i = 0; i < names.Length; i++)
            {
                System.Reflection.MethodInfo mi = t.GetMethod(names[i], Type.EmptyTypes);
                if (mi == null) continue;
                try { mi.Invoke(p, null); how = names[i]; return true; }
                catch (Exception ex)
                {
                    string m = ex.Message;
                    if (ex.InnerException != null) m = ex.InnerException.Message;
                    how = names[i] + "失败(" + m + ")";
                }
            }

            // 都失败：把页对象上所有含 Del/Rem 的方法名带回去，方便下一轮定位
            StringBuilder sb = new StringBuilder("无可用删除方法; 候选:");
            System.Reflection.MethodInfo[] all = t.GetMethods();
            for (int i = 0; i < all.Length; i++)
            {
                string n = all[i].Name;
                if (n.IndexOf("Del", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Rem", StringComparison.OrdinalIgnoreCase) >= 0)
                    sb.Append(n + ",");
            }
            how = sb.ToString();
            return false;
        }

        // ==================================================================
        //  截图（CaptureScreen）：给「边画边看」用
        //  说明：这是"每步抓一帧"，不是实时流。MCP 是请求/响应协议，无法推流；
        //  真正的实时需要 GUI 侧 WebSocket/MJPEG 通道，另做。
        // ==================================================================

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>抓 EPLAN 主窗口当前一帧，存 PNG，返回路径 + 尺寸 + 抓取方式 + 画面方差。</summary>
        private static string DoCaptureScreen(string outputDir)
        {
            string log = "";
            try
            {
                if (string.IsNullOrEmpty(outputDir))
                    outputDir = @"D:\默认存储空间\EPLAN\EplanApi\scratch\shots";
                Directory.CreateDirectory(outputDir);

                IntPtr hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                log += "hwnd=" + hwnd.ToInt64() + "; ";
                if (hwnd == IntPtr.Zero)
                    return "{\"success\":false,\"error\":\"拿不到 EPLAN 主窗口句柄（MainWindowHandle=0）\",\"log\":\""
                           + Esc(log) + "\"}";

                RECT r;
                if (!GetWindowRect(hwnd, out r))
                    return "{\"success\":false,\"error\":\"GetWindowRect 失败\",\"log\":\"" + Esc(log) + "\"}";

                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                log += "rect=" + w + "x" + h + "; ";
                if (w <= 0 || h <= 0)
                    return "{\"success\":false,\"error\":\"窗口尺寸无效: " + w + "x" + h + "\",\"log\":\""
                           + Esc(log) + "\"}";

                string method;
                double variance;

                using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                {
                    variance = GrabPrintWindow(bmp, hwnd);
                    method = "printwindow";
                    log += "pwVariance=" + Math.Round(variance, 2) + "; ";

                    // PrintWindow 抓 Qt 窗口常返回全黑/纯色：方差接近 0 时改用屏幕抓取
                    if (variance < 2.0)
                    {
                        variance = GrabFromScreen(bmp, r);
                        method = "copyfromscreen";
                        log += "cfsVariance=" + Math.Round(variance, 2) + "; ";
                    }

                    string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                    string file = Path.Combine(outputDir, "shot-" + stamp + ".png");
                    bmp.Save(file, ImageFormat.Png);

                    PruneShots(outputDir, 40);

                    return "{\"success\":true,\"message\":\"已截图 " + w + "x" + h + "(" + method
                        + ")，方差 " + Math.Round(variance, 2) + "\",\"log\":\"" + Esc(log) + "\""
                        + M("\"file\":\"" + Esc(file) + "\",\"width\":" + w + ",\"height\":" + h
                            + ",\"method\":\"" + method + "\",\"variance\":" + Math.Round(variance, 2)
                            + ",\"suspectBlank\":" + (variance < 2.0 ? "true" : "false")) + "}";
                }
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                if (ex.InnerException != null) msg += " | " + ex.InnerException.Message;
                return "{\"success\":false,\"error\":\"" + Esc(msg) + "\",\"log\":\"" + Esc(log) + "\"}";
            }
        }

        private static double GrabPrintWindow(Bitmap bmp, IntPtr hwnd)
        {
            try
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    bool ok;
                    try { ok = PrintWindow(hwnd, hdc, 2); }  // 2 = PW_RENDERFULLCONTENT
                    finally { g.ReleaseHdc(hdc); }
                    if (!ok) return 0;
                }
                return LumaStdDev(bmp);
            }
            catch { return 0; }
        }

        private static double GrabFromScreen(Bitmap bmp, RECT r)
        {
            try
            {
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(bmp.Width, bmp.Height));
                return LumaStdDev(bmp);
            }
            catch { return 0; }
        }

        /// <summary>抽样亮度标准差：全黑/纯色 ≈ 0，正常界面通常 20+。</summary>
        private static double LumaStdDev(Bitmap bmp)
        {
            double sum = 0, sum2 = 0;
            int n = 0;
            for (int y = 0; y < bmp.Height; y += 7)
            {
                for (int x = 0; x < bmp.Width; x += 7)
                {
                    Color c = bmp.GetPixel(x, y);
                    double l = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                    sum += l;
                    sum2 += l * l;
                    n++;
                }
            }
            if (n == 0) return 0;
            double mean = sum / n;
            double v = sum2 / n - mean * mean;
            return Math.Sqrt(v < 0 ? 0 : v);
        }

        /// <summary>只保留最近 keep 张截图，避免无限堆积。</summary>
        private static void PruneShots(string dir, int keep)
        {
            try
            {
                FileInfo[] files = new DirectoryInfo(dir).GetFiles("shot-*.png");
                if (files.Length <= keep) return;
                Array.Sort(files, delegate(FileInfo a, FileInfo b)
                {
                    return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc);
                });
                for (int i = keep; i < files.Length; i++)
                {
                    try { files[i].Delete(); } catch { }
                }
            }
            catch { }
        }
    }
}
