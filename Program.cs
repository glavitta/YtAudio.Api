using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using YtAudio.Api.Data;
using YtAudio.Api.Endpoints;
using YtAudio.Api.Jobs;
using YtAudio.Api.Middleware;
using YtAudio.Api.Services;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is missing.");

builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseNpgsql(connectionString)
        .UseSnakeCaseNamingConvention()); 

builder.Services.AddHangfire(cfg => cfg
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connectionString)));

builder.Services.AddHangfireServer(opts =>
{
    opts.WorkerCount = builder.Configuration.GetValue("Hangfire:WorkerCount", 2);
    opts.Queues = ["default"];
    opts.ServerName = $"ytaudio-{Environment.MachineName}";
});

builder.Services.AddScoped<YtDlpService>();
builder.Services.AddScoped<DownloadAudioJob>();
builder.Services.AddScoped<TelegramDownloadAndSendJob>();
builder.Services.AddSingleton<FileStorageService>();

builder.Services.AddSingleton<YouTubeSearchService>();
builder.Services.AddHostedService<TelegramBotService>();

builder.Services.AddOpenApi();

builder.Services.AddCors(opts => opts.AddDefaultPolicy(policy =>
    policy.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
          .AllowAnyHeader()
          .AllowAnyMethod()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

app.UseMiddleware<ApiKeyMiddleware>();

app.UseCors();

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = [new HangfireLocalFilter()],
    DashboardTitle = "YtAudio Jobs"
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(opts =>
    {
        opts.Title = "YtAudio API";
        opts.Theme = ScalarTheme.DeepSpace;
    });
}

app.MapDownloads();
app.MapTracks();

app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }))
   .WithTags("System");

app.Run();