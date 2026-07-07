using Microsoft.EntityFrameworkCore;
using YtAudio.Api.Models;

namespace YtAudio.Api.Data
{
    public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
    {
        public DbSet<Track> Tracks => Set<Track>();
        public DbSet<DownloadTask> DownloadTasks => Set<DownloadTask>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Track>(e =>
            {
                e.HasKey(t => t.Id);
                e.Property(t => t.YoutubeId).HasMaxLength(20).IsRequired();
                e.HasIndex(t => t.YoutubeId).IsUnique();
                e.Property(t => t.Title).HasMaxLength(500).IsRequired();
                e.Property(t => t.Artist).HasMaxLength(300);
                e.Property(t => t.Album).HasMaxLength(300);
                e.Property(t => t.FilePath).HasMaxLength(1000).IsRequired();
                e.Property(t => t.FileExtension).HasMaxLength(10).IsRequired();
            });

            modelBuilder.Entity<DownloadTask>(e =>
            {
                e.HasKey(t => t.Id);
                e.Property(t => t.YoutubeUrl).HasMaxLength(2000).IsRequired();
                e.Property(t => t.Status).HasConversion<int>();
                e.HasOne(t => t.Track)
                 .WithMany()
                 .HasForeignKey(t => t.TrackId)
                 .OnDelete(DeleteBehavior.SetNull);
            });
        }
    }
}
