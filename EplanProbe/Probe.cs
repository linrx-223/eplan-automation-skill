using System;
using System.IO;
using System.Text;
using Eplan.EplApi.DataModel;

namespace EplanProbe
{
    /// <summary>
    /// Runs inside the EPLAN process, loaded by an EPLAN script via Assembly.Load(byte[]).
    /// Behaviour is driven by C:\Temp\probe_mode.txt:
    ///   inventory       - read-only dump of project/page/object structure  (default)
    ///   deleteframes    - remove every MacroBox (macro frame) on every page
    ///   removeplotframe - clear the page property PAGE_FORMPLOT (page border / title block)
    /// Target project path is read from C:\Temp\eplan_target.txt (UTF-8).
    /// ASCII-only source on purpose.
    /// </summary>
    public static class Probe
    {
        private const string OutFile = @"C:\Temp\probe_result.txt";
        private const string TargetFile = @"C:\Temp\eplan_target.txt";
        private const string ModeFile = @"C:\Temp\probe_mode.txt";

        public static void Run()
        {
            StringBuilder sb = new StringBuilder();
            ProjectManager pm = null;

            try
            {
                pm = new ProjectManager();
                sb.AppendLine("ctor ProjectManager: ok");
            }
            catch (Exception e)
            {
                sb.AppendLine("ctor ProjectManager FAILED: " + e.Message);
                Write(sb);
                return;
            }

            // ---- step 1: enumerate open projects ----
            try
            {
                Project[] open = pm.OpenProjects;
                sb.AppendLine("step1 OpenProjects: ok, count=" + (open == null ? -1 : open.Length));
                if (open != null)
                {
                    for (int i = 0; i < open.Length; i++)
                    {
                        string nm = "?";
                        try { nm = open[i].ProjectName; } catch (Exception e) { nm = "nameErr:" + e.Message; }
                        sb.AppendLine("    open[" + i + "] name=" + nm);
                    }
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("step1 OpenProjects FAILED: " + e.Message);
            }

            // ---- step 2: current project ----
            try
            {
                Project cur = pm.CurrentProject;
                sb.AppendLine("step2 CurrentProject: ok, " + (cur == null ? "null" : "name=" + cur.ProjectName));
                if (cur != null) { DumpPages(sb, "step2", cur); }
            }
            catch (Exception e)
            {
                sb.AppendLine("step2 CurrentProject FAILED: " + e.Message);
            }

            // ---- step 3: open target by path, exclusive ----
            try
            {
                string path = File.ReadAllText(TargetFile, Encoding.UTF8).Trim();
                sb.AppendLine("step3 target path = " + path);
                Project p = pm.OpenProject(path, ProjectManager.OpenMode.Exclusive);
                sb.AppendLine("step3 OpenProject(Exclusive): ok");
                DumpPages(sb, "step3", p);
            }
            catch (Exception e)
            {
                sb.AppendLine("step3 OpenProject(Exclusive) FAILED: " + e.Message);
            }

            // ---- step 4: get project object without opening ----
            try
            {
                string path = File.ReadAllText(TargetFile, Encoding.UTF8).Trim();
                Project p = pm.GetProject(path);
                sb.AppendLine("step4 GetProject: ok, " + (p == null ? "null" : "name=" + p.ProjectName));
                if (p != null) { DumpPages(sb, "step4", p); }
            }
            catch (Exception e)
            {
                sb.AppendLine("step4 GetProject FAILED: " + e.Message);
            }

            // ---- step 5: optional mutation ----
            string mode = "";
            try { mode = File.ReadAllText(ModeFile).Trim().ToLowerInvariant(); } catch { }
            sb.AppendLine("mode=[" + mode + "]");

            if (mode == "deleteframes")
            {
                DeleteMacroBoxes(sb, pm);
            }
            else if (mode == "removeplotframe")
            {
                RemovePlotFrames(sb, pm);
            }
            else if (mode == "bbox")
            {
                ReportBoxes(sb, pm, false);
            }
            else if (mode == "shrink")
            {
                ReportBoxes(sb, pm, true);
            }
            else if (mode == "newpage")
            {
                CreatePageWithFrame(sb, pm);
            }
            else if (mode == "delpage")
            {
                DeletePageByName(sb, pm);
            }
            else if (mode == "testframes")
            {
                TestFrameNames(sb, pm);
            }
            else if (mode == "newproject")
            {
                CreateProjectProperly(sb, pm);
            }
            else if (mode == "mastersync")
            {
                SyncMasterData(sb, pm);
            }
            else if (mode == "outside")
            {
                ListOutsideObjects(sb, pm);
            }
            else if (mode == "boxes")
            {
                ListMacroBoxes(sb, pm);
            }
            else if (mode == "fixboxes")
            {
                FixMacroBoxes(sb, pm);
            }
            else if (mode == "dumpmb")
            {
                DumpMacroBoxApi(sb, pm);
            }
            else if (mode == "region")
            {
                ListRegion(sb, pm);
            }
            else if (mode == "junk")
            {
                FindJunk(sb, pm);
            }
            else if (mode == "findtext")
            {
                FindByText(sb, pm);
            }

            Write(sb);
        }

        /// <summary>Searches every placement's text/name for a substring and reports matches with owner.</summary>
        private static void FindByText(StringBuilder sb, ProjectManager pm)
        {
            try
            {
                Project proj = ResolveProject(pm);
                if (proj == null) { sb.AppendLine("findtext: no project"); return; }

                string needle = "";
                try { needle = File.ReadAllText(@"C:\Temp\probe_text.txt").Trim(); } catch { }
                sb.AppendLine("findtext: looking for [" + needle + "]");
                if (needle.Length == 0) { return; }

                Page[] pages = proj.Pages;
                for (int p = 0; p < pages.Length; p++)
                {
                    Page pg = pages[p];
                    Placement[] pls = null;
                    try { pls = pg.AllPlacements; } catch { }
                    if (pls == null) { continue; }

                    // owner map
                    System.Collections.Generic.Dictionary<long, string> owners =
                        new System.Collections.Generic.Dictionary<long, string>();
                    for (int i = 0; i < pls.Length; i++)
                    {
                        MacroBox mb = pls[i] as MacroBox;
                        if (mb == null) { continue; }
                        string nm = "";
                        try { nm = mb.MacroName; } catch { }
                        int idx = nm.LastIndexOf('\\');
                        if (idx >= 0) { nm = nm.Substring(idx + 1); }
                        try
                        {
                            Placement[] refs = mb.ReferencedPlacements;
                            if (refs != null)
                            {
                                for (int r = 0; r < refs.Length; r++)
                                {
                                    try { owners[refs[r].ObjectIdentifier] = nm; } catch { }
                                }
                            }
                        }
                        catch { }
                    }

                    int hits = 0;
                    for (int i = 0; i < pls.Length; i++)
                    {
                        string txt = "";
                        try
                        {
                            Eplan.EplApi.DataModel.Graphics.Text t =
                                pls[i] as Eplan.EplApi.DataModel.Graphics.Text;
                            if (t != null) { txt = t.Contents.ToString(); }
                        }
                        catch { }
                        if (txt.Length == 0)
                        {
                            try
                            {
                                Eplan.EplApi.DataModel.Graphics.PathText pt =
                                    pls[i] as Eplan.EplApi.DataModel.Graphics.PathText;
                                if (pt != null) { txt = pt.Contents.ToString(); }
                            }
                            catch { }
                        }
                        if (txt.Length == 0)
                        {
                            try
                            {
                                Eplan.EplApi.DataModel.Function f =
                                    pls[i] as Eplan.EplApi.DataModel.Function;
                                if (f != null) { txt = f.Name; }
                            }
                            catch { }
                        }

                        if (txt.Length == 0) { continue; }
                        if (txt.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) { continue; }

                        hits++;
                        if (hits > 25) { continue; }

                        long id = -1;
                        try { id = pls[i].ObjectIdentifier; } catch { }
                        string owner = owners.ContainsKey(id) ? owners[id] : "-- ORPHAN --";

                        double a = 0, b = 0, c = 0, d = 0;
                        object bb = null;
                        try { bb = pls[i].GetBoundingBox(); } catch { }
                        bool haveBox = (bb != null && TryBox(bb, out a, out b, out c, out d));

                        string shortTxt = txt.Length > 90 ? txt.Substring(0, 90) + "..." : txt;
                        sb.AppendLine("  HIT " + pls[i].GetType().Name + " [" + shortTxt + "] "
                            + (haveBox ? Fmt(a, b, c, d) : "<no box>")
                            + "  IN<" + owner + ">");
                    }
                    sb.AppendLine("  findtext: hits=" + hits);
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("findtext FAILED: " + e.Message);
            }
        }

        /// <summary>
        /// Precise orphan detection: walks every MacroBox's ReferencedPlacements to build the set of
        /// objects that belong to a macro. Anything else on the page is an orphan. If the file
        /// C:\Temp\probe_delete.txt contains "yes", the orphans are deleted as well.
        /// </summary>
        private static void FindJunk(StringBuilder sb, ProjectManager pm)
        {
            try
            {
                Project proj = ResolveProject(pm);
                if (proj == null) { sb.AppendLine("junk: no project"); return; }

                bool doDelete = false;
                try
                {
                    string flag = File.ReadAllText(@"C:\Temp\probe_delete.txt").Trim().ToLowerInvariant();
                    doDelete = (flag == "yes");
                }
                catch { }
                sb.AppendLine("junk: delete=" + doDelete);

                Page[] pages = proj.Pages;
                for (int p = 0; p < pages.Length; p++)
                {
                    Page pg = pages[p];
                    sb.AppendLine("junk: page=" + pg.Name + " " + Describe(pg.Size, "size"));

                    double fx1, fy1, fx2, fy2;
                    bool haveFrame = TryFrameBox(pg, out fx1, out fy1, out fx2, out fy2);
                    if (haveFrame) { sb.AppendLine("  frame box: " + Fmt(fx1, fy1, fx2, fy2)); }

                    Placement[] pls = null;
                    try { pls = pg.AllPlacements; } catch { }
                    if (pls == null) { continue; }

                    // 1. everything referenced by some macro box
                    System.Collections.Generic.HashSet<long> owned =
                        new System.Collections.Generic.HashSet<long>();
                    int boxCount = 0;
                    System.Collections.Generic.List<MacroBox> boxes =
                        new System.Collections.Generic.List<MacroBox>();
                    for (int i = 0; i < pls.Length; i++)
                    {
                        MacroBox mb = pls[i] as MacroBox;
                        if (mb == null) { continue; }
                        boxCount++;
                        boxes.Add(mb);
                        try
                        {
                            Placement[] refs = mb.ReferencedPlacements;
                            if (refs != null)
                            {
                                for (int r = 0; r < refs.Length; r++)
                                {
                                    try { owned.Add(refs[r].ObjectIdentifier); } catch { }
                                }
                            }
                        }
                        catch { }
                    }
                    sb.AppendLine("  macroBoxes=" + boxCount + " ownedObjects=" + owned.Count);

                    // 2. orphans = placements not owned, excluding macro boxes themselves
                    System.Collections.Generic.List<Placement> orphans =
                        new System.Collections.Generic.List<Placement>();
                    System.Collections.Generic.List<string> orphanInfo =
                        new System.Collections.Generic.List<string>();

                    int outsideFrame = 0;
                    for (int i = 0; i < pls.Length; i++)
                    {
                        if (pls[i] is MacroBox) { continue; }

                        long id = -1;
                        try { id = pls[i].ObjectIdentifier; } catch { }
                        if (id >= 0 && owned.Contains(id)) { continue; }

                        string t = "?";
                        string nm = "";
                        try { t = pls[i].GetType().Name; } catch { }
                        try
                        {
                            Eplan.EplApi.DataModel.Function f = pls[i] as Eplan.EplApi.DataModel.Function;
                            if (f != null) { nm = f.Name; }
                        }
                        catch { }
                        try
                        {
                            Eplan.EplApi.DataModel.Graphics.Text tx =
                                pls[i] as Eplan.EplApi.DataModel.Graphics.Text;
                            if (tx != null && nm.Length == 0) { nm = tx.Contents.ToString(); }
                        }
                        catch { }

                        double a = 0, b = 0, c = 0, d = 0;
                        object bb = null;
                        try { bb = pls[i].GetBoundingBox(); } catch { }
                        bool haveBox = (bb != null && TryBox(bb, out a, out b, out c, out d));

                        if (haveBox && haveFrame)
                        {
                            if (c < fx1 || a > fx2 || d < fy1 || b > fy2) { outsideFrame++; }
                        }

                        orphans.Add(pls[i]);
                        orphanInfo.Add(t + " [" + nm + "] " + (haveBox ? Fmt(a, b, c, d) : "<no box>"));
                    }

                    sb.AppendLine("  ORPHANS=" + orphans.Count + " (of which fully outside frame: " + outsideFrame + ")");
                    for (int i = 0; i < orphanInfo.Count && i < 40; i++)
                    {
                        sb.AppendLine("    ! " + orphanInfo[i]);
                    }

                    if (doDelete && orphans.Count > 0)
                    {
                        int ok = 0, fail = 0;
                        for (int i = 0; i < orphans.Count; i++)
                        {
                            try { orphans[i].Remove(); ok++; }
                            catch (Exception e)
                            {
                                fail++;
                                if (fail <= 5) { sb.AppendLine("      remove failed: " + e.Message); }
                            }
                        }
                        sb.AppendLine("  DELETED orphans=" + ok + " failed=" + fail);
                    }
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("junk FAILED: " + e.Message);
            }
        }

        /// <summary>
        /// Lists placements whose centre lies inside the rectangle given in C:\Temp\probe_region.txt
        /// ("xmin,ymin,xmax,ymax"), together with the macro box (if any) that contains them.
        /// </summary>
        private static void ListRegion(StringBuilder sb, ProjectManager pm)
        {
            try
            {
                Project proj = ResolveProject(pm);
                if (proj == null) { sb.AppendLine("region: no project"); return; }

                double rx1 = 0, ry1 = 0, rx2 = 1189, ry2 = 841;
                try
                {
                    string[] parts = File.ReadAllText(@"C:\Temp\probe_region.txt").Split(',');
                    if (parts.Length >= 4)
                    {
                        rx1 = double.Parse(parts[0]); ry1 = double.Parse(parts[1]);
                        rx2 = double.Parse(parts[2]); ry2 = double.Parse(parts[3]);
                    }
                }
                catch { }
                sb.AppendLine("region: x[" + rx1 + ".." + rx2 + "] y[" + ry1 + ".." + ry2 + "]");

                Page[] pages = proj.Pages;
                for (int p = 0; p < pages.Length; p++)
                {
                    Page pg = pages[p];
                    Placement[] pls = null;
                    try { pls = pg.AllPlacements; } catch { }
                    if (pls == null) { continue; }

                    // macro boxes for containment test
                    System.Collections.Generic.List<double[]> boxes =
                        new System.Collections.Generic.List<double[]>();
                    System.Collections.Generic.List<string> boxNames =
                        new System.Collections.Generic.List<string>();
                    for (int i = 0; i < pls.Length; i++)
                    {
                        MacroBox mb = pls[i] as MacroBox;
                        if (mb == null) { continue; }
                        object bb = null;
                        try { bb = mb.GetBoundingBox(); } catch { }
                        double a, b, c, d;
                        if (TryBox(bb, out a, out b, out c, out d))
                        {
                            boxes.Add(new double[] { a, b, c, d });
                            string nm = "";
                            try { nm = mb.MacroName; } catch { }
                            int idx = nm.LastIndexOf('\\');
                            if (idx >= 0) { nm = nm.Substring(idx + 1); }
                            boxNames.Add(nm);
                        }
                    }

                    System.Collections.Generic.Dictionary<string, int> byType =
                        new System.Collections.Generic.Dictionary<string, int>();
                    int inside = 0, outside = 0, shown = 0;

                    for (int i = 0; i < pls.Length; i++)
                    {
                        if (pls[i] is MacroBox) { continue; }
                        object bb = null;
                        try { bb = pls[i].GetBoundingBox(); } catch { }
                        double a, b, c, d;
                        if (bb == null || !TryBox(bb, out a, out b, out c, out d)) { continue; }

                        double cx = (a + c) / 2.0;
                        double cy = (b + d) / 2.0;
                        if (cx < rx1 || cx > rx2 || cy < ry1 || cy > ry2) { continue; }

                        string t = "?";
                        try { t = pls[i].GetType().Name; } catch { }
                        if (byType.ContainsKey(t)) { byType[t] = byType[t] + 1; } else { byType[t] = 1; }

                        string owner = "";
                        for (int k = 0; k < boxes.Count; k++)
                        {
                            double[] bx = boxes[k];
                            if (cx >= bx[0] && cx <= bx[2] && cy >= bx[1] && cy <= bx[3])
                            {
                                owner = boxNames[k];
                                break;
                            }
                        }
                        if (owner.Length > 0) { inside++; } else { outside++; }

                        if (shown < 40 && owner.Length == 0)
                        {
                            shown++;
                            string nm = "";
                            try
                            {
                                Eplan.EplApi.DataModel.Function f = pls[i] as Eplan.EplApi.DataModel.Function;
                                if (f != null) { nm = f.Name; }
                            }
                            catch { }
                            try
                            {
                                if (nm.Length == 0)
                                {
                                    Eplan.EplApi.DataModel.Graphics.Text tx =
                                        pls[i] as Eplan.EplApi.DataModel.Graphics.Text;
                                    if (tx != null) { nm = tx.Contents.ToString(); }
                                }
                            }
                            catch { }
                            sb.AppendLine("  " + t + " [" + nm + "] "
                                + Fmt(a, b, c, d)
                                + (owner.Length > 0 ? "  IN<" + owner + ">" : "  -- NO MACRO BOX --"));
                        }
                    }

                    sb.AppendLine("  totals in region: insideMacroBox=" + inside + ", outsideMacroBox=" + outside);
                    foreach (System.Collections.Generic.KeyValuePair<string, int> kv in byType)
                    {
                        sb.AppendLine("  TYPE " + kv.Key + " = " + kv.Value);
                    }
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("region FAILED: " + e.Message);
            }
        }

        /// <summary>Reflects over the live MacroBox object to list everything it can do.</summary>
        private static void DumpMacroBoxApi(StringBuilder sb, ProjectManager pm)
        {
            try
            {
                Project proj = ResolveProject(pm);
                if (proj == null) { sb.AppendLine("dumpmb: no project"); return; }

                MacroBox mb = null;
                Page[] pages = proj.Pages;
                for (int i = 0; i < pages.Length && mb == null; i++)
                {
                    Placement[] pls = null;
                    try { pls = pages[i].AllPlacements; } catch { }
                    if (pls == null) { continue; }
                    for (int k = 0; k < pls.Length; k++)
                    {
                        mb = pls[k] as MacroBox;
                        if (mb != null) { break; }
                    }
                }
                if (mb == null) { sb.AppendLine("dumpmb: no macro box found"); return; }

                Type t = mb.GetType();
                sb.AppendLine("type=" + t.FullName);
                Type bt = t.BaseType;
                int guard = 0;
                while (bt != null && guard++ < 8)
                {
                    sb.AppendLine("  base: " + bt.FullName);
                    bt = bt.BaseType;
                }

                sb.AppendLine("--- PROPERTIES ---");
                System.Reflection.PropertyInfo[] props = t.GetProperties();
                for (int i = 0; i < props.Length; i++)
                {
                    sb.AppendLine("  " + props[i].Name
                        + " : " + props[i].PropertyType.Name
                        + " read=" + props[i].CanRead
                        + " write=" + props[i].CanWrite
                        + " idx=" + props[i].GetIndexParameters().Length);
                }

                sb.AppendLine("--- METHODS ---");
                System.Reflection.MethodInfo[] mets = t.GetMethods();
                for (int i = 0; i < mets.Length; i++)
                {
                    if (mets[i].DeclaringType == typeof(object)) { continue; }
                    System.Reflection.ParameterInfo[] ps = mets[i].GetParameters();
                    StringBuilder sig = new StringBuilder();
                    for (int k = 0; k < ps.Length; k++)
                    {
                        if (k > 0) { sig.Append(", "); }
                        sig.Append(ps[k].ParameterType.Name);
                    }
                    sb.AppendLine("  " + mets[i].Name + "(" + sig + ")");
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("dumpmb FAILED: " + e.Message);
            }
        }

        /// <summary>
        /// Moves each MacroBox so that it lines up with the bounding box of the objects it references
        /// (that is where the macro frame originally sat). Boxes with no content left are reported.
        /// </summary>
        private static void FixMacroBoxes(StringBuilder sb, ProjectManager pm)
        {
            try
            {
                Project proj = ResolveProject(pm);
                if (proj == null) { sb.AppendLine("fixboxes: no project"); return; }

                Page[] pages = proj.Pages;
                for (int p = 0; p < pages.Length; p++)
                {
                    Page pg = pages[p];
                    sb.AppendLine("fixboxes: page=" + pg.Name);

                    Placement[] pls = null;
                    try { pls = pg.AllPlacements; } catch { }
                    if (pls == null) { continue; }

                    // collect boxes first, moving while iterating is unsafe
                    System.Collections.Generic.List<MacroBox> boxes =
                        new System.Collections.Generic.List<MacroBox>();
                    for (int i = 0; i < pls.Length; i++)
                    {
                        MacroBox mb = pls[i] as MacroBox;
                        if (mb != null) { boxes.Add(mb); }
                    }
                    sb.AppendLine("  found " + boxes.Count + " macro boxes");

                    for (int i = 0; i < boxes.Count; i++)
                    {
                        MacroBox mb = boxes[i];
                        string macName = "";
                        try { macName = mb.MacroName; } catch { }
                        if (macName.Length > 60) { macName = macName.Substring(macName.Length - 60); }

                        double a, b, c, d;
                        object bb = null;
                        try { bb = mb.GetBoundingBox(); } catch { }
                        TryBox(bb, out a, out b, out c, out d);

                        double ca = double.MaxValue, cb = double.MaxValue;
                        double cc = double.MinValue, cd = double.MinValue;
                        int cnt = 0;
                        try
                        {
                            Placement[] refs = mb.ReferencedPlacements;
                            if (refs != null)
                            {
                                for (int r = 0; r < refs.Length; r++)
                                {
                                    object rb = null;
                                    try { rb = refs[r].GetBoundingBox(); } catch { }
                                    double x1, y1, x2, y2;
                                    if (rb != null && TryBox(rb, out x1, out y1, out x2, out y2))
                                    {
                                        if (x1 < ca) { ca = x1; }
                                        if (y1 < cb) { cb = y1; }
                                        if (x2 > cc) { cc = x2; }
                                        if (y2 > cd) { cd = y2; }
                                        cnt++;
                                    }
                                }
                            }
                        }
                        catch { }

                        if (cnt == 0)
                        {
                            sb.AppendLine("  MB[" + (i + 1) + "] " + macName + " -> NO CONTENT, skipped");
                            continue;
                        }

                        double ox = a - ca;
                        double oy = b - cb;
                        if (Math.Abs(ox) < 0.5 && Math.Abs(oy) < 0.5)
                        {
                            sb.AppendLine("  MB[" + (i + 1) + "] " + macName + " -> already aligned (" + Math.Round(ox, 2) + "," + Math.Round(oy, 2) + ")");
                            continue;
                        }

                        // Empirically Group.Move translates X exactly, but Y lands one box-height short,
                        // so compensate by adding the box height on the Y axis.
                        double boxH = d - b;
                        double mvx = -ox;
                        double mvy = -oy + boxH;

                        sb.AppendLine("  MB[" + (i + 1) + "] " + macName
                            + " -> offset (" + Math.Round(ox, 2) + "," + Math.Round(oy, 2) + ")"
                            + " boxH=" + Math.Round(boxH, 2)
                            + ", moving by (" + Math.Round(mvx, 2) + "," + Math.Round(mvy, 2) + ")");

                        string moved = MoveMacroBox(mb, mvx, mvy);
                        sb.AppendLine("      " + moved);
                    }
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("fixboxes FAILED: " + e.Message);
            }
        }

        /// <summary>Moves a MacroBox by (dx,dy) using its Group, or the writable Location property.</summary>
        private static string MoveMacroBox(MacroBox mb, double dx, double dy)
        {
            StringBuilder log = new StringBuilder();

            // 1. Group.Move(PointD) -- translation, unambiguous
            try
            {
                Eplan.EplApi.DataModel.Group g = mb.Group;
                if (g != null)
                {
                    g.Move(new Eplan.EplApi.Base.PointD(dx, dy));
                    return "moved via Group.Move(" + Math.Round(dx, 2) + "," + Math.Round(dy, 2) + ")";
                }
                log.Append("Group=null; ");
            }
            catch (Exception e)
            {
                log.Append("Group.Move failed: " + (e.InnerException != null ? e.InnerException.Message : e.Message) + "; ");
            }

            // 2. Location is write-only: set it to the target absolute position
            try
            {
                double a, b, c, d;
                object bb = mb.GetBoundingBox();
                TryBox(bb, out a, out b, out c, out d);
                double tx = a + dx;
                double ty = b + dy;
                mb.Location = new Eplan.EplApi.Base.PointD(tx, ty);
                return "set Location to (" + Math.Round(tx, 2) + "," + Math.Round(ty, 2) + ") [" + log + "]";
            }
            catch (Exception e)
            {
                log.Append("Location failed: " + (e.InnerException != null ? e.InnerException.Message : e.Message) + "; ");
            }

            return "all failed -> " + log;
        }

        /// <summary>Moves any placement by (dx,dy) using whatever Move/position API it exposes.</summary>
        private static string MoveObject(object obj, double dx, double dy)
        {
            // 1. Group.Move(PointD) -- MacroBox may derive from Group
            try
            {
                System.Reflection.MethodInfo mi = obj.GetType().GetMethod("Move",
                    new Type[] { typeof(Eplan.EplApi.Base.PointD) });
                if (mi != null)
                {
                    mi.Invoke(obj, new object[] { new Eplan.EplApi.Base.PointD(dx, dy) });
                    return "moved via Move(PointD)";
                }
            }
            catch (Exception e)
            {
                return "Move(PointD) failed: " + (e.InnerException != null ? e.InnerException.Message : e.Message);
            }

            // 2. settable X / Y properties
            try
            {
                System.Reflection.PropertyInfo px = obj.GetType().GetProperty("X");
                System.Reflection.PropertyInfo py = obj.GetType().GetProperty("Y");
                if (px != null && py != null && px.CanWrite && py.CanWrite)
                {
                    px.SetValue(obj, ToDouble(px.GetValue(obj, null)) + dx, null);
                    py.SetValue(obj, ToDouble(py.GetValue(obj, null)) + dy, null);
                    return "moved via X/Y properties";
                }
            }
            catch (Exception e)
            {
                return "X/Y failed: " + e.Message;
            }

            // 3. settable Location
            try
            {
                System.Reflection.PropertyInfo pl = obj.GetType().GetProperty("Location");
                if (pl != null && pl.CanWrite)
                {
                    object cur = pl.GetValue(obj, null);
                    pl.SetValue(obj, new Eplan.EplApi.Base.PointD(dx, dy), null);
                    return "moved via Location";
                }
            }
            catch (Exception e)
            {
                return "Location failed: " + e.Message;
            }

            return "NO MOVABLE API FOUND on " + obj.GetType().FullName;
        }

        /// <summary>For every MacroBox: its own box vs the bounding box of the objects it references.</summary>
        private static void ListMacroBoxes(StringBuilder sb, ProjectManager pm)
        {
            try
            {
                Project proj = ResolveProject(pm);
                if (proj == null) { sb.AppendLine("boxes: no project"); return; }

                Page[] pages = proj.Pages;
                for (int p = 0; p < pages.Length; p++)
                {
                    Page pg = pages[p];
                    sb.AppendLine("boxes: page=" + pg.Name + " " + Describe(pg.Size, "size"));

                    double fx1, fy1, fx2, fy2;
                    if (TryFrameBox(pg, out fx1, out fy1, out fx2, out fy2))
                    {
                        sb.AppendLine("  frame box: " + Fmt(fx1, fy1, fx2, fy2));
                    }

                    Placement[] pls = null;
                    try { pls = pg.AllPlacements; } catch { }
                    if (pls == null) { continue; }

                    int n = 0;
                    for (int i = 0; i < pls.Length; i++)
                    {
                        MacroBox mb = pls[i] as MacroBox;
                        if (mb == null) { continue; }
                        n++;

                        string macName = "";
                        try { macName = mb.MacroName; } catch { }

                        double a, b, c, d;
                        object bb = null;
                        try { bb = mb.GetBoundingBox(); } catch { }
                        TryBox(bb, out a, out b, out c, out d);

                        sb.AppendLine("  MB[" + n + "] [" + macName + "] box=" + Fmt(a, b, c, d));

                        double ca = double.MaxValue, cb = double.MaxValue;
                        double cc = double.MinValue, cd = double.MinValue;
                        int cnt = 0;
                        try
                        {
                            Placement[] refs = mb.ReferencedPlacements;
                            if (refs != null)
                            {
                                for (int r = 0; r < refs.Length; r++)
                                {
                                    object rb = null;
                                    try { rb = refs[r].GetBoundingBox(); } catch { }
                                    double x1, y1, x2, y2;
                                    if (rb != null && TryBox(rb, out x1, out y1, out x2, out y2))
                                    {
                                        if (x1 < ca) { ca = x1; }
                                        if (y1 < cb) { cb = y1; }
                                        if (x2 > cc) { cc = x2; }
                                        if (y2 > cd) { cd = y2; }
                                        cnt++;
                                    }
                                }
                            }
                        }
                        catch { }

                        if (cnt > 0)
                        {
                            sb.AppendLine("        content(" + cnt + ")=" + Fmt(ca, cb, cc, cd)
                                + "  offset=(" + Math.Round(a - ca, 2) + ", " + Math.Round(b - cb, 2) + ")");
                        }
                        else
                        {
                            sb.AppendLine("        content: none referenced");
                        }
                    }
                    sb.AppendLine("  macroBoxCount=" + n);
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("boxes FAILED: " + e.Message);
            }
        }

        /// <summary>Lists placements that fall (wholly or partly) outside the page area.</summary>
        private static void ListOutsideObjects(StringBuilder sb, ProjectManager pm)
        {
            try
            {
                Project proj = ResolveProject(pm);
                if (proj == null) { sb.AppendLine("outside: no project"); return; }

                Page[] pages = proj.Pages;
                for (int i = 0; i < pages.Length; i++)
                {
                    Page pg = pages[i];
                    sb.AppendLine("outside: page=" + pg.Name + " " + Describe(pg.Size, "size"));

                    double fx1, fy1, fx2, fy2;
                    if (TryFrameBox(pg, out fx1, out fy1, out fx2, out fy2))
                    {
                        sb.AppendLine("  frame box: " + Fmt(fx1, fy1, fx2, fy2));
                    }

                    Placement[] pls = null;
                    try { pls = pg.AllPlacements; } catch { }
                    if (pls == null) { continue; }

                    int fully = 0, partly = 0, shown = 0;
                    for (int k = 0; k < pls.Length; k++)
                    {
                        object bb = null;
                        try { bb = pls[k].GetBoundingBox(); } catch { }
                        double a, b, c, d;
                        if (bb == null || !TryBox(bb, out a, out b, out c, out d)) { continue; }

                        bool whollyOut = (c <= 0) || (a >= 420) || (d <= 0);
                        bool abovePage = (b >= 297);
                        if (!whollyOut && !abovePage) { continue; }

                        if (whollyOut) { fully++; } else { partly++; }

                        if (shown < 30)
                        {
                            shown++;
                            string t = "?";
                            string nm = "";
                            try { t = pls[k].GetType().Name; } catch { }
                            try { nm = ((Eplan.EplApi.DataModel.Placement)pls[k]).ToString(); } catch { }
                            try
                            {
                                Eplan.EplApi.DataModel.Function f = pls[k] as Eplan.EplApi.DataModel.Function;
                                if (f != null) { nm = f.Name; }
                            }
                            catch { }
                            sb.AppendLine("  OUT[" + shown + "] " + t + " name=[" + nm + "] " + Fmt(a, b, c, d));
                        }
                    }
                    sb.AppendLine("  totals: whollyOutside=" + fully + " partlyOutside=" + partly);
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("outside FAILED: " + e.Message);
            }
        }

        /// <summary>
        /// Lists the project's master-data entries (frames etc.), runs UpdateProject to synchronise them
        /// from the system master-data paths, then tries to give the page the requested frame.
        /// </summary>
        private static void SyncMasterData(StringBuilder sb, ProjectManager pm)
        {
            try
            {
                Project proj = ResolveProject(pm);
                if (proj == null) { sb.AppendLine("mastersync: no project"); return; }
                sb.AppendLine("mastersync: project=" + proj.ProjectName);

                Eplan.EplApi.HEServices.Masterdata md = new Eplan.EplApi.HEServices.Masterdata();

                DumpEntries(sb, "BEFORE", md.get_ProjectEntries(proj));

                sb.AppendLine("mastersync: calling UpdateProject ...");
                try { md.UpdateProject(proj); sb.AppendLine("mastersync: UpdateProject ok"); }
                catch (Exception e) { sb.AppendLine("mastersync: UpdateProject FAILED: " + e.Message); }

                DumpEntries(sb, "AFTER", md.get_ProjectEntries(proj));

                // The project carries forms (.fXX) but no plot frames (.fn1) -- that is exactly why
                // every frame name was ignored. Try to add the frame file to the project master data.
                string frameFile = @"D:\EPLAN\图框\Linrrrx\GB_A1_001.fn1";
                sb.AppendLine("mastersync: frame file exists = " + System.IO.File.Exists(frameFile));

                System.Collections.Specialized.StringCollection add =
                    new System.Collections.Specialized.StringCollection();
                add.Add(frameFile);
                try
                {
                    md.AddToProjectEx(proj, add);
                    sb.AppendLine("mastersync: AddToProjectEx(full path) ok");
                }
                catch (Exception e)
                {
                    sb.AppendLine("mastersync: AddToProjectEx(full path) FAILED: " + e.Message);
                    System.Collections.Specialized.StringCollection add2 =
                        new System.Collections.Specialized.StringCollection();
                    add2.Add("GB_A1_001.fn1");
                    try
                    {
                        md.AddToProjectEx(proj, add2);
                        sb.AppendLine("mastersync: AddToProjectEx(bare name) ok");
                    }
                    catch (Exception e2)
                    {
                        sb.AppendLine("mastersync: AddToProjectEx(bare name) FAILED: " + e2.Message);
                    }
                }

                DumpEntries(sb, "AFTER-ADD", md.get_ProjectEntries(proj));

                string frame = "GB_A1_001";
                try
                {
                    string f = File.ReadAllText(@"C:\Temp\probe_frame.txt").Trim();
                    if (f.Length > 0) { frame = f; }
                }
                catch { }

                Page[] pages = proj.Pages;
                if (pages.Length == 0) { sb.AppendLine("mastersync: no pages"); return; }

                sb.AppendLine("mastersync: trying frame '" + frame + "' on page " + pages[0].Name);
                try
                {
                    pages[0].Properties[Eplan.EplApi.DataModel.Properties.Page.PAGE_FORMPLOT] = frame;
                    sb.AppendLine("  wrote PAGE_FORMPLOT=" + frame);
                }
                catch (Exception e) { sb.AppendLine("  write FAILED: " + e.Message); }

                sb.AppendLine("  page size now = " + Describe(pages[0].Size, "")
                    + " actual frame = " + PlotFrameName(pages[0]));
            }
            catch (Exception e)
            {
                sb.AppendLine("mastersync FAILED: " + e.Message);
                if (e.InnerException != null) { sb.AppendLine("  inner: " + e.InnerException.Message); }
            }
        }

        private static void DumpEntries(StringBuilder sb, string tag,
            System.Collections.Specialized.StringCollection entries)
        {
            if (entries == null) { sb.AppendLine("  " + tag + ": <null>"); return; }
            sb.AppendLine("  " + tag + ": count=" + entries.Count);
            int limit = entries.Count < 12 ? entries.Count : 12;
            for (int i = 0; i < limit; i++) { sb.AppendLine("      " + entries[i]); }
        }

        /// <summary>
        /// Creates a brand-new project through EPLAN's own CreateProject (from a template), so master
        /// data (frames) gets imported properly. Then tries to create a page with a chosen frame.
        /// </summary>
        private static void CreateProjectProperly(StringBuilder sb, ProjectManager pm)
        {
            try
            {
                string projName = "ABB变频控制_A1";
                try { projName = File.ReadAllText(@"C:\Temp\probe_projname.txt").Trim(); } catch { }
                if (projName.Length == 0) { projName = "ABB变频控制_A1"; }

                string frame = "GB_A1_001";
                try
                {
                    string f = File.ReadAllText(@"C:\Temp\probe_frame.txt").Trim();
                    if (f.Length > 0) { frame = f; }
                }
                catch { }

                string dir = @"D:\EPLAN\项目\Linrrrx\" + projName;
                string path = dir + @"\" + projName + ".elk";
                string template = @"D:\EPLAN\Platform Data\2.9.4\Templates\EPLAN\IEC_bas003_sample_project.zw9";

                sb.AppendLine("newproject: name='" + projName + "'");
                sb.AppendLine("newproject: path=" + path);
                sb.AppendLine("newproject: template exists=" + File.Exists(template));

                if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); }

                bool exists = false;
                try { exists = pm.ExistsProject(path); } catch { }
                sb.AppendLine("newproject: exists=" + exists);

                if (!exists)
                {
                    try
                    {
                        pm.CreateProject(path, template);
                        sb.AppendLine("newproject: CreateProject -> ok");
                    }
                    catch (Exception e)
                    {
                        sb.AppendLine("newproject: CreateProject FAILED: " + e.Message);
                        return;
                    }
                }

                Project proj = null;
                try { proj = pm.OpenProject(path, ProjectManager.OpenMode.Exclusive); sb.AppendLine("newproject: opened exclusive"); }
                catch (Exception e) { sb.AppendLine("newproject: open FAILED: " + e.Message); return; }
                if (proj == null) { return; }

                sb.AppendLine("newproject: pages=" + proj.Pages.Length);

                // try a page with the requested frame
                PagePropertyList props = new PagePropertyList();
                props[Eplan.EplApi.DataModel.Properties.Page.PAGE_NAME] = "1";
                props[Eplan.EplApi.DataModel.Properties.Page.PAGE_FORMPLOT] = frame;

                Page p = new Page();
                p.Create(proj, Eplan.EplApi.DataModel.DocumentTypeManager.DocumentType.Circuit, props);
                sb.AppendLine("newproject: page=" + p.Name
                    + " size=" + Describe(p.Size, "")
                    + " actual=" + PlotFrameName(p)
                    + " (asked for " + frame + ")");
            }
            catch (Exception e)
            {
                sb.AppendLine("newproject FAILED: " + e.Message);
                if (e.InnerException != null) { sb.AppendLine("  inner: " + e.InnerException.Message); }
            }
        }

        /// <summary>
        /// Brute force: create a throwaway page for each candidate frame name and report the resulting
        /// page size, then delete the page. Tells us whether ANY frame name is honoured by this project.
        /// </summary>
        private static void TestFrameNames(StringBuilder sb, ProjectManager pm)
        {
            try
            {
                Project proj = ResolveProject(pm);
                if (proj == null) { sb.AppendLine("testframes: no project"); return; }

                string[] names = null;
                try { names = File.ReadAllLines(@"C:\Temp\probe_frames.txt"); } catch { }
                if (names == null || names.Length == 0) { sb.AppendLine("testframes: no names"); return; }

                for (int i = 0; i < names.Length; i++)
                {
                    string frame = names[i].Trim();
                    if (frame.Length == 0) { continue; }
                    try
                    {
                        PagePropertyList props = new PagePropertyList();
                        props[Eplan.EplApi.DataModel.Properties.Page.PAGE_NAME] = "ZZTEST";
                        props[Eplan.EplApi.DataModel.Properties.Page.PAGE_FORMPLOT] = frame;

                        Page p = new Page();
                        p.Create(proj, Eplan.EplApi.DataModel.DocumentTypeManager.DocumentType.Circuit, props);
                        sb.AppendLine("FRAME[" + frame + "] -> page=" + p.Name
                            + " size=" + Describe(p.Size, "")
                            + " actual=" + PlotFrameName(p));
                        try { p.Remove(); sb.AppendLine("    (test page removed)"); }
                        catch (Exception e) { sb.AppendLine("    remove failed: " + e.Message); }
                    }
                    catch (Exception e)
                    {
                        sb.AppendLine("FRAME[" + frame + "] -> ERROR: " + e.Message);
                    }
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("testframes FAILED: " + e.Message);
            }
        }

        /// <summary>Removes pages whose PAGE_NAME equals the content of C:\Temp\probe_pagename.txt.</summary>
        private static void DeletePageByName(StringBuilder sb, ProjectManager pm)
        {
            try
            {
                Project proj = ResolveProject(pm);
                if (proj == null) { sb.AppendLine("delpage: no project"); return; }

                string want = "";
                try { want = File.ReadAllText(@"C:\Temp\probe_pagename.txt").Trim(); } catch { }
                sb.AppendLine("delpage: looking for PAGE_NAME='" + want + "'");

                Page[] pages = proj.Pages;
                for (int i = 0; i < pages.Length; i++)
                {
                    string pn = "";
                    try { pn = pages[i].Properties[Eplan.EplApi.DataModel.Properties.Page.PAGE_NAME].ToString(); } catch { }
                    sb.AppendLine("  page[" + pages[i].Name + "] PAGE_NAME=[" + pn + "]");
                    if (want.Length > 0 && pn == want)
                    {
                        try { pages[i].Remove(); sb.AppendLine("    removed"); }
                        catch (Exception e) { sb.AppendLine("    remove FAILED: " + e.Message); }
                    }
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("delpage FAILED: " + e.Message);
            }
        }

        /// <summary>
        /// Creates a new circuit page, specifying the plot frame in the PagePropertyList at creation
        /// time (post-hoc assignment of PAGE_FORMPLOT is ignored by EPLAN). Reports the resulting size.
        /// </summary>
        private static void CreatePageWithFrame(StringBuilder sb, ProjectManager pm)
        {
            try
            {
                Project proj = ResolveProject(pm);
                if (proj == null) { sb.AppendLine("newpage: no project"); return; }

                string pageName = "";
                string frameName = "";
                try { pageName = File.ReadAllText(@"C:\Temp\probe_pagename.txt").Trim(); } catch { }
                try { frameName = File.ReadAllText(@"C:\Temp\probe_frame.txt").Trim(); } catch { }
                if (pageName.Length == 0) { pageName = "TEST_A1"; }

                sb.AppendLine("newpage: name='" + pageName + "' frame='" + frameName + "'");

                PagePropertyList props = new PagePropertyList();
                props[Eplan.EplApi.DataModel.Properties.Page.PAGE_NAME] = pageName;
                if (frameName.Length > 0)
                {
                    props[Eplan.EplApi.DataModel.Properties.Page.PAGE_FORMPLOT] = frameName;
                }

                Page p = new Page();
                p.Create(proj, Eplan.EplApi.DataModel.DocumentTypeManager.DocumentType.Circuit, props);
                sb.AppendLine("newpage: created, name=" + p.Name);
                sb.AppendLine("newpage: size=" + Describe(p.Size, "size"));
                sb.AppendLine("newpage: " + PlotFrameInfo(p));
            }
            catch (Exception e)
            {
                sb.AppendLine("newpage FAILED: " + e.Message);
                if (e.InnerException != null) { sb.AppendLine("  inner: " + e.InnerException.Message); }
            }
        }

        // ------------------------------------------------------------------ measuring / scaling

        /// <summary>Measures page, plot frame and content extents; optionally scales content to fit.</summary>
        private static void ReportBoxes(StringBuilder sb, ProjectManager pm, bool doShrink)
        {
            try
            {
                Project proj = ResolveProject(pm);
                if (proj == null) { sb.AppendLine("bbox: no project"); return; }

                Page[] pages = proj.Pages;
                for (int i = 0; i < pages.Length; i++)
                {
                    Page pg = pages[i];
                    sb.AppendLine("page[" + pg.Name + "] " + Describe(pg.Size, "Page.Size"));
                    sb.AppendLine("  " + PlotFrameInfo(pg));

                    double fx1, fy1, fx2, fy2;
                    bool haveFrame = TryFrameBox(pg, out fx1, out fy1, out fx2, out fy2);
                    sb.AppendLine("  frame box: " + (haveFrame
                        ? Fmt(fx1, fy1, fx2, fy2)
                        : "<unavailable>"));

                    double cx1, cy1, cx2, cy2;
                    int counted;
                    bool haveContent = TryContentBox(pg, out cx1, out cy1, out cx2, out cy2, out counted);
                    sb.AppendLine("  content box (" + counted + " objects): " + (haveContent
                        ? Fmt(cx1, cy1, cx2, cy2)
                        : "<unavailable>"));

                    DumpFirstBoxes(sb, pg);

                    if (!doShrink || !haveContent || !haveFrame) { continue; }

                    const double inset = 5.0;
                    double targetX = fx1 + inset;
                    double targetY = fy1 + inset;
                    double availW = (fx2 - inset) - targetX;
                    double availH = (fy2 - inset) - targetY;

                    double cw = cx2 - cx1;
                    double ch = cy2 - cy1;
                    if (cw <= 0 || ch <= 0 || availW <= 0 || availH <= 0)
                    {
                        sb.AppendLine("  shrink: degenerate box, skipped");
                        continue;
                    }

                    double s = Math.Min(1.0, Math.Min(availW / cw, availH / ch));
                    sb.AppendLine("  shrink: content=" + Math.Round(cw, 1) + "x" + Math.Round(ch, 1)
                        + " avail=" + Math.Round(availW, 1) + "x" + Math.Round(availH, 1)
                        + " -> factor=" + Math.Round(s, 4));

                    if (s >= 0.999)
                    {
                        sb.AppendLine("  shrink: already fits, nothing done");
                        continue;
                    }

                    // Scaling about origin O maps p -> O + (p-O)*s.
                    // Pick O so that the scaled lower-left corner lands exactly on (targetX, targetY).
                    double ox = (targetX - s * cx1) / (1.0 - s);
                    double oy = (targetY - s * cy1) / (1.0 - s);
                    sb.AppendLine("  shrink: scale origin = (" + Math.Round(ox, 2) + ", " + Math.Round(oy, 2) + ")");

                    Eplan.EplApi.Base.PointD origin = new Eplan.EplApi.Base.PointD(ox, oy);
                    Placement[] pls = pg.AllPlacements;
                    int scaled = 0, failed = 0;
                    for (int k = 0; k < pls.Length; k++)
                    {
                        try
                        {
                            pls[k].Scale(s, s, origin);
                            scaled++;
                        }
                        catch { failed++; }
                    }
                    sb.AppendLine("  shrink: scaled=" + scaled + " failed=" + failed);

                    double nx1, ny1, nx2, ny2;
                    int n2;
                    if (TryContentBox(pg, out nx1, out ny1, out nx2, out ny2, out n2))
                    {
                        sb.AppendLine("  content after: " + Fmt(nx1, ny1, nx2, ny2));
                    }
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("bbox FAILED: " + e.Message);
            }
        }

        /// <summary>Diagnostic: what exactly does GetBoundingBox() return?</summary>
        private static void DumpFirstBoxes(StringBuilder sb, Page pg)
        {
            Placement[] pls = null;
            try { pls = pg.AllPlacements; } catch { }
            if (pls == null) { sb.AppendLine("  dump: no placements"); return; }
            sb.AppendLine("  dump: AllPlacements=" + pls.Length);

            int shown = 0;
            for (int i = 0; i < pls.Length && shown < 3; i++)
            {
                if (pls[i] is MacroBox) { continue; }
                shown++;
                sb.AppendLine("  dump[" + shown + "] type=" + pls[i].GetType().FullName);

                object bb = null;
                try { bb = pls[i].GetBoundingBox(); }
                catch (Exception e) { sb.AppendLine("      GetBoundingBox threw: " + e.Message); continue; }
                if (bb == null) { sb.AppendLine("      bb = null"); continue; }

                sb.AppendLine("      bbType=" + bb.GetType().FullName);
                try
                {
                    System.Reflection.PropertyInfo[] ps = bb.GetType().GetProperties();
                    for (int k = 0; k < ps.Length; k++)
                    {
                        if (!ps[k].CanRead || ps[k].GetIndexParameters().Length > 0) { continue; }
                        string v = "?";
                        try { v = Convert.ToString(ps[k].GetValue(bb, null)); } catch (Exception e) { v = "err:" + e.Message; }
                        sb.AppendLine("      ." + ps[k].Name + " (" + ps[k].PropertyType.Name + ") = " + v);
                    }
                }
                catch { }
            }
        }

        private static bool TryContentBox(Page pg, out double x1, out double y1, out double x2, out double y2, out int counted)
        {
            x1 = double.MaxValue; y1 = double.MaxValue;
            x2 = double.MinValue; y2 = double.MinValue;
            counted = 0;

            Placement[] pls = null;
            try { pls = pg.AllPlacements; } catch { }
            if (pls == null) { return false; }

            for (int i = 0; i < pls.Length; i++)
            {
                if (pls[i] is MacroBox) { continue; }   // frame marker, not content
                object bb = null;
                try { bb = pls[i].GetBoundingBox(); } catch { }
                double a, b, c, d;
                if (bb == null || !TryBox(bb, out a, out b, out c, out d)) { continue; }
                if (a < x1) { x1 = a; }
                if (b < y1) { y1 = b; }
                if (c > x2) { x2 = c; }
                if (d > y2) { y2 = d; }
                counted++;
            }
            return counted > 0;
        }

        private static bool TryFrameBox(Page pg, out double x1, out double y1, out double x2, out double y2)
        {
            x1 = 0; y1 = 0; x2 = 0; y2 = 0;
            try
            {
                Eplan.EplApi.DataModel.MasterData.PlotFrame pf = pg.PlotFrame;
                if (pf == null) { return false; }
                Placement[] subs = pf.SubPlacements;
                if (subs == null || subs.Length == 0) { return false; }

                bool any = false;
                for (int i = 0; i < subs.Length; i++)
                {
                    object bb = null;
                    try { bb = subs[i].GetBoundingBox(); } catch { }
                    double a, b, c, d;
                    if (bb == null || !TryBox(bb, out a, out b, out c, out d)) { continue; }
                    if (!any) { x1 = a; y1 = b; x2 = c; y2 = d; any = true; }
                    else
                    {
                        if (a < x1) { x1 = a; }
                        if (b < y1) { y1 = b; }
                        if (c > x2) { x2 = c; }
                        if (d > y2) { y2 = d; }
                    }
                }
                return any;
            }
            catch { return false; }
        }

        /// <summary>Pulls x1/y1/x2/y2 out of the bounding box returned by GetBoundingBox().</summary>
        private static bool TryBox(object bb, out double x1, out double y1, out double x2, out double y2)
        {
            x1 = 0; y1 = 0; x2 = 0; y2 = 0;
            try
            {
                // EPLAN returns PointD[] { lowerLeft, upperRight }
                Array arr = bb as Array;
                if (arr != null && arr.Length >= 2)
                {
                    object ll = arr.GetValue(0);
                    object ur = arr.GetValue(1);
                    x1 = ToDouble(GetMember(ll, "X"));
                    y1 = ToDouble(GetMember(ll, "Y"));
                    x2 = ToDouble(GetMember(ur, "X"));
                    y2 = ToDouble(GetMember(ur, "Y"));
                    return true;
                }

                object lowerLeft = GetMember(bb, "LowerLeft");
                object upperRight = GetMember(bb, "UpperRight");
                if (lowerLeft != null && upperRight != null)
                {
                    x1 = ToDouble(GetMember(lowerLeft, "X"));
                    y1 = ToDouble(GetMember(lowerLeft, "Y"));
                    x2 = ToDouble(GetMember(upperRight, "X"));
                    y2 = ToDouble(GetMember(upperRight, "Y"));
                    return true;
                }

                object x = GetMember(bb, "X");
                object y = GetMember(bb, "Y");
                object w = GetMember(bb, "Width");
                object h = GetMember(bb, "Height");
                if (x != null && y != null && w != null && h != null)
                {
                    x1 = ToDouble(x); y1 = ToDouble(y);
                    x2 = x1 + ToDouble(w); y2 = y1 + ToDouble(h);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static object GetMember(object o, string name)
        {
            if (o == null) { return null; }
            try
            {
                System.Reflection.PropertyInfo p = o.GetType().GetProperty(name);
                if (p != null) { return p.GetValue(o, null); }
                System.Reflection.FieldInfo f = o.GetType().GetField(name);
                if (f != null) { return f.GetValue(o); }
            }
            catch { }
            return null;
        }

        private static double ToDouble(object o)
        {
            if (o == null) { return 0; }
            try { return Convert.ToDouble(o); } catch { return 0; }
        }

        private static string Fmt(double x1, double y1, double x2, double y2)
        {
            return "x[" + Math.Round(x1, 1) + ".." + Math.Round(x2, 1) + "]"
                 + " y[" + Math.Round(y1, 1) + ".." + Math.Round(y2, 1) + "]"
                 + " w=" + Math.Round(x2 - x1, 1) + " h=" + Math.Round(y2 - y1, 1);
        }

        private static string Describe(object o, string label)
        {
            if (o == null) { return label + "=<null>"; }
            StringBuilder t = new StringBuilder(label + "=");
            try
            {
                System.Reflection.PropertyInfo[] ps = o.GetType().GetProperties();
                for (int i = 0; i < ps.Length; i++)
                {
                    if (!ps[i].CanRead) { continue; }
                    Type pt = ps[i].PropertyType;
                    if (pt != typeof(double) && pt != typeof(float) && pt != typeof(int) && pt != typeof(string)) { continue; }
                    try { t.Append(ps[i].Name + ":" + ps[i].GetValue(o, null) + " "); } catch { }
                }
            }
            catch { }
            return t.ToString();
        }

        private static string PlotFrameInfo(Page pg)
        {
            try
            {
                Eplan.EplApi.DataModel.MasterData.PlotFrame pf = pg.PlotFrame;
                if (pf == null) { return "PlotFrame=<null/none>"; }
                return "PlotFrame=[" + pf.Name + "] " + Describe(pf.Size, "size");
            }
            catch (Exception e) { return "PlotFrame err: " + e.Message; }
        }

        // ------------------------------------------------------------------ mutations

        /// <summary>Clears the page property PAGE_FORMPLOT (the 图框 / page border / title block form).</summary>
        private static void RemovePlotFrames(StringBuilder sb, ProjectManager pm)
        {
            try
            {
                Project proj = ResolveProject(pm);
                sb.AppendLine("removeplotframe: project=" + (proj == null ? "null" : proj.ProjectName));
                if (proj == null) { return; }

                Page[] pages = proj.Pages;
                for (int i = 0; i < pages.Length; i++)
                {
                    string wp = "?";
                    try { wp = pages[i].WriteProtected.ToString(); } catch (Exception e) { wp = "err:" + e.Message; }
                    sb.AppendLine("  page[" + pages[i].Name + "] WriteProtected=" + wp
                        + " PlotFrame=" + PlotFrameName(pages[i])
                        + " PAGE_FORMPLOT=[" + ReadFormPlot(pages[i]) + "]");

                    try
                    {
                        string frameName = "";
                        try { frameName = File.ReadAllText(@"C:\Temp\probe_frame.txt").Trim(); } catch { }

                        try { pages[i].PauseWriteProtection(true); sb.AppendLine("      write protection paused"); }
                        catch (Exception e) { sb.AppendLine("      pause failed: " + e.Message); }

                        pages[i].Properties[Eplan.EplApi.DataModel.Properties.Page.PAGE_FORMPLOT] = frameName;
                        sb.AppendLine("      write PAGE_FORMPLOT='" + frameName + "' -> accepted (no exception)");

                        try { pages[i].PauseWriteProtection(false); } catch { }
                    }
                    catch (Exception e)
                    {
                        sb.AppendLine("      write '' FAILED: " + e.Message);
                    }

                    sb.AppendLine("      readback PAGE_FORMPLOT=[" + ReadFormPlot(pages[i]) + "]");
                    sb.AppendLine("      PlotFrame now=" + PlotFrameName(pages[i]));
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("removeplotframe FAILED: " + e.Message);
            }
        }

        /// <summary>Removes every MacroBox (macro frame) from every page.</summary>
        private static void DeleteMacroBoxes(StringBuilder sb, ProjectManager pm)
        {
            try
            {
                Project proj = ResolveProject(pm);
                sb.AppendLine("deleteframes: project=" + (proj == null ? "null" : proj.ProjectName));
                if (proj == null) { return; }

                int removed = 0;
                int failed = 0;
                Page[] pages = proj.Pages;
                for (int i = 0; i < pages.Length; i++)
                {
                    Placement[] placements = null;
                    try { placements = pages[i].AllPlacements; } catch { }
                    if (placements == null) { continue; }

                    System.Collections.Generic.List<Placement> boxes =
                        new System.Collections.Generic.List<Placement>();
                    for (int k = 0; k < placements.Length; k++)
                    {
                        if (placements[k] is MacroBox) { boxes.Add(placements[k]); }
                    }
                    sb.AppendLine("  page[" + pages[i].Name + "] macroBoxes=" + boxes.Count);

                    for (int k = 0; k < boxes.Count; k++)
                    {
                        try
                        {
                            try { ((MacroBox)boxes[k]).RemoveAllReferences(); } catch { }
                            boxes[k].Remove();
                            removed++;
                        }
                        catch (Exception e)
                        {
                            failed++;
                            sb.AppendLine("    remove FAILED: " + e.Message);
                        }
                    }
                }
                sb.AppendLine("deleteframes: removed=" + removed + " failed=" + failed);
            }
            catch (Exception e)
            {
                sb.AppendLine("deleteframes FAILED: " + e.Message);
            }
        }

        private static Project ResolveProject(ProjectManager pm)
        {
            Project proj = null;
            try { proj = pm.CurrentProject; } catch { }
            if (proj == null)
            {
                string path = File.ReadAllText(TargetFile, Encoding.UTF8).Trim();
                proj = pm.OpenProject(path, ProjectManager.OpenMode.Exclusive);
            }
            return proj;
        }

        // ------------------------------------------------------------------ read-only dumps

        private static void DumpPages(StringBuilder sb, string tag, Project p)
        {
            Page[] pages = null;
            try
            {
                pages = p.Pages;
            }
            catch (Exception e)
            {
                sb.AppendLine("    " + tag + " p.Pages FAILED: " + e.Message);
                return;
            }

            sb.AppendLine("    " + tag + " pageCount=" + (pages == null ? -1 : pages.Length));
            if (pages == null) { return; }

            for (int j = 0; j < pages.Length; j++)
            {
                string pn = "?";
                int fc = -1;
                try { pn = pages[j].Name; } catch (Exception e) { pn = "nameErr:" + e.Message; }
                try { fc = pages[j].Functions.Length; } catch { }
                sb.AppendLine("      PAGE[" + j + "] name=[" + pn + "] funcs=" + fc);
                Inventory(sb, pages[j]);
            }
        }

        private static void Inventory(StringBuilder sb, Page page)
        {
            try
            {
                System.Collections.Generic.Dictionary<string, int> counts =
                    new System.Collections.Generic.Dictionary<string, int>();

                Tally(sb, counts, "AllPlacements", page.AllPlacements);
                Tally(sb, counts, "AllGraphicalPlacements", SafeGraphical(page));

                foreach (System.Collections.Generic.KeyValuePair<string, int> kv in counts)
                {
                    sb.AppendLine("      OBJ " + kv.Key + " = " + kv.Value);
                }

                sb.AppendLine("      PlotFrame = " + PlotFrameName(page)
                    + "  PAGE_FORMPLOT=[" + ReadFormPlot(page) + "]");
            }
            catch (Exception e)
            {
                sb.AppendLine("      inventory FAILED: " + e.Message);
            }
        }

        private static string PlotFrameName(Page page)
        {
            try
            {
                Eplan.EplApi.DataModel.MasterData.PlotFrame pf = page.PlotFrame;
                return pf == null ? "<null / none>" : "[" + pf.Name + "]";
            }
            catch (Exception e)
            {
                return "err:" + e.Message;
            }
        }

        private static string ReadFormPlot(Page page)
        {
            try
            {
                return page.Properties[Eplan.EplApi.DataModel.Properties.Page.PAGE_FORMPLOT].ToString();
            }
            catch (Exception e)
            {
                return "err:" + e.Message;
            }
        }

        private static Placement[] SafeGraphical(Page page)
        {
            try { return page.AllGraphicalPlacements; }
            catch { return null; }
        }

        private static void Tally(StringBuilder sb,
            System.Collections.Generic.Dictionary<string, int> counts,
            string label,
            Placement[] items)
        {
            if (items == null)
            {
                sb.AppendLine("      " + label + ": <null>");
                return;
            }
            sb.AppendLine("      " + label + " total=" + items.Length);
            for (int i = 0; i < items.Length; i++)
            {
                string t = "?";
                try { t = items[i].GetType().Name; } catch { }
                if (counts.ContainsKey(t)) { counts[t] = counts[t] + 1; }
                else { counts[t] = 1; }
            }
        }

        private static void Write(StringBuilder sb)
        {
            try { File.WriteAllText(OutFile, sb.ToString(), new UTF8Encoding(true)); }
            catch { }
        }
    }
}
