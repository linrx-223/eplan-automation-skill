using System.Text.Json;
using EplanApi.Models;

namespace EplanApi.Services;

/// <summary>
/// 能力注册表：读 capabilities.json，提供「动作是否被允许执行」的权威判断。
/// 未登记的动作一律拒绝 —— 不允许出现「代码能跑但没人知道它可靠不可靠」的暗能力。
/// </summary>
public sealed class CapabilityRegistry
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly Dictionary<string, CapabilityAction> _byName =
        new(StringComparer.OrdinalIgnoreCase);

    public CapabilityFile Document { get; }
    public string SourcePath { get; }
    public string LoadError { get; private set; } = "";

    public CapabilityRegistry(IWebHostEnvironment env, IConfiguration cfg)
    {
        var candidates = new List<string>();
        var configured = cfg["Eplan:CapabilitiesFile"];
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        candidates.Add(Path.Combine(env.ContentRootPath, "capabilities.json"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "capabilities.json"));

        SourcePath = candidates.FirstOrDefault(File.Exists) ?? candidates[^1];

        if (!File.Exists(SourcePath))
        {
            Document = new CapabilityFile();
            LoadError = $"找不到能力注册表: {SourcePath}";
            return;
        }

        try
        {
            var json = ReadAllTextShared(SourcePath);
            Document = JsonSerializer.Deserialize<CapabilityFile>(json, JsonOpts) ?? new CapabilityFile();
            foreach (var kv in Document.Actions)
                _byName[kv.Key] = kv.Value;
        }
        catch (Exception ex)
        {
            Document = new CapabilityFile();
            LoadError = "能力注册表解析失败: " + ex.Message;
        }
    }

    private static string ReadAllTextShared(string p)
    {
        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    /// <summary>按动作名查能力；未登记返回 null。</summary>
    public CapabilityAction? Get(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return null;
        return _byName.TryGetValue(action.Trim(), out var cap) ? cap : null;
    }

    /// <summary>动作名归一化（用于 trace / 回显）。</summary>
    public string Canonical(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return "";
        foreach (var k in _byName.Keys)
            if (string.Equals(k, action.Trim(), StringComparison.OrdinalIgnoreCase)) return k;
        return action.Trim();
    }

    public IEnumerable<string> ActionNames => _byName.Keys;

    /// <summary>对外快照（GET /api/eplan/capabilities）。</summary>
    public object Snapshot() => new
    {
        version = Document.Version,
        updated = Document.Updated,
        runtime = Document.Runtime,
        statusLevels = Document.StatusLevels,
        sourcePath = SourcePath,
        loadError = LoadError,
        actions = _byName.ToDictionary(k => k.Key, v => (object)v.Value),
        knownGaps = Document.KnownGaps
    };
}
