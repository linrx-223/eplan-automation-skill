// ⚠️ DEPRECATED：本脚本是历史占位实现，真实导出已移到 EplanAddin/EplanApiAddin.cs 的 DoExportPdf。
//    保留仅为记录演进过程，不要在 EplanService 里再启用它。
// EPLAN 动作脚本（由 EPLAN 通过 /Runscript 加载执行，不是 Web 项目的一部分）。
// 不要加入 Web 项目编译。参数来自环境变量 EPLAN_API_PARAMS，结果写回 EPLAN_RESULT_FILE。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Eplan.EplApi.ApplicationManagement;
// using Eplan.EplApi.HEServices;   // 导出 PDF 需要的命名空间（按版本调整）

public class ExportPdfScript : IEplAction
{
    public bool Execute(ActionCallingContext ctx)
    {
        try
        {
            string raw = Environment.GetEnvironmentVariable("EPLAN_API_PARAMS") ?? "{}";
            var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(raw)
                             ?? new Dictionary<string, string>();
            string project = parameters.TryGetValue("project", out var p) ? p : "";
            string outputDir = parameters.TryGetValue("outputDir", out var d) ? d : "";

            // ===== 真实 PDF 导出（按你的 EPLAN 版本补全）=====
            // var export = new Eplan.EplApi.HEServices.Export();  // 或 Print，视版本而定
            // export.Project = ...;
            // export.ExportPdf(Path.Combine(outputDir, project + ".pdf"), pageFormat);
            // =================================================
            WriteResult(true, $"[脚本占位] 已导出 PDF: 项目={project}, 输出目录={outputDir}", null);
        }
        catch (Exception ex)
        {
            WriteResult(false, "", ex.Message);
        }
        return true;
    }

    public void GetActionProperties(ref ActionProperties actionProperties) { }

    private static void WriteResult(bool success, string message, string error)
    {
        string resultPath = Environment.GetEnvironmentVariable("EPLAN_RESULT_FILE")
                            ?? Path.Combine(Path.GetTempPath(), "eplan_result.json");
        var dto = new { success, message, error };
        File.WriteAllText(resultPath, JsonSerializer.Serialize(dto));
    }
}
