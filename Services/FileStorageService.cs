namespace YtAudio.Api.Services
{
    public class FileStorageService
    {
        private readonly string _storageRoot;
        private readonly string _tempRoot;
        private readonly ILogger<FileStorageService> _logger;

        public FileStorageService(IConfiguration config, ILogger<FileStorageService> logger)
        {
            _logger = logger;
            _storageRoot = config["Storage:Root"]
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "YtAudio", "tracks");

            _tempRoot = Path.Combine(_storageRoot, ".tmp");

            Directory.CreateDirectory(_storageRoot);
            Directory.CreateDirectory(_tempRoot);

            logger.LogInformation("Storage root: {Root}", _storageRoot);

        }

        public string CreateTempDirectory()
        {
            var dir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        public string MoveToStorage(string tempFilePath, string youtubeId)
        {
            var ext = Path.GetExtension(tempFilePath);
            var destination = Path.Combine(_storageRoot, $"{youtubeId}{ext}");

            File.Move(tempFilePath, destination, overwrite: true);
            _logger.LogInformation("Stored track {Id} → {Path}", youtubeId, destination);

            var tempDir = Path.GetDirectoryName(tempFilePath);
            if (tempDir is not null && tempDir != _tempRoot && Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not clean temp dir {Dir}", tempDir); }
            }

            return destination;
        }

        public bool Exists(string filePath) => File.Exists(filePath);

        public void Delete(string filePath)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogInformation("Deleted file {Path}", filePath);
            }
        }

        public StorageStats GetStats()
        {
            var files = Directory.GetFiles(_storageRoot, "*", SearchOption.TopDirectoryOnly);
            return new StorageStats
            {
                TrackCount = files.Length,
                TotalBytes = files.Sum(f => new FileInfo(f).Length)
            };
        }
    }

    public record StorageStats
    {
        public int TrackCount { get; init; }
        public long TotalBytes { get; init; }
        public string TotalHuman => TotalBytes switch
        {
            < 1024 => $"{TotalBytes} B",
            < 1024 * 1024 => $"{TotalBytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{TotalBytes / (1024.0 * 1024):F1} MB",
            _ => $"{TotalBytes / (1024.0 * 1024 * 1024):F2} GB"
        };
    }
}
