#if EPLAN_API
using System.Threading;
using System.Threading.Tasks;
using EplanApi.Models;

namespace EplanApi.Services;

/// <summary>
/// 进程内 EPLAN API 引擎（条件编译：dotnet run /p:EPLAN_API=true 且已引用 API 程序集）。
/// 注意：EPLAN API 必须在安装了 EPLAN 的机器上、通常与 EPLAN 同进程或由 EPLAN 宿主加载；
/// 通过 Standalone API 可在外部进程驱动一个隐藏的 EPLAN 实例。
/// HintPath 与 API 类名请按你的 EPLAN 版本调整。
/// </summary>
public class EplanApiEngine : IEplanEngine
{
    public Task<EplanResponse> OpenProjectAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
    {
        try
        {
            var path = parameters.TryGetValue("path", out var p) ? p : "";
            var app = new Eplan.EplApi.ApplicationManagement.Application();
            app.OpenProject(path, Eplan.EplApi.ApplicationManagement.ProjectOpenOption_e.SelectionDialogBoxNotShown);
            return Task.FromResult(new EplanResponse { Success = true, Message = $"已打开项目: {path}" });
        }
        catch (System.Exception ex)
        {
            return Task.FromResult(new EplanResponse { Success = false, Message = "打开项目失败", Error = ex.Message });
        }
    }

    public Task<EplanResponse> ExportPdfAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
    {
        try
        {
            var project = parameters.TryGetValue("project", out var p) ? p : "";
            var outputDir = parameters.TryGetValue("outputDir", out var d) ? d : "";

            // TODO: 按版本调用 Eplan.EplApi.HEServices.Print / Export 生成 PDF。
            // 例如：var export = new Eplan.EplApi.HEServices.Export(); export.ExportPdf(...);
            return Task.FromResult(new EplanResponse
            {
                Success = true,
                Message = $"[待实现] 项目={project}, 输出目录={outputDir}"
            });
        }
        catch (System.Exception ex)
        {
            return Task.FromResult(new EplanResponse { Success = false, Message = "导出 PDF 失败", Error = ex.Message });
        }
    }
}
#endif
