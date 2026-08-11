using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Snipping.App;

internal enum SmartSelectionSource
{
    NativeHwnd,
    Automation,
    Visual,
    WindowFallback
}

internal sealed record SmartSelectionCandidate(
    Rectangle ScreenBounds,
    string Description,
    bool IsWindowFallback = false,
    bool NeedsRefinement = false,
    bool IsContainer = false,
    SmartSelectionSource Source = SmartSelectionSource.Automation,
    int Confidence = 50,
    IntPtr NativeWindowHandle = default,
    string? NativeClassName = null);

internal interface ISmartSelectionDetector
{
    SmartSelectionDiagnostics Diagnostics { get; }

    SmartSelectionCandidate? Detect(
        Point screenPoint,
        IntPtr excludedWindow,
        CancellationToken cancellationToken = default);

    SmartSelectionCandidate? DetectPrecise(
        Point screenPoint,
        IntPtr excludedWindow,
        CancellationToken cancellationToken = default)
        => Detect(screenPoint, excludedWindow, cancellationToken);
}

/// <summary>
/// Detects the smallest useful region under the pointer using a capture-session
/// snapshot. Native HWND rectangles are built once before the overlay appears;
/// UI Automation and visual analysis are lazy, per-root fallbacks.
/// </summary>
internal sealed class SmartSelectionDetector : ISmartSelectionDetector
{
    private const uint GaRoot = 2;
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const int DwmwaCloaked = 14;
    private const int MaxNativeChildrenPerWindow = 768;
    private const int MaxAutomationElements = 2048;
    private const int AutomationBudgetMilliseconds = 120;
    private const int MinNativeCandidateSize = 6;
    private const int MinVisualCandidateSize = 18;
    private const int VisualMinimumDistance = 10;
    private const int VisualMaximumDistance = 1400;
    private const int VisualSampleSpan = 36;
    private const int VisualSampleStep = 4;
    private const int VisualDistanceStep = 2;
    private const double BroadCandidateAreaRatio = 0.55;
    private const double VisualMaximumAreaRatio = 0.86;
    private const double VisualBoundaryThreshold = 0.37;
    private const double VisualSupportThreshold = 0.55;
    private static readonly TimeSpan AutomationSnapshotLifetime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EmptyAutomationSnapshotLifetime = TimeSpan.FromMilliseconds(750);

    private static readonly ControlType[] SupportedControlTypes =
    [
        ControlType.Window,
        ControlType.Pane,
        ControlType.Group,
        ControlType.ToolBar,
        ControlType.Menu,
        ControlType.MenuItem,
        ControlType.Button,
        ControlType.CheckBox,
        ControlType.RadioButton,
        ControlType.ComboBox,
        ControlType.Edit,
        ControlType.List,
        ControlType.ListItem,
        ControlType.Tab,
        ControlType.TabItem,
        ControlType.Tree,
        ControlType.TreeItem,
        ControlType.Hyperlink,
        ControlType.Document,
        ControlType.DataGrid,
        ControlType.DataItem,
        ControlType.Text,
        ControlType.Image,
        ControlType.Header,
        ControlType.HeaderItem,
        ControlType.Thumb,
        ControlType.ScrollBar,
        ControlType.Custom
    ];

    private static readonly HashSet<int> SupportedControlTypeIds =
        SupportedControlTypes.Select(static type => type.Id).ToHashSet();

    private readonly SmartSelectionDiagnostics _diagnostics;
    private readonly object _snapshotGate = new();
    private PixelSurface? _captureSurface;
    private NativeSelectionSnapshot? _nativeSnapshot;
    private readonly Dictionary<IntPtr, AutomationSelectionSnapshot> _automationSnapshots = [];
    private long _captureGeneration;

    public SmartSelectionDetector(SmartSelectionDiagnostics? diagnostics = null)
    {
        _diagnostics = diagnostics ?? new SmartSelectionDiagnostics();
    }

    public SmartSelectionDiagnostics Diagnostics => _diagnostics;

    /// <summary>
    /// Copies the immutable capture bitmap into a compact pixel buffer. The
    /// bitmap is never read by the worker after it has been disposed by the UI.
    /// </summary>
    internal void SetCaptureSurface(Bitmap? bitmap, Point screenOrigin)
    {
        PixelSurface? surface = null;
        if (bitmap is not null)
        {
            var bitmapBounds = new Rectangle(Point.Empty, bitmap.Size);
            var data = bitmap.LockBits(
                bitmapBounds,
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                var stride = data.Stride;
                var absoluteStride = Math.Abs(stride);
                var pixels = new byte[checked(absoluteStride * bitmap.Height)];
                for (var y = 0; y < bitmap.Height; y++)
                {
                    var sourceOffset = stride >= 0
                        ? y * stride
                        : (bitmap.Height - 1 - y) * stride;
                    Marshal.Copy(
                        IntPtr.Add(data.Scan0, sourceOffset),
                        pixels,
                        y * absoluteStride,
                        absoluteStride);
                }

                surface = new PixelSurface(
                    screenOrigin,
                    bitmap.Width,
                    bitmap.Height,
                    absoluteStride,
                    pixels);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        lock (_snapshotGate)
        {
            _captureSurface = surface;
            _nativeSnapshot = null;
            _automationSnapshots.Clear();
            _captureGeneration++;
        }
    }

    /// <summary>
    /// Builds the ShareX/Greenshot-style HWND candidate catalog while the
    /// overlay is still hidden. Mouse movement never performs EnumWindows.
    /// </summary>
    internal void RefreshNativeSnapshot(IntPtr excludedWindow = default)
    {
        var snapshot = BuildNativeSnapshot(excludedWindow);
        lock (_snapshotGate)
        {
            _nativeSnapshot = snapshot;
            _automationSnapshots.Clear();
        }

        _diagnostics.NativeSnapshotBuilt(snapshot.Windows.Count, snapshot.ChildCount);
    }

    public SmartSelectionCandidate? Detect(
        Point screenPoint,
        IntPtr excludedWindow,
        CancellationToken cancellationToken = default)
        => DetectCore(screenPoint, excludedWindow, forcePrecision: false, cancellationToken);

    public SmartSelectionCandidate? DetectPrecise(
        Point screenPoint,
        IntPtr excludedWindow,
        CancellationToken cancellationToken = default)
        => DetectCore(screenPoint, excludedWindow, forcePrecision: true, cancellationToken);

    private SmartSelectionCandidate? DetectCore(
        Point screenPoint,
        IntPtr excludedWindow,
        bool forcePrecision,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var uiaStarted = 0L;
        _diagnostics.QueryStarted();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetNativeWindow(screenPoint, excludedWindow, out var window))
                return null;

            var nativeCandidate = HitTestNative(window, screenPoint);
            if (!forcePrecision)
                return nativeCandidate ?? CreateWindowFallback(window);

            if (nativeCandidate is not null && !nativeCandidate.NeedsRefinement)
                return nativeCandidate;

            AutomationSelectionSnapshot automationSnapshot;
            try
            {
                automationSnapshot = GetAutomationSnapshot(
                    window.Handle,
                    window.Bounds,
                    cancellationToken,
                    ref uiaStarted);
            }
            catch (ElementNotAvailableException)
            {
                _diagnostics.Exception();
                automationSnapshot = new AutomationSelectionSnapshot(
                    DateTime.UtcNow,
                    Array.Empty<SmartSelectionCandidate>());
            }
            catch (COMException)
            {
                _diagnostics.Exception();
                automationSnapshot = new AutomationSelectionSnapshot(
                    DateTime.UtcNow,
                    Array.Empty<SmartSelectionCandidate>());
            }
            catch (InvalidOperationException)
            {
                _diagnostics.Exception();
                automationSnapshot = new AutomationSelectionSnapshot(
                    DateTime.UtcNow,
                    Array.Empty<SmartSelectionCandidate>());
            }
            var automationCandidate = HitTestCandidates(
                automationSnapshot.Candidates,
                screenPoint);

            if (automationCandidate is not null && !automationCandidate.IsContainer)
                return automationCandidate;

            var visualBounds = nativeCandidate is null || nativeCandidate.IsWindowFallback
                ? window.Bounds
                : Rectangle.Intersect(window.Bounds, nativeCandidate.ScreenBounds);
            var visualCandidate = QueryVisualRegion(
                visualBounds,
                screenPoint,
                cancellationToken);

            if (visualCandidate is not null
                && (automationCandidate is null
                    || Area(visualCandidate.ScreenBounds)
                        < Area(automationCandidate.ScreenBounds) * 0.90))
            {
                return visualCandidate;
            }

            if (automationCandidate is not null)
                return automationCandidate;

            return nativeCandidate ?? CreateWindowFallback(window);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ElementNotAvailableException)
        {
            _diagnostics.Exception();
            return null;
        }
        catch (COMException)
        {
            _diagnostics.Exception();
            return null;
        }
        catch (InvalidOperationException)
        {
            _diagnostics.Exception();
            return null;
        }
        finally
        {
            _diagnostics.QueryCompleted(
                ElapsedMilliseconds(started),
                uiaStarted == 0 ? 0 : ElapsedMilliseconds(uiaStarted));
        }
    }

    private bool TryGetNativeWindow(
        Point screenPoint,
        IntPtr excludedWindow,
        out NativeWindowInfo window)
    {
        NativeSelectionSnapshot? snapshot;
        lock (_snapshotGate)
            snapshot = _nativeSnapshot;

        if (snapshot is not null)
        {
            var cached = snapshot.Windows.FirstOrDefault(candidate =>
                candidate.Handle != excludedWindow
                && candidate.Bounds.Contains(screenPoint));
            if (cached is not null)
            {
                window = cached;
                return true;
            }
        }

        var dynamicHandle = FindWindowUnderPoint(screenPoint, excludedWindow);
        if (dynamicHandle == IntPtr.Zero
            || !TryGetWindowBounds(dynamicHandle, out var dynamicBounds))
        {
            window = null!;
            return false;
        }

        window = BuildNativeWindowInfo(dynamicHandle, dynamicBounds);
        return true;
    }

    private SmartSelectionCandidate? HitTestNative(
        NativeWindowInfo window,
        Point screenPoint)
    {
        var child = window.Children
            .Where(candidate => candidate.Bounds.Contains(screenPoint))
            .OrderBy(candidate => Area(candidate.Bounds))
            .ThenBy(candidate => candidate.ZOrder)
            .FirstOrDefault();

        if (child is null)
            return null;

        var isContainer = IsBroadCandidate(child.Bounds, window.Bounds)
            || IsLikelyAutomationHost(child.ClassName);
        _diagnostics.NativeCandidateHit();
        return new SmartSelectionCandidate(
            child.Bounds,
            string.IsNullOrWhiteSpace(child.Title)
                ? child.ClassName is { Length: > 0 } ? child.ClassName : "Control"
                : child.Title,
            NeedsRefinement: isContainer,
            IsContainer: isContainer,
            Source: SmartSelectionSource.NativeHwnd,
            Confidence: isContainer ? 58 : 92,
            NativeWindowHandle: child.Handle,
            NativeClassName: child.ClassName);
    }

    private AutomationSelectionSnapshot GetAutomationSnapshot(
        IntPtr rootHandle,
        Rectangle rootBounds,
        CancellationToken cancellationToken,
        ref long uiaStarted)
    {
        var now = DateTime.UtcNow;
        long generation;
        lock (_snapshotGate)
        {
            generation = _captureGeneration;
            if (_automationSnapshots.TryGetValue(rootHandle, out var cached))
            {
                var lifetime = cached.Candidates.Count == 0
                    ? EmptyAutomationSnapshotLifetime
                    : AutomationSnapshotLifetime;
                if (now - cached.CreatedAtUtc < lifetime)
                {
                    _diagnostics.AutomationSnapshotHit();
                    _diagnostics.CandidateCount(cached.Candidates.Count);
                    return cached;
                }
            }
        }

        uiaStarted = Stopwatch.GetTimestamp();
        _diagnostics.TreeScan();
        var candidates = BuildAutomationCandidates(
            rootHandle,
            rootBounds,
            cancellationToken);
        var snapshot = new AutomationSelectionSnapshot(DateTime.UtcNow, candidates);

        lock (_snapshotGate)
        {
            if (generation == _captureGeneration)
                _automationSnapshots[rootHandle] = snapshot;
        }

        _diagnostics.AutomationSnapshotBuilt(candidates.Count);
        _diagnostics.CandidateCount(candidates.Count);
        return snapshot;
    }

    private IReadOnlyList<SmartSelectionCandidate> BuildAutomationCandidates(
        IntPtr rootHandle,
        Rectangle rootBounds,
        CancellationToken cancellationToken)
    {
        var candidates = new List<SmartSelectionCandidate>();
        var visited = 0;
        var deadline = Stopwatch.GetTimestamp()
            + AutomationBudgetMilliseconds * Stopwatch.Frequency / 1000;

        var cacheRequest = new CacheRequest
        {
            AutomationElementMode = AutomationElementMode.None,
            TreeScope = TreeScope.Element
        };
        cacheRequest.Add(AutomationElement.ControlTypeProperty);
        cacheRequest.Add(AutomationElement.BoundingRectangleProperty);
        cacheRequest.Add(AutomationElement.IsOffscreenProperty);
        cacheRequest.Add(AutomationElement.NameProperty);
        cacheRequest.Add(AutomationElement.LocalizedControlTypeProperty);
        cacheRequest.Add(AutomationElement.NativeWindowHandleProperty);

        using (cacheRequest.Activate())
        {
            var root = AutomationElement.FromHandle(rootHandle);
            var pending = new Stack<AutomationElement>();
            pending.Push(root);
            var walker = TreeWalker.RawViewWalker;

            while (pending.Count > 0
                && visited < MaxAutomationElements
                && Stopwatch.GetTimestamp() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parent = pending.Pop();
                AutomationElement? child;
                try
                {
                    child = walker.GetFirstChild(parent);
                }
                catch (ElementNotAvailableException)
                {
                    _diagnostics.Exception();
                    continue;
                }
                catch (COMException)
                {
                    _diagnostics.Exception();
                    continue;
                }
                catch (InvalidOperationException)
                {
                    _diagnostics.Exception();
                    continue;
                }

                while (child is not null
                    && visited < MaxAutomationElements
                    && Stopwatch.GetTimestamp() < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AutomationElement? next = null;
                    try
                    {
                        visited++;
                        var info = child.Cached;
                        var controlType = info.ControlType;
                        var bounds = ClipToRoot(
                            ToScreenRectangle(info.BoundingRectangle),
                            rootBounds);
                        var nativeHandle = info.NativeWindowHandle;

                        if (!info.IsOffscreen
                            && bounds.Width >= MinNativeCandidateSize
                            && bounds.Height >= MinNativeCandidateSize
                            && BelongsToWindow(nativeHandle, rootHandle)
                            && controlType is not null
                            && controlType != ControlType.Window
                            && SupportedControlTypeIds.Contains(controlType.Id))
                        {
                            var isContainer = IsContainerControlType(controlType.Id)
                                || IsBroadCandidate(bounds, rootBounds);
                            candidates.Add(new SmartSelectionCandidate(
                                bounds,
                                string.IsNullOrWhiteSpace(info.Name)
                                    ? info.LocalizedControlType ?? "Control"
                                    : info.Name,
                                NeedsRefinement: false,
                                IsContainer: isContainer,
                                Source: SmartSelectionSource.Automation,
                                Confidence: isContainer ? 58 : 84,
                                NativeWindowHandle: nativeHandle));
                        }

                        if (visited < MaxAutomationElements)
                            pending.Push(child);
                        next = walker.GetNextSibling(child);
                    }
                    catch (ElementNotAvailableException)
                    {
                        _diagnostics.Exception();
                    }
                    catch (COMException)
                    {
                        _diagnostics.Exception();
                    }
                    catch (InvalidOperationException)
                    {
                        _diagnostics.Exception();
                    }

                    child = next;
                }
            }
        }

        return candidates
            .DistinctBy(static candidate => (
                candidate.ScreenBounds,
                candidate.Description,
                candidate.Source))
            .OrderBy(static candidate => candidate.IsContainer ? 1 : 0)
            .ThenBy(static candidate => Area(candidate.ScreenBounds))
            .Take(128)
            .ToArray();
    }

    private SmartSelectionCandidate? HitTestCandidates(
        IReadOnlyList<SmartSelectionCandidate> candidates,
        Point screenPoint)
    {
        var candidate = candidates
            .Where(item => item.ScreenBounds.Contains(screenPoint))
            .OrderBy(item => item.IsContainer ? 1 : 0)
            .ThenByDescending(item => item.Confidence)
            .ThenBy(item => Area(item.ScreenBounds))
            .FirstOrDefault();
        return candidate;
    }

    private SmartSelectionCandidate? QueryVisualRegion(
        Rectangle searchBounds,
        Point screenPoint,
        CancellationToken cancellationToken)
    {
        PixelSurface? surface;
        lock (_snapshotGate)
            surface = _captureSurface;

        if (surface is null)
            return null;

        var bounds = Rectangle.Intersect(searchBounds, surface.ScreenBounds);
        if (bounds.Width < MinVisualCandidateSize * 2
            || bounds.Height < MinVisualCandidateSize * 2
            || !bounds.Contains(screenPoint))
        {
            return null;
        }

        _diagnostics.VisualScan();
        var left = FindVisualBoundary(surface, screenPoint, bounds, Axis.Vertical, -1, cancellationToken);
        var right = FindVisualBoundary(surface, screenPoint, bounds, Axis.Vertical, 1, cancellationToken);
        var top = FindVisualBoundary(surface, screenPoint, bounds, Axis.Horizontal, -1, cancellationToken);
        var bottom = FindVisualBoundary(surface, screenPoint, bounds, Axis.Horizontal, 1, cancellationToken);
        var foundBoundaries = (left.Found ? 1 : 0)
            + (right.Found ? 1 : 0)
            + (top.Found ? 1 : 0)
            + (bottom.Found ? 1 : 0);

        if (foundBoundaries < 3)
            return null;

        var region = Rectangle.FromLTRB(left.Coordinate, top.Coordinate, right.Coordinate, bottom.Coordinate);
        if (region.Width < MinVisualCandidateSize
            || region.Height < MinVisualCandidateSize
            || !region.Contains(screenPoint))
        {
            return null;
        }

        var searchArea = Area(bounds);
        var regionArea = Area(region);
        if (searchArea <= 0 || regionArea >= searchArea * VisualMaximumAreaRatio)
            return null;

        var score = (left.Score + right.Score + top.Score + bottom.Score) / 4d;
        var confidence = (int)Math.Round(Math.Clamp(score * 100, 0, 100));
        if (confidence < 48)
            return null;

        _diagnostics.VisualResult();
        return new SmartSelectionCandidate(
            region,
            "Visual region",
            Source: SmartSelectionSource.Visual,
            Confidence: confidence);
    }

    private static BoundaryProbe FindVisualBoundary(
        PixelSurface surface,
        Point screenPoint,
        Rectangle bounds,
        Axis axis,
        int direction,
        CancellationToken cancellationToken)
    {
        var coordinate = axis == Axis.Vertical ? screenPoint.X : screenPoint.Y;
        var minimum = axis == Axis.Vertical ? bounds.Left : bounds.Top;
        var maximum = axis == Axis.Vertical ? bounds.Right - 1 : bounds.Bottom - 1;
        var maximumDistance = direction < 0
            ? coordinate - minimum
            : maximum - coordinate;
        maximumDistance = Math.Min(maximumDistance, VisualMaximumDistance);

        var bestScore = 0d;
        var bestCoordinate = direction < 0
            ? (axis == Axis.Vertical ? bounds.Left : bounds.Top)
            : (axis == Axis.Vertical ? bounds.Right : bounds.Bottom);
        var consecutive = 0;

        for (var distance = VisualMinimumDistance;
             distance <= maximumDistance;
             distance += VisualDistanceStep)
        {
            if ((distance & 31) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var sampleCoordinate = coordinate + direction * distance;
            var score = axis == Axis.Vertical
                ? surface.VerticalBoundaryScore(sampleCoordinate, screenPoint.Y, bounds)
                : surface.HorizontalBoundaryScore(screenPoint.X, sampleCoordinate, bounds);

            if (score >= VisualBoundaryThreshold)
            {
                consecutive++;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestCoordinate = axis == Axis.Vertical
                        ? direction < 0 ? sampleCoordinate + 1 : sampleCoordinate
                        : direction < 0 ? sampleCoordinate + 1 : sampleCoordinate;
                }

                if (consecutive >= 2 && bestScore >= VisualBoundaryThreshold)
                    break;
            }
            else
            {
                consecutive = 0;
            }
        }

        return new BoundaryProbe(
            bestCoordinate,
            bestScore,
            bestScore >= VisualBoundaryThreshold);
    }

    private static Rectangle ClipToRoot(Rectangle bounds, Rectangle rootBounds)
    {
        if (bounds.Width < 1 || bounds.Height < 1)
            return Rectangle.Empty;
        return rootBounds.Width > 0 && rootBounds.Height > 0
            ? Rectangle.Intersect(bounds, rootBounds)
            : bounds;
    }

    private static long Area(Rectangle bounds) =>
        (long)Math.Max(0, bounds.Width) * Math.Max(0, bounds.Height);

    private static bool IsBroadCandidate(Rectangle bounds, Rectangle targetBounds)
    {
        var candidateArea = Area(bounds);
        var targetArea = Area(targetBounds);
        return targetArea > 0 && candidateArea >= targetArea * BroadCandidateAreaRatio;
    }

    private static bool IsContainerControlType(int controlTypeId) =>
        controlTypeId == ControlType.Pane.Id
        || controlTypeId == ControlType.Group.Id
        || controlTypeId == ControlType.Document.Id
        || controlTypeId == ControlType.ToolBar.Id
        || controlTypeId == ControlType.Menu.Id
        || controlTypeId == ControlType.List.Id
        || controlTypeId == ControlType.Tree.Id
        || controlTypeId == ControlType.DataGrid.Id
        || controlTypeId == ControlType.Tab.Id
        || controlTypeId == ControlType.ComboBox.Id;

    private static bool IsLikelyAutomationHost(string className) =>
        className.Contains("Chrome_RenderWidgetHost", StringComparison.OrdinalIgnoreCase)
        || className.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase)
        || className.Contains("Internet Explorer_Server", StringComparison.OrdinalIgnoreCase)
        || className.Contains("WebView", StringComparison.OrdinalIgnoreCase)
        || className.Contains("Cef", StringComparison.OrdinalIgnoreCase)
        || className.Contains("DirectUIHWND", StringComparison.OrdinalIgnoreCase)
        || className.Contains("ApplicationFrameInputSinkWindow", StringComparison.OrdinalIgnoreCase);

    private SmartSelectionCandidate CreateWindowFallback(NativeWindowInfo window)
    {
        _diagnostics.FallbackResult();
        return new SmartSelectionCandidate(
            window.Bounds,
            "Window",
            IsWindowFallback: true,
            NeedsRefinement: true,
            IsContainer: true,
            Source: SmartSelectionSource.WindowFallback,
            Confidence: 20,
            NativeWindowHandle: window.Handle);
    }

    private static NativeSelectionSnapshot BuildNativeSnapshot(IntPtr excludedWindow)
    {
        var windows = new List<NativeWindowInfo>();
        EnumWindowsProc callback = (handle, _) =>
        {
            if (handle == excludedWindow
                || !IsWindowVisible(handle)
                || IsCloaked(handle)
                || !TryGetWindowBounds(handle, out var bounds)
                || bounds.Width < MinNativeCandidateSize
                || bounds.Height < MinNativeCandidateSize)
            {
                return true;
            }

            windows.Add(BuildNativeWindowInfo(handle, bounds));
            return true;
        };

        _ = EnumWindows(callback, IntPtr.Zero);
        return new NativeSelectionSnapshot(windows);
    }

    private static NativeWindowInfo BuildNativeWindowInfo(
        IntPtr handle,
        Rectangle bounds)
    {
        var children = new List<NativeChildInfo>();
        var zOrder = 0;
        EnumWindowsProc callback = (child, _) =>
        {
            if (children.Count >= MaxNativeChildrenPerWindow
                || !IsWindowVisible(child)
                || IsCloaked(child)
                || IsTransparent(child)
                || !TryGetWindowBounds(child, out var childBounds))
            {
                return children.Count < MaxNativeChildrenPerWindow;
            }

            var clipped = Rectangle.Intersect(bounds, childBounds);
            if (clipped.Width >= MinNativeCandidateSize
                && clipped.Height >= MinNativeCandidateSize
                && GetAncestor(child, GaRoot) == handle)
            {
                children.Add(new NativeChildInfo(
                    child,
                    clipped,
                    ReadWindowClass(child),
                    ReadWindowTitle(child),
                    zOrder));
            }

            zOrder++;
            return true;
        };

        _ = EnumChildWindows(handle, callback, IntPtr.Zero);
        return new NativeWindowInfo(handle, bounds, children);
    }

    private static IntPtr FindWindowUnderPoint(Point screenPoint, IntPtr excludedWindow)
    {
        var result = IntPtr.Zero;
        EnumWindowsProc callback = (handle, _) =>
        {
            if (handle != excludedWindow
                && IsWindowVisible(handle)
                && !IsCloaked(handle)
                && TryGetWindowBounds(handle, out var bounds)
                && bounds.Contains(screenPoint))
            {
                result = handle;
                return false;
            }

            return true;
        };

        _ = EnumWindows(callback, IntPtr.Zero);
        return result;
    }

    private static string ReadWindowClass(IntPtr handle)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(handle, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : string.Empty;
    }

    private static string ReadWindowTitle(IntPtr handle)
    {
        var buffer = new StringBuilder(256);
        return GetWindowText(handle, buffer, buffer.Capacity) > 0
            ? buffer.ToString()
            : string.Empty;
    }

    private static bool IsTransparent(IntPtr handle) =>
        (GetWindowLongPtr(handle, GwlExStyle).ToInt64() & WsExTransparent) != 0;

    private static bool IsCloaked(IntPtr handle)
    {
        try
        {
            var result = DwmGetWindowAttribute(
                handle,
                DwmwaCloaked,
                out var cloaked,
                Marshal.SizeOf<int>());
            return result == 0 && cloaked != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryGetWindowBounds(IntPtr window, out Rectangle bounds)
    {
        if (GetWindowRect(window, out var nativeBounds))
        {
            bounds = Rectangle.FromLTRB(
                nativeBounds.Left,
                nativeBounds.Top,
                nativeBounds.Right,
                nativeBounds.Bottom);
            return bounds.Width > 0 && bounds.Height > 0;
        }

        bounds = Rectangle.Empty;
        return false;
    }

    private static bool BelongsToWindow(IntPtr window, IntPtr targetWindow)
    {
        if (window == IntPtr.Zero || window == targetWindow)
            return true;

        return GetAncestor(window, GaRoot) == targetWindow;
    }

    private static Rectangle ToScreenRectangle(System.Windows.Rect bounds)
    {
        if (double.IsNaN(bounds.X) || double.IsNaN(bounds.Y)
            || double.IsNaN(bounds.Width) || double.IsNaN(bounds.Height)
            || double.IsInfinity(bounds.X) || double.IsInfinity(bounds.Y)
            || double.IsInfinity(bounds.Width) || double.IsInfinity(bounds.Height))
        {
            return Rectangle.Empty;
        }

        var left = (int)Math.Floor(bounds.Left);
        var top = (int)Math.Floor(bounds.Top);
        var right = (int)Math.Ceiling(bounds.Right);
        var bottom = (int)Math.Ceiling(bounds.Bottom);
        return right > left && bottom > top
            ? Rectangle.FromLTRB(left, top, right, bottom)
            : Rectangle.Empty;
    }

    private static double ElapsedMilliseconds(long started) =>
        (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;

    private enum Axis
    {
        Vertical,
        Horizontal
    }

    private readonly record struct BoundaryProbe(int Coordinate, double Score, bool Found);

    private sealed record NativeSelectionSnapshot(IReadOnlyList<NativeWindowInfo> Windows)
    {
        public int ChildCount => Windows.Sum(static window => window.Children.Count);
    }

    private sealed record NativeWindowInfo(
        IntPtr Handle,
        Rectangle Bounds,
        IReadOnlyList<NativeChildInfo> Children);

    private sealed record NativeChildInfo(
        IntPtr Handle,
        Rectangle Bounds,
        string ClassName,
        string Title,
        int ZOrder);

    private sealed record AutomationSelectionSnapshot(
        DateTime CreatedAtUtc,
        IReadOnlyList<SmartSelectionCandidate> Candidates);

    private sealed record PixelSurface(
        Point Origin,
        int Width,
        int Height,
        int Stride,
        byte[] Pixels)
    {
        public Rectangle ScreenBounds => new(Origin, new Size(Width, Height));

        public double VerticalBoundaryScore(int x, int centerY, Rectangle bounds)
        {
            var span = Math.Min(
                VisualSampleSpan,
                Math.Min(centerY - bounds.Top, bounds.Bottom - centerY - 1));
            return BoundaryScore(x, centerY, span, vertical: true);
        }

        public double HorizontalBoundaryScore(int centerX, int y, Rectangle bounds)
        {
            var span = Math.Min(
                VisualSampleSpan,
                Math.Min(centerX - bounds.Left, bounds.Right - centerX - 1));
            return BoundaryScore(centerX, y, span, vertical: false);
        }

        private double BoundaryScore(int centerX, int centerY, int span, bool vertical)
        {
            if (span < 4)
                return 0;

            var total = 0d;
            var supported = 0;
            var samples = 0;
            for (var offset = -span; offset <= span; offset += VisualSampleStep)
            {
                var x = vertical ? centerX : centerX + offset;
                var y = vertical ? centerY + offset : centerY;
                if (!TryColor(x - (vertical ? 2 : 0), y - (vertical ? 0 : 2), out var before)
                    || !TryColor(x + (vertical ? 2 : 0), y + (vertical ? 0 : 2), out var after))
                {
                    continue;
                }

                var normalized = ColorDistance(before, after) / 765d;
                total += normalized;
                if (normalized >= 0.22)
                    supported++;
                samples++;
            }

            if (samples == 0)
                return 0;

            var average = total / samples;
            var support = supported / (double)samples;
            return support >= VisualSupportThreshold
                ? average * 0.62 + support * 0.38
                : average * 0.35 + support * 0.20;
        }

        private bool TryColor(int x, int y, out RgbColor color)
        {
            var localX = x - Origin.X;
            var localY = y - Origin.Y;
            if ((uint)localX >= (uint)Width || (uint)localY >= (uint)Height)
            {
                color = default;
                return false;
            }

            var index = localY * Stride + localX * 4;
            color = new RgbColor(
                Pixels[index + 2],
                Pixels[index + 1],
                Pixels[index]);
            return true;
        }

        private static int ColorDistance(RgbColor left, RgbColor right) =>
            Math.Abs(left.Red - right.Red)
            + Math.Abs(left.Green - right.Green)
            + Math.Abs(left.Blue - right.Blue);

        private readonly record struct RgbColor(byte Red, byte Green, byte Blue);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(
        IntPtr parent,
        EnumWindowsProc callback,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr window,
        StringBuilder text,
        int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(
        IntPtr window,
        StringBuilder className,
        int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr window,
        int attribute,
        out int value,
        int valueSize);

    private static IntPtr GetWindowLongPtr(IntPtr window, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(window, index)
            : new IntPtr(GetWindowLong32(window, index));
}
