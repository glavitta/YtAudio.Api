using Hangfire;
using Microsoft.EntityFrameworkCore;
using YtAudio.Api.Data;
using YtAudio.Api.Jobs;
using YtAudio.Api.Models;

namespace YtAudio.Api.Endpoints
{
    public static class DownloadsEndpoints
    {
        public static IEndpointRouteBuilder MapDownloads(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/api/downloads").WithTags("Downloads");

            group.MapPost("/", async (SubmitDownloadRequest req, AppDbContext db, IBackgroundJobClient jobs) =>
            {
                if (string.IsNullOrWhiteSpace(req.Url))
                    return Results.BadRequest(new { error = "url is required." });

                if (!IsYouTubeUrl(req.Url))
                    return Results.BadRequest(new { error = "Only YouTube URLs are supported." });

                var task = new DownloadTask
                {
                    Id = Guid.NewGuid(),
                    YoutubeUrl = req.Url.Trim(),
                    CreatedAt = DateTime.UtcNow
                };

                db.DownloadTasks.Add(task);
                await db.SaveChangesAsync();

                jobs.Enqueue<DownloadAudioJob>(j => j.ExecuteAsync(task.Id, CancellationToken.None));

                return Results.Accepted(
                    $"/api/downloads/{task.Id}",
                    new DownloadTaskResponse(task));
            });

            group.MapGet("/{id:guid}", async (Guid id, AppDbContext db) =>
            {
                var task = await db.DownloadTasks
                    .Include(t => t.Track)
                    .FirstOrDefaultAsync(t => t.Id == id);

                return task is null
                    ? Results.NotFound()
                    : Results.Ok(new DownloadTaskResponse(task));
            });

            group.MapGet("/", async (AppDbContext db, int limit = 50) =>
            {
                var tasks = await db.DownloadTasks
                    .Include(t => t.Track)
                    .OrderByDescending(t => t.CreatedAt)
                    .Take(Math.Min(limit, 200))
                    .Select(t => new DownloadTaskResponse(t))
                    .ToListAsync();

                return Results.Ok(tasks);
            });

            return app;
        }
        private static bool IsYouTubeUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Host.Contains("youtube.com") || uri.Host.Contains("youtu.be"));
    }

    public record SubmitDownloadRequest(string Url);

    public record DownloadTaskResponse
    {

        public Guid Id { get; init; }
        public string YoutubeUrl { get; init; }
        public string Status { get; init; }
        public string? ErrorMessage { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
        public TrackSummary? Track { get; init; }

        public DownloadTaskResponse(DownloadTask t)
        {
            Id = t.Id;
            YoutubeUrl = t.YoutubeUrl;
            Status = t.Status.ToString();
            ErrorMessage = t.ErrorMessage;
            CreatedAt = t.CreatedAt;
            CompletedAt = t.CompletedAt;
            Track = t.Track is null ? null : new TrackSummary(t.Track.Id, t.Track.Title, t.Track.Artist);
        }
    }

    public record TrackSummary(Guid Id, string Title, string? Artist);
}