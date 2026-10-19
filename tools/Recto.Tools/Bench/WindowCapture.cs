using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace Recto.Tools.Bench;

/// <summary>The Direct3D device the capture frame pools hand frames out on.</summary>
internal sealed class CaptureDevice : IDisposable
{
    public IDirect3DDevice Device { get; }

    public CaptureDevice()
    {
        const int hardware = 1, warp = 5, bgraSupport = 0x20, sdkVersion = 7;
        // WARP, the software rasterizer, for machines without a GPU such as virtual machines.
        int hr = Native.D3D11CreateDevice(IntPtr.Zero, hardware, IntPtr.Zero, bgraSupport,
            IntPtr.Zero, 0, sdkVersion, out var d3d, out _, out var context);
        if (hr < 0)
            hr = Native.D3D11CreateDevice(IntPtr.Zero, warp, IntPtr.Zero, bgraSupport,
                IntPtr.Zero, 0, sdkVersion, out d3d, out _, out context);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            var iidDxgiDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, iidDxgiDevice, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(Native.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                try
                {
                    Device = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                }
                finally
                {
                    Marshal.Release(inspectable);
                }
            }
            finally
            {
                Marshal.Release(dxgi);
            }
        }
        finally
        {
            Marshal.Release(context);
            Marshal.Release(d3d);
        }
    }

    public void Dispose() => Device.Dispose();
}

/// <summary>
/// Watches one window's frames for the marker: the magenta band across the top of page 1 of every benchmark
/// document. Windows.Graphics.Capture sees covered windows too, and stamps each frame with the time DWM composed
/// it, so the result doesn't depend on how quickly frames are checked.
/// </summary>
internal sealed class WindowCapture : IDisposable
{
    private const DirectXPixelFormat Format = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly double _fraction;
    private readonly long _launched;
    private readonly Lock _lock = new();
    private SizeInt32 _size;
    private SoftwareBitmap? _match;

    /// <summary>Launch to the composition of the first frame showing the marker, in ms.</summary>
    public double? MatchTime { get; private set; }

    /// <summary>Launch to the end of checking that frame, in ms.</summary>
    public double? DetectedTime { get; private set; }

    private WindowCapture(IntPtr hwnd, CaptureDevice device, double fraction, long launched)
    {
        _device = device.Device;
        _fraction = fraction;
        _launched = launched;
        var item = GraphicsCaptureItem.TryCreateFromWindowId(new Windows.UI.WindowId((ulong)hwnd))
            ?? throw new InvalidOperationException("window can't be captured");
        _size = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, Format, 2, _size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false;
        try
        {
            _session.IsBorderRequired = false;
        }
        catch (Exception)
        {
            // Needs borderless capture access; the yellow border isn't in the frames anyway.
        }
        _session.StartCapture();
    }

    /// <summary>Starts watching a window; null if it can't be captured (yet).</summary>
    public static WindowCapture? TryStart(IntPtr hwnd, CaptureDevice device, double fraction, long launched)
    {
        try
        {
            return new WindowCapture(hwnd, device, fraction, launched);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
    {
        lock (_lock)
        {
            using var frame = pool.TryGetNextFrame();
            if (frame is null || MatchTime is not null) return;
            var content = frame.ContentSize;
            if (content.Width != _size.Width || content.Height != _size.Height)
            {
                // The window was resized; later frames come at the new size.
                _size = content;
                pool.Recreate(_device, Format, 2, content);
            }

            var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask().GetAwaiter().GetResult();
            int width = Math.Min(content.Width, bitmap.PixelWidth), height = Math.Min(content.Height, bitmap.PixelHeight);
            if (HasMarker(bitmap, width, height))
            {
                // SystemRelativeTime is on the QueryPerformanceCounter clock, as Stopwatch is.
                double launched = _launched * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;
                MatchTime = (frame.SystemRelativeTime.Ticks - launched) / TimeSpan.TicksPerMillisecond;
                DetectedTime = Stopwatch.GetElapsedTime(_launched).TotalMilliseconds;
                _match = bitmap;
            }
            else
            {
                bitmap.Dispose();
            }
        }
    }

    private unsafe bool HasMarker(SoftwareBitmap bitmap, int width, int height)
    {
        using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
        var plane = buffer.GetPlaneDescription(0);
        using var reference = buffer.CreateReference();
        reference.As<IMemoryBufferByteAccess>().GetBuffer(out byte* data, out _);
        data += plane.StartIndex;

        // Every other pixel in both directions is plenty for a band thousands of pixels in area.
        int hits = 0, samples = 0;
        for (int y = 0; y < height; y += 2)
        {
            uint* row = (uint*)(data + (long)y * plane.Stride);
            for (int x = 0; x < width; x += 2)
            {
                uint p = row[x];
                uint b = p & 0xFF, g = (p >> 8) & 0xFF, r = (p >> 16) & 0xFF;
                if (r >= 200 && b >= 200 && g <= 70) hits++;
                samples++;
            }
        }
        return samples > 0 && hits >= _fraction * samples;
    }

    /// <summary>Writes the frame the marker was found in as a PNG.</summary>
    public async Task SaveMatchAsync(string path)
    {
        SoftwareBitmap? match;
        lock (_lock) match = _match;
        if (match is null) return;
        using var opaque = SoftwareBitmap.Convert(match, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(opaque);
        await encoder.FlushAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        stream.Seek(0);
        await stream.AsStreamForRead().CopyToAsync(file);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _session.Dispose();
            _pool.Dispose();
            _match?.Dispose();
            _match = null;
        }
    }

    /// <summary>
    /// The viewer's new top-level windows that are shown, not minimized or cloaked, and big enough to be a
    /// document window rather than a splash screen or tooltip.
    /// </summary>
    public static List<IntPtr> FindWindows(HashSet<IntPtr> existing, ProcessTree tree)
    {
        var list = new List<IntPtr>();
        foreach (var hwnd in TopLevelWindows())
        {
            if (existing.Contains(hwnd) || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) continue;
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (!Native.GetWindowRect(hwnd, out var rect) || rect.Right - rect.Left < 200 || rect.Bottom - rect.Top < 150) continue;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (tree.Pids.Contains((int)pid)) list.Add(hwnd);
        }
        return list;
    }

    /// <summary>
    /// Puts a viewer window in front, without taking the focus from what the user is doing. Windows doesn't let a viewer
    /// started from a console in the background take the foreground, so its window can open behind other windows,
    /// where some viewers stop drawing. Topmost keeps it in front until the run closes it; viewers that refuse that
    /// (Acrobat) are moved to the top of the other windows instead, which this repeats if the user brings another
    /// window to the front. Asynchronous, so a viewer that's busy starting up doesn't hold up the timing.
    /// </summary>
    public static void BringToFront(IntPtr hwnd)
    {
        if ((Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0) return;
        const uint Flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_ASYNCWINDOWPOS;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Flags);
        Native.SetWindowPos(hwnd, Native.HWND_TOP, 0, 0, 0, 0, Flags);
    }

    public static List<IntPtr> TopLevelWindows()
    {
        var list = new List<IntPtr>();
        Native.EnumWindows((hwnd, _) =>
        {
            list.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    [ComImport, Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
