using System.Text.Json.Serialization;

namespace EplanApi.Models;

/// <summary>capabilities.json 根节点。</summary>
public class CapabilityFile
{
    [JsonPropertyName("version")] public string Version { get; set; } = "0.0.0";
    [JsonPropertyName("updated")] public string? Updated { get; set; }
    [JsonPropertyName("runtime")] public Dictionary<string, string>? Runtime { get; set; }
    [JsonPropertyName("statusLevels")] public Dictionary<string, string>? StatusLevels { get; set; }
    [JsonPropertyName("actions")] public Dictionary<string, CapabilityAction> Actions { get; set; } = new();
    [JsonPropertyName("knownGaps")] public List<CapabilityGap> KnownGaps { get; set; } = new();
}

/// <summary>单个动作的能力声明。</summary>
public class CapabilityAction
{
    /// <summary>verified / pilot / blocked</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "pilot";

    /// <summary>internal / addin / script</summary>
    [JsonPropertyName("layer")] public string? Layer { get; set; }

    /// <summary>false = 代码里没有真实实现，禁止当作可用能力。</summary>
    [JsonPropertyName("implemented")] public bool Implemented { get; set; } = true;

    /// <summary>json / file / drawing / state</summary>
    [JsonPropertyName("produces")] public string? Produces { get; set; }

    [JsonPropertyName("params")] public Dictionary<string, string>? Params { get; set; }

    [JsonPropertyName("verify")] public VerifySpec? Verify { get; set; }

    [JsonPropertyName("limits")] public List<string>? Limits { get; set; }

    [JsonPropertyName("verifiedOn")] public string? VerifiedOn { get; set; }

    [JsonPropertyName("notes")] public string? Notes { get; set; }

    [JsonPropertyName("blockedReason")] public string? BlockedReason { get; set; }

    [JsonPropertyName("nextSteps")] public List<string>? NextSteps { get; set; }
}

/// <summary>独立校验规则。决定 Success=true 之后 Status 到底是 ok 还是 needs_review。</summary>
public class VerifySpec
{
    /// <summary>none | text_ok | file_delta | metrics_min | metrics_delta</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "text_ok";

    [JsonPropertyName("field")] public string? Field { get; set; }

    /// <summary>metrics_delta 时用于对比的基线字段（如 connectionsBefore）。</summary>
    [JsonPropertyName("against")] public string? Against { get; set; }

    [JsonPropertyName("min")] public double Min { get; set; }

    [JsonPropertyName("dirParam")] public string? DirParam { get; set; }

    /// <summary>mode=file_reported 时，metrics 里哪个字段是产物路径（默认 "file"）。</summary>
    [JsonPropertyName("fileField")] public string? FileField { get; set; }

    [JsonPropertyName("ext")] public List<string>? Ext { get; set; }
}

/// <summary>已知能力缺口（API 本身就做不到的事）。</summary>
public class CapabilityGap
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("evidence")] public string? Evidence { get; set; }
    [JsonPropertyName("workaround")] public string? Workaround { get; set; }
}
