using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Jellyfin.Plugin.SegmentRecognition.ScheduledTasks;

/// <summary>
/// Thread-safe counters for tracking task progress. All increments use
/// <see cref="Interlocked"/> to support parallel item/group processing.
/// </summary>
internal sealed class TaskStats
{
    private int _chapterAnalyzed;
    private int _blackFrameAnalyzed;
    private int _analysisSkipped;
    private int _analysisFailed;
    private int _fingerprintsGenerated;
    private int _seasonsAnalyzed;
    private int _pushed;
    private int _pushSkipped;

    /// <summary>
    /// Gets the set of item IDs that were already pushed during this task run.
    /// Used by <see cref="AnalyzeSegmentsTask"/> to avoid double-pushing.
    /// </summary>
    public ConcurrentDictionary<Guid, byte> PushedItemIds { get; } = new();

    /// <summary>Gets the number of items that had chapter name analysis performed.</summary>
    public int ChapterAnalyzed => _chapterAnalyzed;

    /// <summary>Gets the number of items that had black frame analysis performed.</summary>
    public int BlackFrameAnalyzed => _blackFrameAnalyzed;

    /// <summary>Gets the number of items skipped because all analysis was already up-to-date.</summary>
    public int AnalysisSkipped => _analysisSkipped;

    /// <summary>Gets the number of analysis operations that failed with an exception.</summary>
    public int AnalysisFailed => _analysisFailed;

    /// <summary>Gets the number of chromaprint fingerprints generated (intro + credits counted separately).</summary>
    public int FingerprintsGenerated => _fingerprintsGenerated;

    /// <summary>Gets the number of groups (seasons) that had chromaprint comparison performed.</summary>
    public int SeasonsAnalyzed => _seasonsAnalyzed;

    /// <summary>Gets the number of items whose segments were pushed to Jellyfin.</summary>
    public int Pushed => _pushed;

    /// <summary>Gets the number of items skipped during push because they had no results.</summary>
    public int PushSkipped => _pushSkipped;

    /// <summary>Gets the total number of analysis operations performed (chapter + black frame + fingerprint).</summary>
    public int TotalWork => _chapterAnalyzed + _blackFrameAnalyzed + _fingerprintsGenerated;

    /// <summary>Increments the chapter analysis counter.</summary>
    public void IncrementChapterAnalyzed() => Interlocked.Increment(ref _chapterAnalyzed);

    /// <summary>Increments the black frame analysis counter.</summary>
    public void IncrementBlackFrameAnalyzed() => Interlocked.Increment(ref _blackFrameAnalyzed);

    /// <summary>Increments the analysis-skipped counter.</summary>
    public void IncrementAnalysisSkipped() => Interlocked.Increment(ref _analysisSkipped);

    /// <summary>Increments the analysis-failed counter.</summary>
    public void IncrementAnalysisFailed() => Interlocked.Increment(ref _analysisFailed);

    /// <summary>Increments the fingerprints-generated counter.</summary>
    public void IncrementFingerprintsGenerated() => Interlocked.Increment(ref _fingerprintsGenerated);

    /// <summary>Increments the seasons-analyzed counter.</summary>
    public void IncrementSeasonsAnalyzed() => Interlocked.Increment(ref _seasonsAnalyzed);

    /// <summary>Increments the pushed counter.</summary>
    public void IncrementPushed() => Interlocked.Increment(ref _pushed);

    /// <summary>Increments the push-skipped counter.</summary>
    public void IncrementPushSkipped() => Interlocked.Increment(ref _pushSkipped);
}
