namespace Snipping.App;

internal sealed record SmartSelectionMetrics(
    long RequestsSubmitted,
    long RequestsCoalesced,
    long QueriesStarted,
    long QueriesCompleted,
    double TotalQueryMilliseconds,
    double TotalUiaMilliseconds,
    long CacheHits,
    long TreeScans,
    long NativeSnapshotBuilds,
    long NativeCandidateHits,
    long AutomationSnapshotHits,
    long AutomationSnapshotBuilds,
    long VisualScans,
    long VisualResults,
    long LastCandidateCount,
    long FallbackResults,
    long StaleResults,
    long Exceptions,
    double LastQueryMilliseconds);

internal sealed class SmartSelectionDiagnostics
{
    private long _requestsSubmitted;
    private long _requestsCoalesced;
    private long _queriesStarted;
    private long _queriesCompleted;
    private long _totalQueryMicroseconds;
    private long _totalUiaMicroseconds;
    private long _cacheHits;
    private long _treeScans;
    private long _nativeSnapshotBuilds;
    private long _nativeCandidateHits;
    private long _automationSnapshotHits;
    private long _automationSnapshotBuilds;
    private long _visualScans;
    private long _visualResults;
    private long _lastCandidateCount;
    private long _fallbackResults;
    private long _staleResults;
    private long _exceptions;
    private double _lastQueryMilliseconds;

    internal void RequestSubmitted() => Interlocked.Increment(ref _requestsSubmitted);
    internal void RequestCoalesced() => Interlocked.Increment(ref _requestsCoalesced);
    internal void QueryStarted() => Interlocked.Increment(ref _queriesStarted);
    internal void QueryCompleted(double milliseconds, double uiaMilliseconds = 0)
    {
        Interlocked.Increment(ref _queriesCompleted);
        Interlocked.Add(ref _totalQueryMicroseconds, ToMicroseconds(milliseconds));
        Interlocked.Add(ref _totalUiaMicroseconds, ToMicroseconds(uiaMilliseconds));
        Interlocked.Exchange(ref _lastQueryMilliseconds, milliseconds);
    }
    internal void CacheHit() => Interlocked.Increment(ref _cacheHits);
    internal void TreeScan() => Interlocked.Increment(ref _treeScans);
    internal void NativeSnapshotBuilt(int _, int __) => Interlocked.Increment(ref _nativeSnapshotBuilds);
    internal void NativeCandidateHit() => Interlocked.Increment(ref _nativeCandidateHits);
    internal void AutomationSnapshotHit() => Interlocked.Increment(ref _automationSnapshotHits);
    internal void AutomationSnapshotBuilt(int _) => Interlocked.Increment(ref _automationSnapshotBuilds);
    internal void VisualScan() => Interlocked.Increment(ref _visualScans);
    internal void VisualResult() => Interlocked.Increment(ref _visualResults);
    internal void CandidateCount(int count) => Interlocked.Exchange(ref _lastCandidateCount, count);
    internal void FallbackResult() => Interlocked.Increment(ref _fallbackResults);
    internal void StaleResult() => Interlocked.Increment(ref _staleResults);
    internal void Exception() => Interlocked.Increment(ref _exceptions);

    internal SmartSelectionMetrics Snapshot() => new(
        Interlocked.Read(ref _requestsSubmitted),
        Interlocked.Read(ref _requestsCoalesced),
        Interlocked.Read(ref _queriesStarted),
        Interlocked.Read(ref _queriesCompleted),
        Interlocked.Read(ref _totalQueryMicroseconds) / 1000d,
        Interlocked.Read(ref _totalUiaMicroseconds) / 1000d,
        Interlocked.Read(ref _cacheHits),
        Interlocked.Read(ref _treeScans),
        Interlocked.Read(ref _nativeSnapshotBuilds),
        Interlocked.Read(ref _nativeCandidateHits),
        Interlocked.Read(ref _automationSnapshotHits),
        Interlocked.Read(ref _automationSnapshotBuilds),
        Interlocked.Read(ref _visualScans),
        Interlocked.Read(ref _visualResults),
        Interlocked.Read(ref _lastCandidateCount),
        Interlocked.Read(ref _fallbackResults),
        Interlocked.Read(ref _staleResults),
        Interlocked.Read(ref _exceptions),
        Interlocked.CompareExchange(ref _lastQueryMilliseconds, 0, 0));

    private static long ToMicroseconds(double milliseconds) =>
        Math.Max(0, (long)Math.Round(milliseconds * 1000));
}
