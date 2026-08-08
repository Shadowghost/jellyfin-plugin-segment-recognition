using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SegmentRecognition.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.SegmentRecognition.Data;

/// <summary>
/// Writes <see cref="AnalysisStatus"/> rows idempotently.
/// </summary>
internal static class AnalysisStatusWriter
{
    /// <summary>
    /// Inserts or updates the status row for one (item, provider) pair. The caller is
    /// responsible for calling <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>.
    /// </summary>
    /// <param name="db">The database context.</param>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="providerName">The provider name.</param>
    /// <param name="hasResults">Whether this provider stored any segments for the item.</param>
    /// <param name="configHash">Hash of the configuration that produced the result, for staleness detection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public static async Task UpsertAsync(
        SegmentDbContext db,
        Guid itemId,
        string providerName,
        bool hasResults,
        string? configHash,
        CancellationToken cancellationToken)
    {
        var existing = await db.AnalysisStatuses
            .FirstOrDefaultAsync(s => s.ItemId == itemId && s.ProviderName == providerName, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.AnalysisStatuses.Add(new AnalysisStatus
            {
                ItemId = itemId,
                ProviderName = providerName,
                AnalyzedAt = DateTime.UtcNow,
                HasResults = hasResults,
                ConfigHash = configHash,
            });
            return;
        }

        existing.AnalyzedAt = DateTime.UtcNow;
        existing.HasResults = hasResults;
        existing.ConfigHash = configHash;
    }
}
