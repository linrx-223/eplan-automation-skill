namespace EplanApi.Models;

/// <summary>
/// 统一返回体。
/// 核心约定（Verification-first）：Success 只表示「调用没抛异常」，
/// 是否真的达成了任务要看 Status + Evidence。调用方不得只看 Success。
/// </summary>
public class EplanResponse
{
    /// <summary>兼容旧字段：引擎是否正常返回。不等于任务完成。</summary>
    public bool Success { get; set; }

    /// <summary>
    /// ok              = 已通过独立校验（证据充分）
    /// needs_review    = 引擎说成功但拿不到充分证据，必须人工复核
    /// capability_gap  = 能力缺口：当前实现做不到，交还 Agent 另寻路径
    /// failed          = 执行失败（异常 / 前置条件不满足）
    /// </summary>
    public string Status { get; set; } = "ok";

    public string Message { get; set; } = string.Empty;

    public string? Error { get; set; }

    /// <summary>归一化动作名，便于排查。</summary>
    public string? Action { get; set; }

    /// <summary>本次调用的追踪 ID，可在 trace.jsonl 里检索。</summary>
    public string? TraceId { get; set; }

    /// <summary>证据：产物文件（路径/大小/时间/hash/是否本轮新增）、对象计数 delta、耗时等。</summary>
    public Dictionary<string, object?>? Evidence { get; set; }

    /// <summary>能力缺口或失败时给 Agent 的下一步建议。</summary>
    public List<string>? NextSteps { get; set; }

    /// <summary>AddIn / 脚本回填的原始计数指标，由证据层消费。</summary>
    public Dictionary<string, object?>? Metrics { get; set; }
}
