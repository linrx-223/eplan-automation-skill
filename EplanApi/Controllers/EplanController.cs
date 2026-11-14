using System.Text.Json;
using EplanApi.Models;
using EplanApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace EplanApi.Controllers;

[ApiController]
[Route("api/eplan")]
public class EplanController : ControllerBase
{
    private readonly EplanService _service;
    private readonly CapabilityRegistry _registry;
    private readonly EvidenceService _evidence;

    public EplanController(EplanService service, CapabilityRegistry registry, EvidenceService evidence)
    {
        _service = service;
        _registry = registry;
        _evidence = evidence;
    }

    /// <summary>
    /// 统一接口：POST /api/eplan/execute
    /// 请求体: { "action": "动作名", "parameters": { "键": "值" } }
    /// 返回:   { success, status, message, error, action, traceId, evidence, nextSteps, metrics }
    /// 注意：只看 success 是错的，必须看 status（ok / needs_review / capability_gap / failed）。
    /// </summary>
    [HttpPost("execute")]
    public async Task<ActionResult<EplanResponse>> Execute([FromBody] EplanRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Action))
        {
            return BadRequest(new EplanResponse
            {
                Success = false,
                Status = "failed",
                Message = "action 不能为空",
                Error = "Missing action",
                NextSteps = new List<string> { "GET /api/eplan/capabilities 查看可用动作" }
            });
        }

        var result = await _service.ExecuteAsync(request.Action, request.Parameters);
        return Ok(result);
    }

    /// <summary>
    /// 能力清单：GET /api/eplan/capabilities
    /// 每个动作的状态等级（verified / pilot / blocked）、参数、限制、校验规则与已知缺口。
    /// Agent 在规划前应先读这个，而不是猜哪个动作能用。
    /// </summary>
    [HttpGet("capabilities")]
    public ActionResult Capabilities() => Ok(_registry.Snapshot());

    /// <summary>追踪记录：GET /api/eplan/trace?limit=20（新→旧）</summary>
    [HttpGet("trace")]
    public ActionResult Trace([FromQuery] int limit = 20)
    {
        if (limit <= 0 || limit > 500) limit = 20;
        var items = _evidence.ReadTrace(limit);
        return Ok(new
        {
            traceFile = _evidence.TracePath,
            count = items.Count,
            items
        });
    }

    /// <summary>探活：GET /api/eplan/health —— 同时报 WebAPI 与 AddIn 状态。</summary>
    [HttpGet("health")]
    public async Task<ActionResult<EplanResponse>> Health()
        => Ok(await _service.ExecuteAsync("GetStatus", new Dictionary<string, string>()));
}
