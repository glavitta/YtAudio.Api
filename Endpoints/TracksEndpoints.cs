using Microsoft.EntityFrameworkCore;
using YtAudio.Api.Data;
using YtAudio.Api.Services;


namespace YtAudio.Api.Endpoints
{
    public static class TracksEndpoints
    {
        public static IEndpointRouteBuilder MapTracks(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/api/tracks").WithTags("Tracks");

            group.MapGet("/", async (AppDbContext db, string? search, string? sortBy) =>
            {
                var query = db.Tracks.AsQueryable();

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var term = search.Trim().ToLower();
                    query = query.Where(t =>
                        t.Title.ToLower().Contains(term) ||
                        (t.Artist != null && t.Artist.ToLower().Contains(term)));
                }

                query = sortBy switch
                {
                    "artist" => query.OrderBy(t => t.Artist).ThenBy(t => t.Title),
                    "date" => query.OrderByDescending(t => t.CreatedAt),
                    "size" => query.OrderByDescending(t => t.FileSizeBytes),
                    _ => query.OrderBy(t => t.Title)
                };

                var tracks = await query
                    .Select(t => new TrackDto(
                        t.Id, t.YoutubeId, t.Title, t.Artist, t.ThumbnailUrl,
                        t.DurationSeconds, t.FileExtension, t.FileSizeBytes, t.CreatedAt))
                    .ToListAsync();

                return Results.Ok(tracks);
            });

            group.MapGet("/{id:guid}", async (Guid id, AppDbContext db) =>
            {
                var t = await db.Tracks.FindAsync(id);
                return t is null
                    ? Results.NotFound()
                    : Results.Ok(new TrackDto(
                        t.Id, t.YoutubeId, t.Title, t.Artist, t.ThumbnailUrl,
                        t.DurationSeconds, t.FileExtension, t.FileSizeBytes, t.CreatedAt));
            });

            group.MapGet("/{id:guid}/stream", async (Guid id, AppDbContext db) =>
            {
                var track = await db.Tracks.FindAsync(id);
                if (track is null) return Results.NotFound();
                if (!File.Exists(track.FilePath))
                    return Results.Problem(
                        detail: "Audio file is missing from storage. Try re-downloading.",
                        statusCode: StatusCodes.Status500InternalServerError);

                var contentType = track.FileExtension switch
                {
                    "opus" => "audio/ogg; codecs=opus",
                    "m4a" => "audio/mp4",
                    "mp3" => "audio/mpeg",
                    "webm" => "audio/webm",
                    _ => "application/octet-stream"
                };

                var absolutePath = Path.GetFullPath(track.FilePath);
                return Results.File(absolutePath, contentType, enableRangeProcessing: true);
            });

            group.MapGet("/{id:guid}/download", async (Guid id, AppDbContext db, HttpContext ctx) =>
            {
                var track = await db.Tracks.FindAsync(id);
                if (track is null) return Results.NotFound();
                if (!File.Exists(Path.GetFullPath(track.FilePath)))
                    return Results.Problem("Audio file missing from storage.", statusCode: 500);

                var absolutePath = Path.GetFullPath(track.FilePath);
                var fileName = $"{track.Title}.{track.FileExtension}"
                    .Replace("/", "_").Replace("\\", "_");

                var encodedName = Uri.EscapeDataString(fileName);
                ctx.Response.Headers.Append(
                    "Content-Disposition",
                    $"attachment; filename*=UTF-8''{encodedName}");

                return Results.File(absolutePath, "application/octet-stream");
            });

            group.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, FileStorageService storage) =>
            {
                var track = await db.Tracks.FindAsync(id);
                if (track is null) return Results.NotFound();

                storage.Delete(track.FilePath);
                db.Tracks.Remove(track);
                await db.SaveChangesAsync();

                return Results.NoContent();
            });

            group.MapGet("/stats", (FileStorageService storage) => Results.Ok(storage.GetStats()))
                 .WithName("GetStats");

            return app;
        }
    }

    public record TrackDto(
        Guid Id,
        string YoutubeId,
        string Title,
        string? Artist,
        string? ThumbnailUrl,
        long DurationSeconds,
        string FileExtension,
        long FileSizeBytes,
        DateTime CreatedAt);

}