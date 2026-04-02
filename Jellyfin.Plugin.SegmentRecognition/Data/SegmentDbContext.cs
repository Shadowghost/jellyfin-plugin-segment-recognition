using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.SegmentRecognition.Data;

/// <summary>
/// Database context for segment recognition data.
/// </summary>
public class SegmentDbContext : DbContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentDbContext"/> class.
    /// </summary>
    /// <param name="options">The database context options.</param>
    public SegmentDbContext(DbContextOptions<SegmentDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Gets or sets the analysis status records.
    /// </summary>
    public DbSet<AnalysisStatus> AnalysisStatuses { get; set; } = null!;

    /// <summary>
    /// Gets or sets the black frame results.
    /// </summary>
    public DbSet<BlackFrameResult> BlackFrameResults { get; set; } = null!;

    /// <summary>
    /// Gets or sets the chromaprint results.
    /// </summary>
    public DbSet<ChromaprintResult> ChromaprintResults { get; set; } = null!;

    /// <summary>
    /// Gets or sets the chapter analysis results.
    /// </summary>
    public DbSet<ChapterAnalysisResult> ChapterAnalysisResults { get; set; } = null!;

    /// <summary>
    /// Gets or sets the crop detection results.
    /// </summary>
    public DbSet<CropDetectResult> CropDetectResults { get; set; } = null!;

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AnalysisStatus>(entity =>
        {
            entity.HasKey(e => new { e.ItemId, e.ProviderName });
            entity.HasIndex(e => e.ItemId);
        });

        modelBuilder.Entity<BlackFrameResult>(entity =>
        {
            entity.HasKey(e => new { e.ItemId, e.TimestampTicks });
            entity.HasIndex(e => e.ItemId);
        });

        modelBuilder.Entity<ChromaprintResult>(entity =>
        {
            entity.HasKey(e => new { e.ItemId, e.Region });
            entity.HasIndex(e => e.SeasonId);
        });

        modelBuilder.Entity<ChapterAnalysisResult>(entity =>
        {
            entity.HasKey(e => new { e.ItemId, e.SegmentType, e.MatchedChapterName });
            entity.HasIndex(e => e.ItemId);
        });

        modelBuilder.Entity<CropDetectResult>(entity =>
        {
            entity.HasKey(e => e.ItemId);
        });
    }
}
