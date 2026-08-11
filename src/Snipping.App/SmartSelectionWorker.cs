using System.Diagnostics;

namespace Snipping.App;

/// <summary>
/// Coalesces high-frequency mouse input into one debounced, single-flight
/// snapshot refresh. UIA/visual work runs on one dedicated background thread;
/// the UI thread only paints already-published rectangles.
/// </summary>
internal sealed class SmartSelectionWorker : IDisposable
{
    private const int GridSize = 6;
    private const int DebounceMilliseconds = 30;
    private const int MaximumWaitMilliseconds = 90;
    private const int RefinementDelayMilliseconds = 55;
    // Native snapshot hit-testing is cheap, so keep this short. A long
    // same-grid cache can hide a window/control boundary that is only a few
    // pixels wide when the pointer crosses it.
    private const int RequestDedupeMilliseconds = 100;

    private readonly object _gate = new();
    private readonly ISmartSelectionDetector _detector;
    private readonly Action<SmartSelectionResult> _resultCallback;
    private readonly SmartSelectionDiagnostics _diagnostics;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Thread _thread;
    private PendingRequest? _pending;
    private Point? _lastGrid;
    private long _lastGridTimestamp;
    private long _version;
    private bool _disposed;

    public SmartSelectionWorker(
        ISmartSelectionDetector detector,
        Action<SmartSelectionResult> resultCallback)
    {
        _detector = detector;
        _resultCallback = resultCallback;
        _diagnostics = detector.Diagnostics;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Snipping Smart Selection"
        };
        // UI Automation providers are isolated from the WinForms UI thread.
        // MTA also prevents provider callbacks from blocking the overlay's
        // message pump while a root snapshot is being built.
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public SmartSelectionDiagnostics Diagnostics => _diagnostics;

    public void Submit(Point screenPoint, IntPtr excludedWindow)
    {
        var grid = new Point(
            (int)Math.Floor(screenPoint.X / (double)GridSize),
            (int)Math.Floor(screenPoint.Y / (double)GridSize));

        lock (_gate)
        {
            var sameGridWithinRefreshWindow =
                _pending is null
                && _lastGrid == grid
                && Stopwatch.GetTimestamp() - _lastGridTimestamp
                    < RequestDedupeMilliseconds * Stopwatch.Frequency / 1000;
            if (_disposed || sameGridWithinRefreshWindow)
            {
                _diagnostics.RequestCoalesced();
                return;
            }

            if (_pending is not null && _pending.Grid == grid)
            {
                _pending = _pending with { ScreenPoint = screenPoint, ExcludedWindow = excludedWindow };
                _lastGridTimestamp = Stopwatch.GetTimestamp();
                _diagnostics.RequestCoalesced();
                Monitor.Pulse(_gate);
                return;
            }

            if (_pending is not null)
                _diagnostics.RequestCoalesced();
            _lastGrid = grid;
            _lastGridTimestamp = Stopwatch.GetTimestamp();
            _pending = new PendingRequest(screenPoint, excludedWindow, grid, ++_version);
            _diagnostics.RequestSubmitted();
            Monitor.Pulse(_gate);
        }
    }

    public void CancelPending()
    {
        lock (_gate)
        {
            _pending = null;
            _lastGrid = null;
            _lastGridTimestamp = 0;
            _version++;
            Monitor.Pulse(_gate);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending = null;
            _version++;
            _shutdown.Cancel();
            Monitor.Pulse(_gate);
        }

        if (Thread.CurrentThread != _thread)
        {
            var stopped = _thread.Join(250);
            if (stopped)
                _shutdown.Dispose();
        }
    }

    private void Run()
    {
        while (true)
        {
            PendingRequest request;
            long requestVersion;
            lock (_gate)
            {
                while (!_disposed && _pending is null)
                    Monitor.Wait(_gate);

                if (_disposed)
                    return;

                request = _pending!;
                _pending = null;
                requestVersion = request.Version;
            }

            if (!WaitForQuietPeriod(ref request, ref requestVersion))
                return;

            SmartSelectionCandidate? candidate;
            try
            {
                candidate = _detector.Detect(
                    request.ScreenPoint,
                    request.ExcludedWindow,
                    _shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                _diagnostics.Exception();
                candidate = null;
            }

            lock (_gate)
            {
                if (_disposed)
                    return;

                if (requestVersion != _version || _pending is not null)
                {
                    _diagnostics.StaleResult();
                    continue;
                }
            }

            PublishResult(request, candidate, isRefinement: false);

            if (candidate?.NeedsRefinement != true
                || !WaitForRefinementPeriod(requestVersion))
            {
                continue;
            }

            SmartSelectionCandidate? refinedCandidate;
            try
            {
                refinedCandidate = _detector.DetectPrecise(
                    request.ScreenPoint,
                    request.ExcludedWindow,
                    _shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                _diagnostics.Exception();
                refinedCandidate = null;
            }

            if (!IsCurrent(requestVersion))
            {
                _diagnostics.StaleResult();
                continue;
            }

            // A precise pass may fail to expose any UIA element. Do not replace
            // a useful native host candidate with a larger window fallback.
            if (refinedCandidate is not null
                && (!refinedCandidate.IsWindowFallback || candidate.IsWindowFallback))
            {
                PublishResult(request, refinedCandidate, isRefinement: true);
            }
        }
    }

    private void PublishResult(
        PendingRequest request,
        SmartSelectionCandidate? candidate,
        bool isRefinement)
    {
        try
        {
            _resultCallback(new SmartSelectionResult(
                request.ScreenPoint,
                candidate,
                isRefinement));
        }
        catch (InvalidOperationException)
        {
            // The overlay can close between detection and BeginInvoke.
        }
    }

    private bool IsCurrent(long requestVersion)
    {
        lock (_gate)
        {
            return !_disposed
                && requestVersion == _version
                && _pending is null;
        }
    }

    private bool WaitForRefinementPeriod(long requestVersion)
    {
        var deadline = Stopwatch.GetTimestamp()
            + RefinementDelayMilliseconds * Stopwatch.Frequency / 1000;

        lock (_gate)
        {
            while (true)
            {
                if (_disposed || requestVersion != _version || _pending is not null)
                    return false;

                var remaining = deadline - Stopwatch.GetTimestamp();
                if (remaining <= 0)
                    return true;

                var milliseconds = (int)Math.Clamp(
                    remaining * 1000 / Stopwatch.Frequency,
                    1,
                    RefinementDelayMilliseconds);
                Monitor.Wait(_gate, milliseconds);
            }
        }
    }

    private bool WaitForQuietPeriod(ref PendingRequest request, ref long requestVersion)
    {
        var now = Stopwatch.GetTimestamp();
        var quietDeadline = now
            + DebounceMilliseconds * Stopwatch.Frequency / 1000;
        var hardDeadline = now
            + MaximumWaitMilliseconds * Stopwatch.Frequency / 1000;

        while (true)
        {
            lock (_gate)
            {
                if (_disposed)
                    return false;

                if (_pending is not null)
                {
                    request = _pending;
                    requestVersion = request.Version;
                    _pending = null;
                    quietDeadline = Stopwatch.GetTimestamp()
                        + DebounceMilliseconds * Stopwatch.Frequency / 1000;
                    continue;
                }

                var current = Stopwatch.GetTimestamp();
                var deadline = Math.Min(quietDeadline, hardDeadline);
                var remaining = deadline - current;
                if (remaining <= 0)
                {
                    if (_pending is not null)
                    {
                        request = _pending;
                        requestVersion = request.Version;
                        _pending = null;
                    }
                    return true;
                }

                var milliseconds = (int)Math.Clamp(
                    remaining * 1000 / Stopwatch.Frequency,
                    1,
                    DebounceMilliseconds);
                Monitor.Wait(_gate, milliseconds);
            }
        }
    }

    private sealed record PendingRequest(
        Point ScreenPoint,
        IntPtr ExcludedWindow,
        Point Grid,
        long Version);
}

internal sealed record SmartSelectionResult(
    Point ScreenPoint,
    SmartSelectionCandidate? Candidate,
    bool IsRefinement = false);
