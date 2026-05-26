namespace YtAudio.Api.Models
{
    public class DownloadTask
    {
        public Guid Id { get; set; }
        public string YoutubeUrl { get; set; } = default!;
        public DownloadStatus Status { get; set; } = DownloadStatus.Pending;
        public string? ErrorMessage { get; set; }
        public Guid? TrackId { get; set; }
        public Track? Track { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
    }

    public enum DownloadStatus
    {
        Pending = 0,
        Processing = 1,
        Completed = 2,
        Failed = 3
    }
}
