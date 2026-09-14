using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using SkyEyePMS.Api.Auth;
using SkyEyePMS.Api.Endpoints;
using SkyEyePMS.Infrastructure;
using SkyEyePMS.Infrastructure.Database;

var builder = WebApplication.CreateBuilder(args);

// 环境变量按 .NET 标准约定覆盖配置（形如 Database__ConnectionString、Jwt__SecretKey）

// ---------- 服务 ----------
builder.Services.AddSkyEyePmsInfrastructure(builder.Configuration);

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
builder.Services.AddSingleton<TokenService>();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("admin"));
});

var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwtOptions.SecretKey))
{
    Console.WriteLine("[SkyEyePMS] 警告：未配置 Jwt:SecretKey，已使用随机密钥（重启后需重新登录）。生产环境请在配置中设置。");
}
var resolvedSecret = string.IsNullOrWhiteSpace(jwtOptions.SecretKey)
    ? Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48))
    : jwtOptions.SecretKey;

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(resolvedSecret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

// 公网部署防护：登录接口按 IP 限流 + 全局 API 限流
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsync("""{"message":"请求过于频繁，请稍后再试"}""", ct);
    };
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue<int?>("Security:LoginRateLimit") ?? 10,
            Window = TimeSpan.FromMinutes(1)
        }));
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue<int?>("Security:GlobalRateLimit") ?? 600,
                Window = TimeSpan.FromMinutes(1)
            }));
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

// OpenAPI 文档（/openapi/v1.json，供 Apifox/Postman/代码生成使用）
builder.Services.AddOpenApi();
// 看板 overview 短缓存（多客户端自动刷新不放大 DB 压力）
builder.Services.AddMemoryCache();
// 周期报表推送（每周摘要，计划在系统设置中配置）
builder.Services.AddHostedService<SkyEyePMS.Api.Services.ReportPushService>();

var app = builder.Build();

// ---------- 启动初始化：建库迁移 + 种子管理员 ----------
using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<DbInitializer>();
    await initializer.InitializeAsync();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await AuthSeeder.SeedAsync(scope.ServiceProvider, app.Configuration, logger);
}

app.UseDefaultFiles();   // 若 wwwroot 存在前端产物则单容器部署
app.UseStaticFiles();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapPrinterEndpoints();
app.MapAlertEndpoints();
app.MapSettingsEndpoints();
app.MapReportEndpoints();
app.MapDiscoveryEndpoints();
app.MapUserEndpoints();
app.MapSystemEndpoints(app.Configuration);
app.MapAuditEndpoints();

app.MapOpenApi();

app.MapFallbackToFile("index.html");

app.Run();

// 供 WebApplicationFactory 集成测试使用
public partial class Program;
