using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Eplan.EplApi.DataModel;
using Eplan.EplApi.DataModel.MasterData;
using Eplan.EplApi.Base;

namespace EplanDraw
{
    // Loaded into the EPLAN process by C:\Temp\EplanScripts\LoadEplanDraw.cs via Assembly.Load(byte[]).
    // ASCII-only source on purpose.
    public static class Draw
    {
        private const string OutFile = @"D:\默认存储空间\EPLAN\EplanApi\scratch\draw_result.txt";
        private const string TargetFile = @"D:\默认存储空间\EPLAN\EplanApi\scratch\eplan_target.txt";
        private const string ModeFile = @"D:\默认存储空间\EPLAN\EplanApi\scratch\draw_mode.txt";

        private static StringBuilder _sb = new StringBuilder();
        private static int _lines = 0;
        private const int MaxLines = 4000;

        private static void L(string s)
        {
            if (_lines >= MaxLines) return;
            _sb.AppendLine(s);
            _lines++;
        }

        private static string ReadCfg(string path, string def)
        {
            try { if (File.Exists(path)) return File.ReadAllText(path, Encoding.UTF8).Trim(); }
            catch { }
            return def;
        }

        public static void Run()
        {
            string mode = ReadCfg(ModeFile, "stage0");
            try
            {
                if (mode == "stage0") Stage0();
                else if (mode == "stage1") Stage1();
                else if (mode == "stage2") Stage2();
                else if (mode == "stage2b") Stage2b();
                else if (mode == "stage3") Stage3();
                else L("unknown mode: " + mode);
            }
            catch (Exception ex)
            {
                L("FATAL: " + ex.Message);
                Exception ie = ex.InnerException;
                while (ie != null) { L("  INNER: " + ie.Message); ie = ie.InnerException; }
            }
            Write();
        }

        private static void Write()
        {
            try { File.WriteAllText(OutFile, _sb.ToString(), new UTF8Encoding(false)); }
            catch { }
        }

        // ------------------------------------------------------------------ stage 0

        private static readonly string[] LibCandidates = new string[]
        {
            "IEC_symbol", "GRAPHICS", "SPECIAL", "PID_ESS", "PNE1ESS", "IEC_topology_symbol_M20"
        };

        // symbol-name keywords of interest for a motor starter schematic
        private static readonly string[] Keywords = new string[]
        {
            "COIL", "CONTACT", "BREAKER", "CIRCUIT", "MOTOR", "TERMINAL", "SWITCH",
            "BUTTON", "FUSE", "RELAY", "THERMAL", "PLUG", "SOCKET", "TRANSFORMER"
        };

        private static void Stage0()
        {
            string target = ReadCfg(TargetFile, "");
            L("=== EplanDraw stage0: read-only inventory ===");
            L("target = " + (target == "" ? "(missing)" : target));
            if (target == "") return;

            ProjectManager pm = null;
            Project prj = null;
            try
            {
                pm = new ProjectManager();
                L("ProjectManager ctor ok");
                prj = pm.OpenProject(target, ProjectManager.OpenMode.Exclusive);
                L("OpenProject Exclusive ok");
                L("ProjectName     = " + Safe(() => prj.ProjectName));
                L("ProjectFullName = " + Safe(() => prj.ProjectFullName));

                DumpPages(prj);
                DumpLibraries(prj);
            }
            catch (Exception ex)
            {
                L("OPEN FAILED: " + ex.Message);
                if (ex.InnerException != null) L("  INNER: " + ex.InnerException.Message);
                return;
            }
            finally
            {
                try { if (prj != null) prj.Close(); L("project closed"); }
                catch (Exception ce) { L("close failed: " + ce.Message); }
            }
        }

        private static string Safe(Func<string> f)
        {
            try { return f(); }
            catch (Exception e) { return "<ERR:" + e.Message + ">"; }
        }

        private static void DumpPages(Project prj)
        {
            Page[] pages = null;
            try { pages = prj.Pages; } catch (Exception e) { L("Pages read failed: " + e.Message); return; }
            L("");
            L("--- pages: " + (pages == null ? -1 : pages.Length));
            if (pages == null) return;
            for (int i = 0; i < pages.Length && i < 60; i++)
            {
                Page p = pages[i];
                string name = Safe(() => p.NameParts.ToString());
                string typeName = Safe(() => p.PageTypeName);
                string size = Safe(() => { PointD s = p.Size; return "(" + s.X + "," + s.Y + ")"; });
                string cnt = Safe(() => p.AllPlacements.Length.ToString());
                L("  [" + i + "] name=" + name + " | type=" + typeName + " | size=" + size + " | placements=" + cnt);
            }
        }

        private static void DumpLibraries(Project prj)
        {
            L("");
            L("--- library open test");
            foreach (string libName in LibCandidates)
            {
                SymbolLibrary lib = null;
                try
                {
                    lib = new SymbolLibrary(prj, libName);
                    Symbol[] syms = lib.Symbols;
                    L("  " + libName + " => OK, symbols=" + (syms == null ? -1 : syms.Length));
                    if (syms != null && syms.Length > 0) DumpSymbolDict(libName, syms);
                }
                catch (Exception e)
                {
                    L("  " + libName + " => FAIL: " + e.Message);
                }
                finally
                {
                    try { if (lib != null) lib.Dispose(); } catch { }
                }
            }
        }

        private static void DumpSymbolDict(string libName, Symbol[] syms)
        {
            // type histogram
            Dictionary<string, int> hist = new Dictionary<string, int>();
            Dictionary<string, int> fnHist = new Dictionary<string, int>();
            for (int i = 0; i < syms.Length; i++)
            {
                string tn;
                try { tn = syms[i].Type.ToString(); } catch { tn = "<ERR>"; }
                hist[tn] = hist.ContainsKey(tn) ? hist[tn] + 1 : 1;
            }
            L("  [" + libName + "] type histogram:");
            foreach (KeyValuePair<string, int> kv in hist) L("     " + kv.Key + " = " + kv.Value);

            // full list (capped)
            L("  [" + libName + "] full symbol list (capped at 1200):");
            int cap = Math.Min(syms.Length, 1200);
            for (int i = 0; i < cap; i++)
            {
                Symbol s = syms[i];
                string nm = Safe(() => s.Name);
                string tn = Safe(() => s.Type.ToString());
                string vc = Safe(() => s.Variants.Length.ToString());
                L("     " + nm + " | " + tn + " | variants=" + vc);
            }
            L("  [" + libName + "] listed " + cap + " / " + syms.Length);

            // detailed: keywords, and remember which are usable functions
            L("  [" + libName + "] KEYWORD MATCH details:");
            int detailed = 0;
            for (int i = 0; i < syms.Length && detailed < 40; i++)
            {
                Symbol s = syms[i];
                string nm;
                try { nm = s.Name; } catch { continue; }
                if (nm == null) continue;
                string up = nm.ToUpperInvariant();
                bool hit = false;
                for (int k = 0; k < Keywords.Length; k++)
                    if (up.IndexOf(Keywords[k], StringComparison.Ordinal) >= 0) { hit = true; break; }
                if (!hit) continue;

                detailed++;
                string tn = Safe(() => s.Type.ToString());
                SymbolVariant[] vs = null;
                try { vs = s.Variants; } catch (Exception e) { vs = null; L("     " + nm + " | variants read ERR: " + e.Message); }
                int nv = (vs == null) ? -1 : vs.Length;
                L("     *** " + nm + " | " + tn + " | variants=" + nv);
                if (vs == null) continue;
                for (int v = 0; v < nv && v < 4; v++)
                {
                    PinBase[] pins = null;
                    try { pins = vs[v].ConnectionPoints; } catch (Exception e) { L("        var" + v + " pins ERR: " + e.Message); continue; }
                    int np = (pins == null) ? -1 : pins.Length;
                    string offs = "";
                    if (pins != null)
                        for (int q = 0; q < np && q < 20; q++)
                        {
                            PointD pt = pins[q].Location;
                            string dir = Safe(() => pins[q].Direction.ToString());
                            offs += "[" + q + "]=" + pt.X + "," + pt.Y + "/" + dir + "  ";
                        }
                    L("        var" + v + " nr=" + Safe(() => vs[v].VariantNr.ToString()) + " pins=" + np + " : " + offs);
                }
            }
            L("  [" + libName + "] detailed " + detailed + " keyword symbols");
        }

        // ------------------------------------------------------------------ stage 1

        private static readonly string[] Stage1Symbols = new string[]
        {
            "X", "K", "S", "Q_3L", "M3", "F1", "KM2"
        };

        private static void Stage1()
        {
            L("=== EplanDraw stage1: place single symbols ===");
            string target = ReadCfg(TargetFile, "");
            L("target = " + (target == "" ? "(missing)" : target));
            if (target == "") return;

            ProjectManager pm = new ProjectManager();
            Project prj = null;
            try
            {
                prj = pm.OpenProject(target, ProjectManager.OpenMode.Exclusive);
                L("OpenProject Exclusive ok: " + Safe(() => prj.ProjectName));

                int before = prj.Pages == null ? -1 : prj.Pages.Length;
                L("pages before = " + before);

                Page page = MakePage(prj);
                if (page == null) { L("no page available, abort"); return; }
                L("page name = " + Safe(() => page.NameParts.ToString()) +
                  " type = " + Safe(() => page.PageTypeName) +
                  " size = " + Safe(() => { PointD s = page.Size; return "(" + s.X + "," + s.Y + ")"; }));

                int beforePl = 0;
                try { beforePl = page.AllPlacements.Length; } catch { }
                L("placements on page before = " + beforePl);

                SymbolLibrary lib = new SymbolLibrary(prj, "IEC_symbol");
                for (int i = 0; i < Stage1Symbols.Length; i++)
                {
                    TryPlace(page, lib, Stage1Symbols[i], 60.0 + i * 40.0, 100.0, i);
                }
                try { lib.Dispose(); } catch { }

                int afterPl = 0;
                try { afterPl = page.AllPlacements.Length; } catch (Exception e) { L("recount failed: " + e.Message); }
                L("placements on page after = " + afterPl);

                // dump what is actually on the page now
                L("--- page inventory ---");
                try
                {
                    Placement[] pls = page.AllPlacements;
                    Dictionary<string, int> hist = new Dictionary<string, int>();
                    for (int i = 0; i < pls.Length; i++)
                    {
                        string tn = pls[i].GetType().Name;
                        hist[tn] = hist.ContainsKey(tn) ? hist[tn] + 1 : 1;
                    }
                    foreach (KeyValuePair<string, int> kv in hist) L("     " + kv.Key + " = " + kv.Value);
                }
                catch (Exception e) { L("inventory failed: " + e.Message); }
            }
            catch (Exception ex)
            {
                L("FAILED: " + ex.Message);
                if (ex.InnerException != null) L("  INNER: " + ex.InnerException.Message);
            }
            finally
            {
                try { if (prj != null) prj.Close(); L("project closed (saved)"); }
                catch (Exception ce) { L("close failed: " + ce.Message); }
            }
        }

        private static Page MakePage(Project prj)
        {
            // reuse any existing Circuit page, otherwise create one
            try
            {
                Page[] ps = prj.Pages;
                if (ps != null)
                    for (int i = 0; i < ps.Length; i++)
                        if (Safe(() => ps[i].PageTypeName).ToUpperInvariant().IndexOf("SCHEMA") >= 0 ||
                            Safe(() => ps[i].PageTypeName).ToUpperInvariant().IndexOf("SINGLE") >= 0 ||
                            Safe(() => ps[i].PageTypeName).ToUpperInvariant().IndexOf("SCHEMATIC") >= 0)
                            return ps[i];
            }
            catch { }

            try
            {
                PagePropertyList ppl = new PagePropertyList();
                ppl[Eplan.EplApi.DataModel.Properties.Page.PAGE_NAME] = "9";
                Page p = new Page();
                p.Create(prj, DocumentTypeManager.DocumentType.Circuit, ppl);
                L("new page created");
                return p;
            }
            catch (Exception e)
            {
                L("Create page failed: " + e.Message);
                if (e.InnerException != null) L("  INNER: " + e.InnerException.Message);
                return null;
            }
        }

        // ------------------------------------------------------------------ stage 2

        private static void Stage2()
        {
            L("=== EplanDraw stage2: two symbols + one connection ===");
            string target = ReadCfg(TargetFile, "");
            L("target = " + (target == "" ? "(missing)" : target));
            if (target == "") return;

            ProjectManager pm = new ProjectManager();
            Project prj = null;
            try
            {
                prj = pm.OpenProject(target, ProjectManager.OpenMode.Exclusive);
                L("OpenProject ok: " + Safe(() => prj.ProjectName));

                int before = 0;
                try { before = CountConnections(prj); } catch { }
                L("connections in project BEFORE = " + before);

                Page page = MakePage(prj);
                if (page == null) { L("no page, abort"); return; }
                L("page = " + Safe(() => page.NameParts.ToString()));

                SymbolLibrary lib = new SymbolLibrary(prj, "IEC_symbol");

                // ---- device tag methods probe on one function
                SymbolReference probe = Place(page, lib, "F1", 100.0, 100.0);
                if (probe != null)
                {
                    Function pfn = probe as Function;
                    if (pfn != null)
                    {
                        TrySetTag(pfn, "Name-setter", "-U9", false);
                        TrySetTag(pfn, "FUNC_IDENTDEVICETAG", "-V9", true);
                    }
                }

                // ---- ROUTE G: geometric auto-connect
                L("");
                L("--- ROUTE G: align connection points ---");
                SymbolReference gA = Place(page, lib, "F1", 100.0, 200.0);
                SymbolReference gB = null;
                if (gA != null)
                {
                    try
                    {
                        PinBase[] pa = gA.GraphicalConnectionPoints;
                        PointD absA = new PointD(gA.Location.X + pa[0].Location.X, gA.Location.Y + pa[0].Location.Y);
                        L("  A location=(" + gA.Location.X + "," + gA.Location.Y + ") pin0 abs=(" + absA.X + "," + absA.Y + ") dir=" + pa[0].Direction);

                        Symbol symB = FindSymbol(lib, "K");
                        int targetPin = 1;
                        PinBase[] pbMaster = symB.Variants[0].ConnectionPoints;
                        PointD off = pbMaster[targetPin].Location;
                        L("  B master pin" + targetPin + " offset=(" + off.X + "," + off.Y + ") dir=" + pbMaster[targetPin].Direction);
                        PointD ins = new PointD(absA.X - off.X, absA.Y - off.Y);
                        L("  computed insertion for B = (" + ins.X + "," + ins.Y + ")");

                        gB = Place(page, lib, "K", ins.X, ins.Y);
                    }
                    catch (Exception e) { L("  route G setup ERR: " + e.Message); }
                }

                if (gA != null)
                    ReportConnections("A", gA);
                if (gB != null)
                {
                    PinBase[] pbg = gB.GraphicalConnectionPoints;
                    if (pbg != null && pbg.Length > 1)
                    {
                        PointD absB = new PointD(gB.Location.X + pbg[1].Location.X, gB.Location.Y + pbg[1].Location.Y);
                        L("  B pin1 abs=(" + absB.X + "," + absB.Y + ")");
                    }
                    ReportConnections("B", gB);
                }

                int afterG = 0;
                try { afterG = CountConnections(prj); } catch (Exception e) { L("count ERR: " + e.Message); }
                L("connections in project AFTER route G = " + afterG + "   (delta " + (afterG - before) + ")");

                // ---- ROUTE L: explicit logical connection
                L("");
                L("--- ROUTE L: new Connection(prj, pinA, pinB) ---");
                SymbolReference lA = Place(page, lib, "F1", 200.0, 200.0);
                SymbolReference lB = Place(page, lib, "F1", 240.0, 200.0);
                int afterPlacements = CountConnections(prj);
                L("connections after placing two more symbols (expect unchanged) = " + afterPlacements);
                try
                {
                    PinBase p0 = lA.GraphicalConnectionPoints[0];
                    PinBase p1 = lB.GraphicalConnectionPoints[0];
                    Connection conn = new Connection(prj, p0, p1);
                    L("  Connection ctor ok");
                    L("  IsPlaced = " + Safe(() => conn.IsPlaced.ToString()));
                    L("  startPin parent = " + Safe(() => NameOf(conn.StartPin)));
                    L("  endPin   parent = " + Safe(() => NameOf(conn.EndPin)));
                    L("  ToString = " + Safe(() => conn.ToString()));
                }
                catch (Exception e)
                {
                    L("  ROUTE L FAILED: " + e.Message);
                    if (e.InnerException != null) L("    INNER: " + e.InnerException.Message);
                }

                int afterL = 0;
                try { afterL = CountConnections(prj); } catch (Exception e) { L("count ERR: " + e.Message); }
                L("connections in project AFTER route L = " + afterL + "   (delta vs before = " + (afterL - before) + ")");

                try { lib.Dispose(); } catch { }
                L("");
                L("placements on page = " + Safe(() => page.AllPlacements.Length.ToString()));
            }
            catch (Exception ex)
            {
                L("FAILED: " + ex.Message);
                if (ex.InnerException != null) L("  INNER: " + ex.InnerException.Message);
            }
            finally
            {
                try { if (prj != null) prj.Close(); L("project closed (saved)"); }
                catch (Exception ce) { L("close failed: " + ce.Message); }
            }
        }

        // ------------------------------------------------------------------ stage 2b

        private static void Stage2b()
        {
            L("=== EplanDraw stage2b: persist / regenerate / designate ===");
            string target = ReadCfg(TargetFile, "");
            L("target = " + (target == "" ? "(missing)" : target));
            if (target == "") return;

            ProjectManager pm = new ProjectManager();
            Project prj = null;
            try
            {
                prj = pm.OpenProject(target, ProjectManager.OpenMode.Exclusive);
                L("OpenProject ok: " + Safe(() => prj.ProjectName));

                ListConnections(prj, "AFTER REOPEN (persistence test)");

                L("");
                L("--- function inventory ---");
                try
                {
                    DMObjectsFinder f = new DMObjectsFinder(prj);
                    Function[] fns = f.GetFunctions(null);
                    L("functions = " + (fns == null ? -1 : fns.Length));
                    if (fns != null)
                        for (int i = 0; i < fns.Length && i < 30; i++)
                        {
                            L("  fn[" + i + "] name=" + Safe(() => fns[i].Name) +
                              " visibleName=" + Safe(() => fns[i].VisibleName));
                        }
                    try { f.Dispose(); } catch { }
                }
                catch (Exception e) { L("function inventory ERR: " + e.Message); }

                L("");
                L("--- PlaceConnectionDefinitionPoints (regenerate=true) ---");
                try
                {
                    Eplan.EplApi.HEServices.ConnectionService cs = new Eplan.EplApi.HEServices.ConnectionService();
                    cs.PlaceConnectionDefinitionPoints(prj, "", true);
                    L("  ok");
                }
                catch (Exception e)
                {
                    L("  ERR: " + e.Message);
                    if (e.InnerException != null) L("   INNER: " + e.InnerException.Message);
                }
                ListConnections(prj, "AFTER PlaceConnectionDefinitionPoints");

                L("");
                L("--- DesignateConnections (regenerate=true) ---");
                try
                {
                    Eplan.EplApi.HEServices.ConnectionService cs = new Eplan.EplApi.HEServices.ConnectionService();
                    cs.DesignateConnections(prj, "", Eplan.EplApi.HEServices.ConnectionService.DesignateOverwrition.All, false, true);
                    L("  ok");
                }
                catch (Exception e)
                {
                    L("  ERR: " + e.Message);
                    if (e.InnerException != null) L("   INNER: " + e.InnerException.Message);
                }
                ListConnections(prj, "AFTER DesignateConnections");

                L("");
                L("--- inventory of placements per page ---");
                try
                {
                    Page[] ps = prj.Pages;
                    for (int i = 0; i < ps.Length && i < 20; i++)
                    {
                        L("  page[" + i + "] " + Safe(() => ps[i].NameParts.ToString()) +
                          " placements=" + Safe(() => ps[i].AllPlacements.Length.ToString()));
                    }
                }
                catch (Exception e) { L("page inventory ERR: " + e.Message); }
            }
            catch (Exception ex)
            {
                L("FAILED: " + ex.Message);
                if (ex.InnerException != null) L("  INNER: " + ex.InnerException.Message);
            }
            finally
            {
                try { if (prj != null) prj.Close(); L("project closed (saved)"); }
                catch (Exception ce) { L("close failed: " + ce.Message); }
            }
        }

        private static void ListConnections(Project prj, string label)
        {
            L("");
            L("### connections [" + label + "]");
            try
            {
                DMObjectsFinder finder = new DMObjectsFinder(prj);
                Connection[] cs = finder.GetConnections(null);
                int n = (cs == null) ? -1 : cs.Length;
                L("  count = " + n);
                if (cs != null)
                    for (int i = 0; i < n && i < 20; i++)
                    {
                        L("  conn[" + i + "] placed=" + Safe(() => cs[i].IsPlaced.ToString()) +
                          " start=" + Safe(() => NameOf(cs[i].StartPin)) +
                          " end=" + Safe(() => NameOf(cs[i].EndPin)) +
                          " page=" + Safe(() => cs[i].Page == null ? "(null)" : cs[i].Page.NameParts.ToString()));
                    }
                try { finder.Dispose(); } catch { }
            }
            catch (Exception e) { L("  ERR: " + e.Message); }
        }

        // ------------------------------------------------------------------ stage 3

        private static void Stage3()
        {
            L("=== EplanDraw stage3: minimal usable circuit ===");
            string target = ReadCfg(TargetFile, "");
            L("target = " + (target == "" ? "(missing)" : target));
            if (target == "") return;

            ProjectManager pm = new ProjectManager();
            Project prj = null;
            try
            {
                prj = pm.OpenProject(target, ProjectManager.OpenMode.Exclusive);
                L("OpenProject ok: " + Safe(() => prj.ProjectName));

                Page page = MakePage(prj);
                if (page == null) { L("no page, abort"); return; }

                SymbolLibrary lib = new SymbolLibrary(prj, "IEC_symbol");

                L("");
                L("--- main circuit (3-pole) ---");
                SymbolReference q1 = PlaceNamed(page, lib, "Q_3L", 100.0, 240.0, "-Q1");
                SymbolReference m1 = null;
                if (q1 != null)
                    m1 = PlaceLinked(page, lib, "M3", q1, 1, 0, "-M1");

                L("");
                L("--- control circuit ---");
                SymbolReference f1 = PlaceNamed(page, lib, "F1", 200.0, 240.0, "-F1");
                SymbolReference s1 = null, s2 = null, k1 = null, x1 = null;
                if (f1 != null)
                {
                    s1 = PlaceLinked(page, lib, "S", f1, 1, 0, "-S1");
                    if (s1 != null)
                    {
                        s2 = PlaceLinked(page, lib, "S", s1, 1, 0, "-S2");
                        if (s2 != null)
                        {
                            k1 = PlaceLinked(page, lib, "K", s2, 1, 0, "-K1");
                            if (k1 != null)
                                x1 = PlaceLinked(page, lib, "X", k1, 1, 0, "-X1");
                        }
                    }
                }

                L("");
                L("placements on page = " + Safe(() => page.AllPlacements.Length.ToString()));

                L("");
                L("--- realize connections: PlaceConnectionDefinitionPoints (regenerate=true) ---");
                try
                {
                    Eplan.EplApi.HEServices.ConnectionService cs = new Eplan.EplApi.HEServices.ConnectionService();
                    cs.PlaceConnectionDefinitionPoints(prj, "", true);
                    L("  ok");
                }
                catch (Exception e)
                {
                    L("  ERR: " + e.Message);
                    if (e.InnerException != null) L("   INNER: " + e.InnerException.Message);
                }

                try
                {
                    Eplan.EplApi.HEServices.ConnectionService cs = new Eplan.EplApi.HEServices.ConnectionService();
                    cs.DesignateConnections(prj, "", Eplan.EplApi.HEServices.ConnectionService.DesignateOverwrition.All, false, true);
                    L("DesignateConnections ok");
                }
                catch (Exception e) { L("DesignateConnections ERR: " + e.Message); }

                ListConnections(prj, "FINAL");

                L("");
                L("--- devices with tags ---");
                try
                {
                    DMObjectsFinder f = new DMObjectsFinder(prj);
                    Function[] fns = f.GetFunctions(null);
                    L("functions = " + (fns == null ? -1 : fns.Length));
                    if (fns != null)
                        for (int i = 0; i < fns.Length && i < 40; i++)
                        {
                            L("  fn[" + i + "] tag=" + Safe(() => fns[i].Name));
                        }
                    try { f.Dispose(); } catch { }
                }
                catch (Exception e) { L("func list ERR: " + e.Message); }

                try { lib.Dispose(); } catch { }
            }
            catch (Exception ex)
            {
                L("FAILED: " + ex.Message);
                if (ex.InnerException != null) L("  INNER: " + ex.InnerException.Message);
            }
            finally
            {
                try { if (prj != null) prj.Close(); L("project closed (saved)"); }
                catch (Exception ce) { L("close failed: " + ce.Message); }
            }
        }

        private static SymbolReference PlaceNamed(Page page, SymbolLibrary lib, string symName, double x, double y, string tag)
        {
            SymbolReference sr = Place(page, lib, symName, x, y);
            if (sr != null) SetTag(sr, tag);
            return sr;
        }

        private static void SetTag(SymbolReference sr, string tag)
        {
            Function fn = sr as Function;
            if (fn == null) { L("   cannot set tag (not a Function)"); return; }
            try { fn.Name = tag; L("   tag set: " + tag + " readback=" + Safe(() => fn.Name)); }
            catch (Exception e) { L("   tag ERR: " + e.Message); }
        }

        // place symName so that its connection point newPinIdx coincides with
        // existing symbol's connection point prevPinIdx
        private static SymbolReference PlaceLinked(Page page, SymbolLibrary lib, string symName,
            SymbolReference prev, int prevPinIdx, int newPinIdx, string tag)
        {
            try
            {
                Symbol sym = FindSymbol(lib, symName);
                if (sym == null) { L("PlaceLinked '" + symName + "': not found"); return null; }
                PinBase[] pp = prev.GraphicalConnectionPoints;
                PointD abs = new PointD(prev.Location.X + pp[prevPinIdx].Location.X,
                                        prev.Location.Y + pp[prevPinIdx].Location.Y);
                PointD off = sym.Variants[0].ConnectionPoints[newPinIdx].Location;
                PointD ins = new PointD(abs.X - off.X, abs.Y - off.Y);
                L("PlaceLinked '" + symName + "': absPrev=(" + abs.X + "," + abs.Y + ") off=(" + off.X + "," + off.Y +
                  ") -> insert=(" + ins.X + "," + ins.Y + ")");
                SymbolReference sr = SymbolReference.Create(sym.Variants[0], page);
                sr.Location = ins;
                SetTag(sr, tag);
                return sr;
            }
            catch (Exception e)
            {
                L("PlaceLinked '" + symName + "' FAILED: " + e.Message);
                if (e.InnerException != null) L("   INNER: " + e.InnerException.Message);
                return null;
            }
        }

        private static string NameOf(PinBase p)
        {
            if (p == null) return "(null pin)";
            SymbolReference sr = p.Parent;
            if (sr == null) return "(null parent)";
            Function f = sr as Function;
            if (f != null) return Safe(() => f.Name) + "@(" + sr.Location.X + "," + sr.Location.Y + ")";
            return sr.GetType().Name + "@(" + sr.Location.X + "," + sr.Location.Y + ")";
        }

        private static void ReportConnections(string tag, SymbolReference sr)
        {
            try
            {
                SymbolReference.GraphicalConnection[] gc = sr.GraphicalConnections;
                int n = (gc == null) ? -1 : gc.Length;
                L("  " + tag + ": graphicalConnections = " + n);
                if (gc != null)
                    for (int i = 0; i < n && i < 8; i++)
                        L("     conn" + i + " from " + Safe(() => NameOf(gc[i].StartConnectionPoint)) +
                          " to " + Safe(() => NameOf(gc[i].EndConnectionPoint)));
            }
            catch (Exception e) { L("  " + tag + ": connections ERR: " + e.Message); }
        }

        private static int CountConnections(Project prj)
        {
            DMObjectsFinder finder = new DMObjectsFinder(prj);
            Connection[] cs = finder.GetConnections(null);
            int n = (cs == null) ? -1 : cs.Length;
            try { finder.Dispose(); } catch { }
            return n;
        }

        private static void TrySetTag(Function fn, string method, string value, bool viaProperty)
        {
            L("");
            L("--- device tag attempt: " + method);
            try
            {
                if (viaProperty)
                    fn.Properties[Eplan.EplApi.DataModel.Properties.FunctionBase.FUNC_IDENTDEVICETAG] = value;
                else
                    fn.Name = value;
                L("  set ok");
            }
            catch (Exception e)
            {
                L("  ERR: " + e.Message);
                if (e.InnerException != null) L("  INNER: " + e.InnerException.Message);
            }
            L("  readback Name = " + Safe(() => fn.Name));
            L("  readback VisibleName = " + Safe(() => fn.VisibleName));
            L("  readback FUNC_IDENTDEVICETAG = " +
              Safe(() => fn.Properties[Eplan.EplApi.DataModel.Properties.FunctionBase.FUNC_IDENTDEVICETAG].ToString()));
        }

        private static SymbolReference Place(Page page, SymbolLibrary lib, string symName, double x, double y)
        {
            try
            {
                Symbol sym = FindSymbol(lib, symName);
                if (sym == null) { L("place '" + symName + "': symbol not found"); return null; }
                SymbolReference sr = SymbolReference.Create(sym.Variants[0], page);
                sr.Location = new PointD(x, y);
                L("placed '" + symName + "' at (" + x + "," + y + ") type=" + sr.GetType().Name);
                return sr;
            }
            catch (Exception e)
            {
                L("place '" + symName + "' FAILED: " + e.Message);
                if (e.InnerException != null) L("   INNER: " + e.InnerException.Message);
                return null;
            }
        }

        private static Symbol FindSymbol(SymbolLibrary lib, string name)
        {
            Symbol[] syms = lib.Symbols;
            if (syms == null) return null;
            for (int i = 0; i < syms.Length; i++)
            {
                try { if (syms[i].Name == name) return syms[i]; } catch { }
            }
            return null;
        }

        private static void TryPlace(Page page, SymbolLibrary lib, string symName, double x, double y, int idx)
        {
            L("");
            L("--- place #" + idx + " symbol '" + symName + "' at (" + x + "," + y + ")");
            SymbolReference sr = null;
            try
            {
                Symbol sym = FindSymbol(lib, symName);
                if (sym == null) { L("  symbol not found"); return; }
                L("  type=" + Safe(() => sym.Type.ToString()) + " variants=" + Safe(() => sym.Variants.Length.ToString()));

                SymbolVariant[] vs = sym.Variants;
                SymbolVariant v = vs[0];
                L("  variant0 nr=" + Safe(() => v.VariantNr.ToString()) +
                  " pins(master)=" + Safe(() => v.ConnectionPoints.Length.ToString()));

                SymbolReference dummy = (SymbolReference)SymbolReference.Create(v, page);
                sr = dummy;
                L("  Create() ok; runtime type = " + sr.GetType().FullName);

                sr.Location = new PointD(x, y);
                PointD back = sr.Location;
                L("  set Location readback = (" + back.X + "," + back.Y + ")");

                try
                {
                    RectangleD ra = sr.GetLogicalArea();
                    {
                        L("  logicalArea lowerLeft=(" + ra.LowerLeft.X + "," + ra.LowerLeft.Y + ")" +
                          " upperRight=(" + ra.UpperRight.X + "," + ra.UpperRight.Y + ")" +
                          " w=" + ra.Width + " h=" + ra.Height);
                    }
                }
                catch (Exception e) { L("  GetLogicalArea ERR: " + e.Message); }

                try
                {
                    PinBase[] pins = sr.GraphicalConnectionPoints;
                    string s = "";
                    if (pins != null)
                        for (int q = 0; q < pins.Length && q < 12; q++)
                        {
                            PointD p = pins[q].Location;
                            s += "[" + q + "]=(" + p.X + "," + p.Y + ")" + Safe(() => pins[q].Direction.ToString()) + " ";
                        }
                    L("  placed pins(" + (pins == null ? -1 : pins.Length) + "): " + s);
                }
                catch (Exception e) { L("  pins ERR: " + e.Message); }

                try
                {
                    SymbolReference.GraphicalConnection[] gc = sr.GraphicalConnections;
                    L("  graphicalConnections = " + (gc == null ? -1 : gc.Length));
                }
                catch (Exception e) { L("  GraphicalConnections ERR: " + e.Message); }

                Function fn = sr as Function;
                if (fn != null)
                {
                    L("  is Function");
                    try
                    {
                        fn.Properties[Eplan.EplApi.DataModel.Properties.FunctionBase.FUNC_IDENTDEVICETAG] = "-T" + (idx + 1);
                        L("  set deviceTag readback = " +
                          Safe(() => fn.Properties[Eplan.EplApi.DataModel.Properties.FunctionBase.FUNC_IDENTDEVICETAG].ToString()));
                    }
                    catch (Exception e) { L("  deviceTag ERR: " + e.Message); }
                }
                else
                {
                    L("  NOT a Function (cannot assign device tag directly)");
                }
            }
            catch (Exception e)
            {
                L("  PLACE FAILED: " + e.Message);
                if (e.InnerException != null) L("    INNER: " + e.InnerException.Message);
            }
            finally
            {
                try { if (sr != null) sr.Dispose(); } catch { }
            }
        }
    }
}
