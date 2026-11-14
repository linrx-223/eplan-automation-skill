using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EplanApi.Models;

namespace EplanApi.Services;

/// <summary>
/// 证据层。两条职责：
///  1) 校验（Verify）：引擎说 Success=true 之后，独立核实任务是否真的完成，给出 Status。
///  2) 留痕（Trace）：每次调用追加一行 JSONL 到 logs/trace.jsonl，事后可审计。
/// 设计原则：证据必须来自「执行之后重新观察现实」（文件是否真的新了、计数是否真的涨了），
/// 不能来自引擎自己的返回值。
/// </summary>
public sealed class EvidenceService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private readonly string _tracePath;
    private readonly object _lock = new();

    public EvidenceService(IWebHostEnvironment env, IConfiguration cfg)
    {
        var configured = cfg["Eplan:TraceFile"];
        var path = string.IsNullOrWhiteSpace(configured) ? "logs/trace.jsonl" : configured;
        _tracePath = Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path);
        try { Directory.CreateDirectory(Path.GetDirectoryName(_tracePath)!); } catch { }
    }

    public string TracePath => _tracePath;

    // ---------------------------------------------------------------- 证据采集

    /// <summary>
    /// 组装证据。files 为「本轮新增/变化」的产物文件清单（由 Verify 模式需要时填充）。
    /// </summary>
    public Dictionary<string, object?> Build(
        string action,
        CapabilityAction? cap,
        Dictionary<string, string> parameters,
        EplanResponse result,
        TimeSpan elapsed,
        DateTime startUtc)
    {
        var ev = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["capabilityStatus"] = cap?.Status ?? "unregistered",
            ["layer"] = cap?.Layer,
            ["elapsedMs"] = (long)elapsed.TotalMilliseconds,
            ["startedUtc"] = startUtc.ToString("O"),
            ["engineSuccess"] = result.Success,
            ["params"] = parameters
        };

        if (result.Metrics is { Count: > 0 })
            ev["metrics"] = result.Metrics;

        // 产物校验：要么按目录扫（file_delta），要么按引擎自报的路径核实（file_reported）
        var mode = cap?.Verify?.Mode;
        if (mode == "file_reported" || mode == "file_delta" ||
            string.Equals(cap?.Produces, "file", StringComparison.OrdinalIgnoreCase))
        {
            var files = mode == "file_reported"
                ? CollectReportedFile(cap!, result, startUtc)
                : CollectFiles(cap!, parameters, startUtc);

            ev["files"] = files;
            ev["filesChanged"] = files.Count(f =>
                f.TryGetValue("changed", out var c) && c is bool b && b);
        }

        return ev;
    }

    /// <summary>
    /// 按引擎自报的产物路径核实：文件必须真实存在，且 mtime 落在本轮之内。
    /// 这比扫目录更强——引擎说「我写到了 X」，就去 X 那里验。
    /// </summary>
    private static List<Dictionary<string, object?>> CollectReportedFile(
        CapabilityAction cap, EplanResponse result, DateTime startUtc)
    {
        var list = new List<Dictionary<string, object?>>();
        var field = cap.Verify?.FileField ?? "file";
        if (result.Metrics is null || !result.Metrics.TryGetValue(field, out var raw) || raw is null)
            return list;

        string path = raw is JsonElement je
            ? (je.ValueKind == JsonValueKind.String ? je.GetString() ?? "" : je.ToString())
            : raw.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(path)) return list;

        bool exists = File.Exists(path);
        var entry = new Dictionary<string, object?>
        {
            ["path"] = path.Replace('\\', '/'),
            ["exists"] = exists,
            ["changed"] = false
        };

        if (exists)
        {
            try
            {
                var fi = new FileInfo(path);
                entry["size"] = fi.Length;
                entry["mtimeUtc"] = fi.LastWriteTimeUtc.ToString("O");
                entry["sha256_16"] = ShortHash(path);
                entry["changed"] = fi.LastWriteTimeUtc >= startUtc.AddSeconds(-2);
            }
            catch { }
        }

        list.Add(entry);
        return list;
    }

    /// <summary>扫描产物目录，返回本轮之后写入的文件证据。</summary>
    private static List<Dictionary<string, object?>> CollectFiles(
        CapabilityAction cap, Dictionary<string, string> parameters, DateTime startUtc)
    {
        var list = new List<Dictionary<string, object?>>();
        string? dir = null;

        var dirParam = cap.Verify?.DirParam;
        if (!string.IsNullOrWhiteSpace(dirParam) && parameters.TryGetValue(dirParam, out var d) && !string.IsNullOrWhiteSpace(d))
            dir = File.Exists(d) ? Path.GetDirectoryName(d) : d;
        else
        {
            // 从参数里挑一个存在的目录兜底
            foreach (var kv in parameters)
            {
                if (string.IsNullOrWhiteSpace(kv.Value)) continue;
                if (Directory.Exists(kv.Value)) { dir = kv.Value; break; }
            }
        }

        if (dir is null || !Directory.Exists(dir)) return list;

        string[] patterns = cap.Verify?.Ext is { Count: > 0 }
            ? cap.Verify.Ext.Select(e => "*" + e).ToArray()
            : new[] { "*.*" };

        try
        {
            foreach (var pat in patterns)
            {
                foreach (var f in Directory.EnumerateFiles(dir, pat, SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        // 留 2 秒容忍度：文件系统时间戳精度 + 时钟漂移
                        if (fi.LastWriteTimeUtc < startUtc.AddSeconds(-2)) continue;
                        list.Add(new Dictionary<string, object?>
                        {
                            ["path"] = fi.FullName.Replace('\\', '/'),
                            ["size"] = fi.Length,
                            ["mtimeUtc"] = fi.LastWriteTimeUtc.ToString("O"),
                            ["sha256_16"] = ShortHash(fi.FullName),
                            ["changed"] = true
                        });
                        if (list.Count >= 20) return list;
                    }
                    catch { }
                }
            }
        }
        catch { }

        return list;
    }

    private static string ShortHash(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length == 0) return "empty";
            if (fi.Length > 64L * 1024 * 1024) return "skip(>64MB)";
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(fs);
            return Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }
        catch { return "hash_error"; }
    }

    // ---------------------------------------------------------------- 独立校验

    /// <summary>
    /// 判定 Status。规则：
    ///  - 引擎失败 → failed
    ///  - 引擎成功但校验不过 → needs_review + 原因
    ///  - 校验通过 → ok
    /// </summary>
    public (string status, string? reason) Verify(
        CapabilityAction? cap, EplanResponse result, Dictionary<string, object?> evidence)
    {
        if (cap is null) return ("failed", "unregistered_action");
        if (!result.Success) return ("failed", result.Error ?? "engine_failed");

        var mode = cap.Verify?.Mode ?? "text_ok";

        switch (mode)
        {
            case "none":
                return ("ok", null);

            case "file_delta":
            case "file_reported":
            {
                var n = evidence.TryGetValue("filesChanged", out var v) ? ToInt(v) : 0;
                if (n > 0) return ("ok", null);

                // 分清三种失败：没产物 / 产物不存在 / 产物是旧文件
                string detail = "";
                if (evidence.TryGetValue("files", out var fv) &&
                    fv is List<Dictionary<string, object?>> files && files.Count > 0)
                {
                    var f = files[0];
                    var p = f.TryGetValue("path", out var pv) ? pv : "(未知)";
                    var exists = f.TryGetValue("exists", out var ev2) && ev2 is bool b2 && b2;
                    detail = exists
                        ? $"（文件存在但非本轮写入：{p}）"
                        : $"（引擎声明的产物不存在：{p}）";
                }
                return ("needs_review", "no_new_file：产物未通过校验" + detail + "，引擎的成功声明不成立");
            }

            case "metrics_min":
            {
                var field = cap.Verify!.Field;
                var actual = GetMetric(result, field);
                if (actual is null)
                    return ("needs_review", $"metrics_missing：引擎未回填 {field}，无法独立核实（AddIn 可能是旧版本，需重新部署）");
                if (actual.Value < cap.Verify.Min)
                    return ("needs_review", $"metrics_below_min：{field}={actual.Value} < 期望 {cap.Verify.Min}");
                return ("ok", null);
            }

            case "metrics_delta":
            {
                var field = cap.Verify!.Field;
                var against = cap.Verify.Against;
                var now = GetMetric(result, field);
                var before = GetMetric(result, against);
                if (now is null)
                    return ("needs_review", $"metrics_missing：引擎未回填 {field}，无法独立核实（AddIn 可能是旧版本，需重新部署）");
                if (before is null)
                    return ("needs_review", $"metrics_missing：引擎未回填 {against}，无法算增量");
                var delta = now.Value - before.Value;
                if (delta < cap.Verify.Min)
                    return ("needs_review", $"metrics_delta：{field} 增量={delta} < 期望 {cap.Verify.Min}（图上可能有线，但项目数据里没有真实连接）");
                return ("ok", null);
            }

            default: // text_ok
                return ("ok", null);
        }
    }

    private static double? GetMetric(EplanResponse result, string? field)
    {
        if (string.IsNullOrWhiteSpace(field) || result.Metrics is null) return null;
        if (!result.Metrics.TryGetValue(field, out var v) || v is null) return null;
        return ToDouble(v);
    }

    private static double? ToDouble(object? v)
    {
        switch (v)
        {
            case null: return null;
            case double d: return d;
            case int i: return i;
            case long l: return l;
            case decimal m: return (double)m;
            case JsonElement je:
                if (je.ValueKind == JsonValueKind.Number) return je.GetDouble();
                if (je.ValueKind == JsonValueKind.String && double.TryParse(je.GetString(), out var pd)) return pd;
                return null;
            default:
                return double.TryParse(v.ToString(), out var d2) ? d2 : null;
        }
    }

    private static int ToInt(object? v) => (int)(ToDouble(v) ?? 0);

    // ---------------------------------------------------------------- 追踪留痕

    public void Trace(EplanResponse r, Dictionary<string, string> parameters, TimeSpan elapsed)
    {
        try
        {
            var line = new
            {
                ts = DateTime.Now.ToString("O"),
                traceId = r.TraceId,
                action = r.Action,
                status = r.Status,
                success = r.Success,
                elapsedMs = (long)elapsed.TotalMilliseconds,
                message = Trim(r.Message, 300),
                error = Trim(r.Error, 300),
                inputParams = parameters,
                metrics = r.Metrics,
                evidence = r.Evidence is null ? null : new
                {
                    filesChanged = r.Evidence.TryGetValue("filesChanged", out var fc) ? fc : null,
                    files = r.Evidence.TryGetValue("files", out var fl) ? fl : null
                }
            };
            var json = JsonSerializer.Serialize(line, JsonOpts);
            lock (_lock) File.AppendAllText(_tracePath, json + Environment.NewLine, Encoding.UTF8);
        }
        catch { /* 留痕失败不能影响主流程 */ }
    }

    private static string? Trim(string? s, int max) =>
        s is null || s.Length <= max ? s : s[..max] + "…";

    /// <summary>读取最近 N 条追踪记录（新→旧）。</summary>
    public List<JsonElement> ReadTrace(int limit)
    {
        var outList = new List<JsonElement>();
        try
        {
            if (!File.Exists(_tracePath)) return outList;
            string[] lines;
            lock (_lock)
            {
                using var fs = new FileStream(_tracePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                lines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
            for (int i = lines.Length - 1; i >= 0 && outList.Count < limit; i--)
            {
                try { outList.Add(JsonDocument.Parse(lines[i]).RootElement.Clone()); }
                catch { }
            }
        }
        catch { }
        return outList;
    }
}
