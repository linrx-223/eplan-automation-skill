namespace EplanApi.Models;

/// <summary>
/// 统一请求体: { "action": "动作名", "parameters": { "键": "值" } }
/// </summary>
public class EplanRequest
{
    /// <summary>动作名，例如 OpenProject / ExportPdf（大小写不敏感）</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>动作参数键值对，值统一为字符串</summary>
    public Dictionary<string, string>? Parameters { get; set; }
}
