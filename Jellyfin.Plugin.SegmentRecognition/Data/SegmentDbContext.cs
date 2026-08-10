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

            // The analyzed-items listing groups by ContainerId on every request; unindexed that is
            // a full table scan into a temporary B-tree. Indexing ContainerId alone gets the
            // grouping order but still costs one rowid lookup per row into the main table, whose
            // pages are interleaved with the fingerprint blobs - on a real database that is ~165k
            // random reads across ~2 GB for every request. Carrying the four aggregated columns
            // lets SQLite answer from the index alone (SCAN USING COVERING INDEX).
            //
            // This replaces a plain ContainerId index, which it subsumes as its leftmost prefix.
            // Keeping both only cost write amplification on the analysis path.
            entity.HasIndex(e => new { e.ContainerId, e.HasResults, e.AnalyzedAt, e.ProviderName, e.LastError })
                .HasDatabaseName("IX_AnalysisStatuses_ContainerRollup");
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
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => e.ItemId);
            entity.HasIndex(e => new { e.ItemId, e.SegmentType, e.MatchedChapterName, e.StartTicks })
                .IsUnique();
        });

        modelBuilder.Entity<CropDetectResult>(entity =>
        {
            entity.HasKey(e => e.ItemId);
        });
    }
}
