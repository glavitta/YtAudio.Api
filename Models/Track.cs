namespace YtAudio.Api.Models
{
    public class Track
    {
        public Guid Id { get; set; }
        public string YoutubeId { get; set; } = default!;
        public string Title { get; set; } = default!;
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? ThumbnailUrl { get; set; }
        public long DurationSeconds { get; set; }
        public string FilePath { get; set; } = default!;
        public string FileExtension { get; set; } = default!;
        public long FileSizeBytes { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    }
}
