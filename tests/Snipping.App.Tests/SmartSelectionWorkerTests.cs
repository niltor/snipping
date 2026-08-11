using System.Collections.Concurrent;

namespace Snipping.App.Tests;

public sealed class SmartSelectionWorkerTests
{
    [Fact]
    public void WorkerCoalescesRapidInputAndNeverRunsQueriesConcurrently()
    {
        var detector = new FakeDetector
        {
            DelayMilliseconds = 20
        };
        var results = new ConcurrentQueue<Point>();
        using var completed = new ManualResetEventSlim();
        using var worker = new SmartSelectionWorker(detector, result =>
        {
            if (result.Candidate is not null)
                results.Enqueue(new Point(result.Candidate.ScreenBounds.Left, result.Candidate.ScreenBounds.Top));
            completed.Set();
        });

        for (var x = 0; x < 600; x += 12)
            worker.Submit(new Point(x, 20), new IntPtr(1));

        Assert.True(completed.Wait(TimeSpan.FromSeconds(3)));
        Thread.Sleep(100);

        Assert.InRange(detector.Calls, 1, 2);
        Assert.Equal(1, detector.MaxConcurrentCalls);
        Assert.Equal(588, results.Last().X);

        var metrics = worker.Diagnostics.Snapshot();
        Assert.True(metrics.RequestsCoalesced > 0);
        Assert.InRange(metrics.RequestsSubmitted, 1, 50);
    }

    [Fact]
    public void WorkerAllowsSameGridToRefreshAfterShortCacheWindow()
    {
        var detector = new FakeDetector();
        using var first = new ManualResetEventSlim();
        using var second = new ManualResetEventSlim();
        using var worker = new SmartSelectionWorker(detector, _ =>
        {
            if (detector.Calls == 1)
                first.Set();
            else
                second.Set();
        });

        worker.Submit(new Point(12, 20), new IntPtr(1));
        Assert.True(first.Wait(TimeSpan.FromSeconds(3)));

        Thread.Sleep(550);
        worker.Submit(new Point(13, 20), new IntPtr(1));

        Assert.True(second.Wait(TimeSpan.FromSeconds(3)));
        Assert.Equal(2, detector.Calls);
    }

    [Fact]
    public void WorkerPublishesFastCandidateThenRefinesBroadHostAfterPointerSettles()
    {
        var detector = new FakeDetector
        {
            FastCandidate = new SmartSelectionCandidate(
                new Rectangle(0, 0, 900, 700),
                "host",
                NeedsRefinement: true,
                IsContainer: true),
            PreciseCandidate = new SmartSelectionCandidate(
                new Rectangle(120, 80, 240, 160),
                "button")
        };
        var results = new ConcurrentQueue<SmartSelectionResult>();
        using var refined = new ManualResetEventSlim();
        using var worker = new SmartSelectionWorker(detector, result =>
        {
            results.Enqueue(result);
            if (result.IsRefinement)
                refined.Set();
        });

        worker.Submit(new Point(180, 120), new IntPtr(1));

        Assert.True(refined.Wait(TimeSpan.FromSeconds(3)));
        Assert.True(results.TryDequeue(out var fastResult));
        Assert.True(results.TryDequeue(out var preciseResult));
        Assert.False(fastResult.IsRefinement);
        Assert.True(fastResult.Candidate!.NeedsRefinement);
        Assert.True(preciseResult.IsRefinement);
        Assert.Equal(new Rectangle(120, 80, 240, 160), preciseResult.Candidate!.ScreenBounds);
        Assert.Equal(1, detector.Calls);
        Assert.Equal(1, detector.PreciseCalls);
        Assert.Equal(1, detector.MaxConcurrentCalls);
    }

    private sealed class FakeDetector : ISmartSelectionDetector
    {
        private int _activeCalls;
        private int _maxConcurrentCalls;
        private int _calls;
        private int _preciseCalls;

        public SmartSelectionDiagnostics Diagnostics { get; } = new();
        public int DelayMilliseconds { get; init; }
        public SmartSelectionCandidate? FastCandidate { get; init; }
        public SmartSelectionCandidate? PreciseCandidate { get; init; }
        public int Calls => Volatile.Read(ref _calls);
        public int PreciseCalls => Volatile.Read(ref _preciseCalls);
        public int MaxConcurrentCalls => _maxConcurrentCalls;

        public SmartSelectionCandidate? Detect(
            Point screenPoint,
            IntPtr excludedWindow,
            CancellationToken cancellationToken = default)
            => Execute(screenPoint, precise: false);

        public SmartSelectionCandidate? DetectPrecise(
            Point screenPoint,
            IntPtr excludedWindow,
            CancellationToken cancellationToken = default)
            => Execute(screenPoint, precise: true);

        private SmartSelectionCandidate Execute(Point screenPoint, bool precise)
        {
            var active = Interlocked.Increment(ref _activeCalls);
            InterlockedExtensions.Max(ref _maxConcurrentCalls, active);
            if (precise)
                Interlocked.Increment(ref _preciseCalls);
            else
                Interlocked.Increment(ref _calls);
            Diagnostics.QueryStarted();
            try
            {
                Thread.Sleep(DelayMilliseconds);
                return precise
                    ? PreciseCandidate ?? CreateDefaultCandidate(screenPoint)
                    : FastCandidate ?? CreateDefaultCandidate(screenPoint);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
                Diagnostics.QueryCompleted(DelayMilliseconds);
            }
        }

        private static SmartSelectionCandidate CreateDefaultCandidate(Point screenPoint) =>
            new(
                new Rectangle(screenPoint.X, screenPoint.Y, 12, 12),
                "fake");
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref location);
                if (current >= value)
                    return;
                if (Interlocked.CompareExchange(ref location, value, current) == current)
                    return;
            }
        }
    }
}
