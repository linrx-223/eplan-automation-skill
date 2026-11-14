using System.Diagnostics;
using System.Text.Json;
using EplanApi.Models;
using Microsoft.Extensions.Configuration;

namespace EplanApi.Services;

public class ScriptEngine : IEplanEngine
{
    private readonly IConfiguration _config;

    public ScriptEngine(IConfiguration config)
    {
        _config = config;
    }

    private string ExePath => _config["Eplan:ExecutablePath"] ?? "";
    private string ScriptDir => _config["Eplan:ScriptDirectory"] ?? "Scripts";
    private string ExtraArgs => _config["Eplan:ExtraArguments"] ?? "";
    private string ResultFile => _config["Eplan:ResultFile"] ?? Path.Combine(Path.GetTempPath(), "eplan_result.json");
    private bool CloseAfterScript => bool.TryParse(_config["Eplan:CloseAfterScript"], out var b) && b;

    private async Task<EplanResponse> RunScriptAsync(string scriptFile, Dictionary<string, string> parameters, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ExePath) || !File.Exists(ExePath))
            return new EplanResponse
            {
                Success = false,
                Message = "未配置或找不到 EPLAN 可执行文件",
                Error = "Eplan exe not found"
            };

        var scriptPath = Path.Combine(AppContext.BaseDirectory, ScriptDir, scriptFile);
        if (!File.Exists(scriptPath))
            return new EplanResponse
            {
                Success = false,
                Message = $"找不到脚本: {scriptPath}",
                Error = "Script not found"
            };

        var asciiScriptPath = CopyScriptToAsciiTemp(scriptPath);
        if (File.Exists(ResultFile)) File.Delete(ResultFile);

        // 使用 /Runscript 加载 IEplAction 脚本。注意：参数通过 Arguments 字符串原样传给 EPLAN，
        // /Variant:"Electric P8" 整体是一个参数，避免被空格拆散。
        var args = string.IsNullOrWhiteSpace(ExtraArgs) ? "" : ExtraArgs + " ";
        args += $"/Runscript:\"{asciiScriptPath}\"";
        if (CloseAfterScript) args += " /CloseWithoutSave /AutoClose";

        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = false,
        };

        psi.EnvironmentVariables["EPLAN_API_PARAMS"] = JsonSerializer.Serialize(parameters);
        psi.EnvironmentVariables["EPLAN_RESULT_FILE"] = ResultFile;

        using var proc = Process.Start(psi);
        if (proc is null)
            return new EplanResponse { Success = false, Message = "无法启动 EPLAN 进程", Error = "Process start failed" };

        var sw = Stopwatch.StartNew();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        while (sw.Elapsed < TimeSpan.FromSeconds(180))
        {
            await Task.Delay(1000, ct);

            if (File.Exists(ResultFile))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(ResultFile, ct);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var fromScript = JsonSerializer.Deserialize<EplanResponse>(json, opts);
                        if (fromScript is not null) return fromScript;
                    }
                }
                catch { }
            }

            if (proc.HasExited)
            {
                return proc.ExitCode == 0
                    ? new EplanResponse { Success = false, Message = "EPLAN 已退出，但未返回结果文件", Error = "No result file" }
                    : new EplanResponse { Success = false, Message = $"EPLAN 退出码 {proc.ExitCode}", Error = "详见 EPLAN 日志" };
            }
        }

        return new EplanResponse
        {
            Success = false,
            Message = "180 秒内 EPLAN 脚本没有返回结果",
            Error = "Timeout"
        };
    }

    private static string CopyScriptToAsciiTemp(string originalPath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "EplanApiScripts");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, Path.GetFileName(originalPath));
        File.Copy(originalPath, tempPath, overwrite: true);
        return tempPath;
    }

    /// <summary>
    /// 建项目 / 建页：转发给跑在 EPLAN 进程内的 AddIn（http://127.0.0.1:5002）。
    /// 不走 /Runscript 命令行脚本注入 —— EPLAN 2.9.4 下该方式不可靠且受 LockingStep 限制。
    /// </summary>
    private async Task<EplanResponse> CallAddInAsync(string action, Dictionary<string, string> parameters, CancellationToken ct)
    {
        const string addinUrl = "http://127.0.0.1:5002/api/addin/";
        try
        {
            // UseProxy=false：本机若配了系统代理，127.0.0.1 会被代理接管并返回 502 文本，导致 JSON 解析失败
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                Timeout = TimeSpan.FromSeconds(150)
            };
            var payload = JsonSerializer.Serialize(new { action, parameters });
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var resp = await http.PostAsync(addinUrl, content, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<EplanResponse>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return result ?? new EplanResponse { Success = false, Error = "AddIn 返回空" };
        }
        catch (Exception ex)
        {
            return new EplanResponse
            {
                Success = false,
                Message = "无法连接 EPLAN AddIn(5002)。请先启动 EPLAN 并确保插件已加载（运行 LoadAddIn.cs 引导脚本）。",
                Error = ex.Message
            };
        }
    }

    public Task<EplanResponse> OpenProjectAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
        => RunScriptAsync("OpenProject.cs", parameters, ct);

    // PDF 导出已改为 AddIn 进程内实现（Scripts/ExportPdf.cs 是占位脚本，不能用）
    public Task<EplanResponse> ExportPdfAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
        => CallAddInAsync("ExportPdf", parameters, ct);

    public Task<EplanResponse> DeletePagesAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
        => CallAddInAsync("DeletePages", parameters, ct);

    public Task<EplanResponse> CreateProjectAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
        => CallAddInAsync("CreateProject", parameters, ct);

    public Task<EplanResponse> CreatePagesAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
        => CallAddInAsync("CreatePages", parameters, ct);

    public Task<EplanResponse> DrawMainCircuitAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
        => CallAddInAsync("DrawMainCircuit", parameters, ct);

    public Task<EplanResponse> DrawPlcIOAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
        => CallAddInAsync("DrawPlcIO", parameters, ct);

    public Task<EplanResponse> DrawLadderAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
        => CallAddInAsync("DrawLadder", parameters, ct);

    public Task<EplanResponse> DrawAbbPanelAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
        => CallAddInAsync("DrawAbbPanel", parameters, ct);

    public Task<EplanResponse> DrawSchematicAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
        => CallAddInAsync("DrawSchematic", parameters, ct);

    public Task<EplanResponse> ListSymbolsAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
        => CallAddInAsync("ListSymbols", parameters, ct);

    public Task<EplanResponse> CaptureScreenAsync(Dictionary<string, string> parameters, CancellationToken ct = default)
        => CallAddInAsync("CaptureScreen", parameters, ct);
}
