using EplanApi.Services;

var builder = WebApplication.CreateBuilder(args);

// 监听地址：优先读配置 Eplan:ListenUrl，可被环境变量 Eplan__ListenUrl 覆盖；默认 5000
var listenUrl = builder.Configuration["Eplan:ListenUrl"] ?? "http://localhost:5000";
builder.WebHost.UseUrls(listenUrl);

// 根据配置选择底层引擎：Script（默认，进程外脚本） / Api（进程内 EPLAN API）
var engineType = builder.Configuration["Eplan:Engine"] ?? "Script";

builder.Services.AddControllers();

// 能力注册表 + 证据层（P0）：必须在 EplanService 之前注册
builder.Services.AddSingleton<CapabilityRegistry>();
builder.Services.AddSingleton<EvidenceService>();
builder.Services.AddSingleton<EplanService>();

if (engineType.Equals("Api", StringComparison.OrdinalIgnoreCase))
{
#if EPLAN_API
    builder.Services.AddSingleton<IEplanEngine, EplanApiEngine>();
#else
    throw new InvalidOperationException(
        "Engine=Api 需要进程内 EPLAN API。请用命令：dotnet run /p:EPLAN_API=true 运行，或把 Eplan:Engine 改回 Script。");
#endif
}
else
    builder.Services.AddSingleton<IEplanEngine, ScriptEngine>();

var app = builder.Build();

app.MapControllers();

app.Run();
