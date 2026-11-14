// EPLAN 动作脚本（由 EPLAN 通过 /Runscript 加载执行，不是 Web 项目的一部分）。
// 不要加入 Web 项目编译。参数来自环境变量 EPLAN_API_PARAMS，结果写回 EPLAN_RESULT_FILE。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Eplan.EplApi.ApplicationManagement;

public class OpenProjectScript : IEplAction
{
    public bool Execute(ActionCallingContext ctx)
    {
        try
        {
            string raw = Environment.GetEnvironmentVariable("EPLAN_API_PARAMS") ?? "{}";
            var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(raw)
                             ?? new Dictionary<string, string>();
            string path = parameters.TryGetValue("path", out var p) ? p : "";

            var app = new Application();
            if (!app.IsProjectOpened(path))
                app.OpenProject(path, ProjectOpenOption_e.SelectionDialogBoxNotShown);

            WriteResult(true, $"已打开项目: {path}", null);
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
