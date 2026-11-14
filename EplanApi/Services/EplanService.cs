using System.Diagnostics;
using EplanApi.Models;

namespace EplanApi.Services;

/// <summary>
/// EPLAN 操作服务：动作分发 + 能力门禁 + 独立校验 + 追踪留痕。
/// 执行链路固定为：
///   能力查询 → (未登记/blocked 直接拒绝) → 引擎执行 → 证据采集 → 独立校验 → Status → 落 trace
/// 任何一步都不能省略，否则回到「工具说成功就算成功」的老路。
/// </summary>
public class EplanService
{
    // 127.0.0.1 必须绕过系统代理：本机若配了 HTTP 代理，走代理会拿到 502 文本而不是 AddIn 的 JSON
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private readonly IEplanEngine _engine;
    private readonly CapabilityRegistry _registry;
    private readonly EvidenceService _evidence;

    public EplanService(IEplanEngine engine, CapabilityRegistry registry, EvidenceService evidence)
    {
        _engine = engine;
        _registry = registry;
        _evidence = evidence;
    }

    public async Task<EplanResponse> ExecuteAsync(string action, Dictionary<string, string>? parameters)
    {
        parameters ??= new Dictionary<string, string>();
        var startedUtc = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        var canonical = _registry.Canonical(action);
        var traceId = DateTime.Now.ToString("HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var cap = _registry.Get(action);

        EplanResponse result;

        if (cap is null)
        {
            result = new EplanResponse
            {
                Success = false,
                Status = "failed",
                Message = $"未登记的动作: {canonical}。所有动作必须在 capabilities.json 里声明能力等级后才允许调用。",
                Error = "Unknown action",
                NextSteps = new List<string>
                {
                    "GET /api/eplan/capabilities 查看已登记动作",
                    "若确有新能力，先在 capabilities.json 登记（status/verify/limits）再实现"
                }
            };
        }
        else if (!cap.Implemented ||
                 string.Equals(cap.Status, "blocked", StringComparison.OrdinalIgnoreCase))
        {
            result = new EplanResponse
            {
                Success = false,
                Status = "capability_gap",
                Message = $"能力缺口（{canonical}）：{cap.BlockedReason ?? "当前实现无法产出真实结果"}",
                Error = "CAPABILITY_GAP",
                NextSteps = cap.NextSteps ?? new List<string>
                {
                    "不要重试本动作，它不会产生真实结果",
                    "改用其它已验证动作，或按 blockedReason 补齐实现后再登记"
                }
            };
        }
        else
        {
            result = await DispatchAsync(canonical, parameters);

            sw.Stop();
            var evidence = _evidence.Build(canonical, cap, parameters, result, sw.Elapsed, startedUtc);
            var (status, reason) = _evidence.Verify(cap, result, evidence);

            evidence["verify"] = new Dictionary<string, object?>
            {
                ["mode"] = cap.Verify?.Mode ?? "text_ok",
                ["passed"] = status == "ok",
                ["reason"] = reason
            };

            if (status == "needs_review" && reason is not null)
            {
                evidence["reason"] = reason;
                result.Message = string.IsNullOrWhiteSpace(result.Message)
                    ? "未通过独立校验：" + reason
                    : result.Message + " ｜ 未通过独立校验：" + reason;
                result.NextSteps ??= new List<string>();
                result.NextSteps.Add("引擎声称成功但证据不足，请人工确认后再交付");
            }
            else if (reason is not null)
            {
                evidence["reason"] = reason;
            }

            // 引擎可能只回 error 不回 message（AddIn 的失败分支就是这样），别让调用方看到空消息
            if (status == "failed" && string.IsNullOrWhiteSpace(result.Message))
                result.Message = result.Error ?? reason ?? "执行失败（引擎未给出原因）";

            result.Status = status;
            result.Evidence = evidence;
        }

        sw.Stop();
        result.Action = canonical;
        result.TraceId = traceId;
        _evidence.Trace(result, parameters, sw.Elapsed);
        return result;
    }

    /// <summary>真实执行分发。只有通过能力门禁的动作才会走到这里。</summary>
    private Task<EplanResponse> DispatchAsync(string action, Dictionary<string, string> parameters)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            // 自检：不碰 EPLAN，只证明 WebAPI 活着
            case "ping":
                return Task.FromResult(new EplanResponse
                {
                    Success = true,
                    Message = "服务正常，未连接 EPLAN。你传的参数：" +
                              string.Join(", ", parameters.Select(p => $"{p.Key}={p.Value}"))
                });

            // 分别探测 WebAPI 与 AddIn，避免把「API 活着」误当成「EPLAN 活着」
            case "getstatus":
                return GetStatusAsync();

            case "openproject": return _engine.OpenProjectAsync(parameters);
            case "exportpdf": return _engine.ExportPdfAsync(parameters);
            case "createproject": return _engine.CreateProjectAsync(parameters);
            case "createpages": return _engine.CreatePagesAsync(parameters);
            case "drawmaincircuit": return _engine.DrawMainCircuitAsync(parameters);
            case "drawplcio": return _engine.DrawPlcIOAsync(parameters);
            case "drawladder": return _engine.DrawLadderAsync(parameters);
            case "drawabbpanel": return _engine.DrawAbbPanelAsync(parameters);
            case "drawschematic": return _engine.DrawSchematicAsync(parameters);
            case "listsymbols": return _engine.ListSymbolsAsync(parameters);
            case "capturescreen": return _engine.CaptureScreenAsync(parameters);
            case "deletepages": return _engine.DeletePagesAsync(parameters);

            default:
                return Task.FromResult(new EplanResponse
                {
                    Success = false, Status = "failed",
                    Message = $"动作已登记到注册表，但引擎没有对应实现: {action}",
                    Error = "Unimplemented in engine"
                });
        }
    }

    /// <summary>双通道探活：WebAPI（本进程） + AddIn（EPLAN 进程内 5002）。</summary>
    private static async Task<EplanResponse> GetStatusAsync()
    {
        bool addinAlive = false;
        string addinDetail;
        string addinBuild = "";
        try
        {
            using var content = new StringContent("{\"action\":\"Ping\",\"parameters\":{}}",
                System.Text.Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync("http://127.0.0.1:5002/api/addin/", content);
            var txt = await resp.Content.ReadAsStringAsync();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(txt);
                addinAlive = doc.RootElement.TryGetProperty("success", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.True;
                if (doc.RootElement.TryGetProperty("build", out var b))
                    addinBuild = b.GetString() ?? "";
            }
            catch
            {
                // 非 JSON（代理错误页 / 空响应）一律视为离线
                addinAlive = false;
            }
            addinDetail = txt.Length > 200 ? txt[..200] : txt;
        }
        catch (Exception ex)
        {
            addinDetail = ex.GetType().Name + ": " + ex.Message;
        }

        return new EplanResponse
        {
            Success = true,
            Message = addinAlive
                ? "WebAPI 正常；EPLAN AddIn 在线（5002 可 Ping 通）"
                : "WebAPI 正常；EPLAN AddIn 离线（5002 无响应，EPLAN 未启动或插件未加载）",
            Metrics = new Dictionary<string, object?>
            {
                ["webapiAlive"] = 1,
                ["addinAlive"] = addinAlive ? 1 : 0,
                ["addinBuild"] = addinBuild,
                ["addinEndpoint"] = "http://127.0.0.1:5002/api/addin/",
                ["addinDetail"] = addinDetail
            },
            NextSteps = addinAlive ? null : new List<string>
            {
                "启动 EPLAN",
                "用 EplanRemote.exe ExecuteScript 跑 LoadAddIn.cs 引导脚本加载插件",
                "再调 GetStatus 确认 addinAlive=1"
            }
        };
    }
}
