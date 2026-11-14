using EplanApi.Models;

namespace EplanApi.Services;

/// <summary>
/// EPLAN 引擎抽象：屏蔽底层驱动方式差异。
/// 新增动作时：在此接口加方法 → 各引擎实现 → EplanService 的 switch 加一行映射。
/// </summary>
public interface IEplanEngine
{
    Task<EplanResponse> OpenProjectAsync(Dictionary<string, string> parameters, CancellationToken ct = default);

    Task<EplanResponse> ExportPdfAsync(Dictionary<string, string> parameters, CancellationToken ct = default);

    Task<EplanResponse> CreateProjectAsync(Dictionary<string, string> parameters, CancellationToken ct = default);

    /// <summary>在当前项目里建页（走 AddIn）。</summary>
    Task<EplanResponse> CreatePagesAsync(Dictionary<string, string> parameters, CancellationToken ct = default);

    /// <summary>主电路页画 3 个电机回路（走 AddIn）。</summary>
    Task<EplanResponse> DrawMainCircuitAsync(Dictionary<string, string> parameters, CancellationToken ct = default);

    /// <summary>PLC I/O 页画 PLC/DI/DO（走 AddIn）。</summary>
    Task<EplanResponse> DrawPlcIOAsync(Dictionary<string, string> parameters, CancellationToken ct = default);

    /// <summary>梯形图页画急停/线圈 + PLC 电源（走 AddIn）。</summary>
    Task<EplanResponse> DrawLadderAsync(Dictionary<string, string> parameters, CancellationToken ct = default);

    /// <summary>按 ABB 电机控制柜原理图分区布局拼图（走 AddIn）。</summary>
    Task<EplanResponse> DrawAbbPanelAsync(Dictionary<string, string> parameters, CancellationToken ct = default);

    /// <summary>符号级精细绘图 + 真实连线物化（走 AddIn）。参数 project/spec/pageName。</summary>
    Task<EplanResponse> DrawSchematicAsync(Dictionary<string, string> parameters, CancellationToken ct = default);

    /// <summary>查询工程符号库可用符号（走 AddIn）。参数 project/keyword。</summary>
    Task<EplanResponse> ListSymbolsAsync(Dictionary<string, string> parameters, CancellationToken ct = default);

    /// <summary>按页名删除页（走 AddIn）。用于清理重复页/画错的页。参数 project/pageName/dryRun。</summary>
    Task<EplanResponse> DeletePagesAsync(Dictionary<string, string> parameters, CancellationToken ct = default);

    /// <summary>抓取 EPLAN 主窗口截图（走 AddIn）。用于「边画边看」，产物为 PNG。</summary>
    Task<EplanResponse> CaptureScreenAsync(Dictionary<string, string> parameters, CancellationToken ct = default);
}
