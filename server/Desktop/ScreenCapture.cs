using System.Diagnostics;
using System.Runtime.InteropServices;

using Vestigo.Processes;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Graphics.Imaging;
using Windows.Win32.Storage.Xps;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.StationsAndDesktops;
using Windows.Win32.UI.WindowsAndMessaging;

using Win32PInvoke = Windows.Win32.PInvoke;

namespace Vestigo.Desktop;

/// <summary>
///     Captures the virtual desktop, one display, one window, or a rectangle as PNG. Runs on the calling
///     thread's desktop, so interactive captures go through <c>Vestigo.exe --session-helper capture-screen</c>.
/// </summary>
public static class ScreenCapture
{
    private const uint CaptureBlt = 0x40000000;

    private const uint DesktopReadObjects = 0x0001;

    private const uint PwRenderFullContent = 2;

    public static OperationResult<ScreenCaptureResult> Capture(ScreenCaptureRequest request)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<ScreenCaptureResult>.Fail("windows_required", "Screenshots require Windows.");
        }

        nint previousDpi = WindowEnumerator.SetDpiAware();
        try
        {
            if (!TryReadDesktopRelation(out string? desktopError, out bool inactiveDesktop))
            {
                return OperationResult<ScreenCaptureResult>.Fail("desktop_unavailable", desktopError!);
            }

            if (inactiveDesktop && request.Target != ScreenCaptureTarget.Window)
            {
                return OperationResult<ScreenCaptureResult>.Fail(
                    "desktop_unavailable",
                    "A desktop, display, or region capture needs the input desktop. Activate the test desktop first.");
            }

            string fidelity = inactiveDesktop ? "best-effort" : "input-desktop";

            IReadOnlyList<DisplayInfo> displays = DisplayTopology.Enumerate();
            OperationResult<ResolvedCapture> resolved = Resolve(request, displays);
            if (!resolved.Success)
            {
                return OperationResult<ScreenCaptureResult>.Fail(resolved.ErrorCode!, resolved.Error!);
            }

            ResolvedCapture target = resolved.Value!;
            OperationResult<byte[]> pixels = CopyPixels(target, request.IncludeCursor);
            if (!pixels.Success)
            {
                return OperationResult<ScreenCaptureResult>.Fail(pixels.ErrorCode!, pixels.Error!);
            }

            byte[] fullPng = EncodePng(pixels.Value!, target.Bounds.Width, target.Bounds.Height);
            DesktopSize? previewSize = null;
            string? previewBase64 = null;
            if (request.MaxEdge > 0)
            {
                byte[] previewPng = EncodePng(pixels.Value!, target.Bounds.Width, target.Bounds.Height, request.MaxEdge);
                previewSize = ReadPngSize(previewPng);
                previewBase64 = Convert.ToBase64String(previewPng);
            }

            string? fullBase64 = request.IncludeFullImage ? Convert.ToBase64String(fullPng) : null;
            long estimate = 256 + (previewBase64?.Length ?? 0) + (fullBase64?.Length ?? 0);
            if (request.MaxBytes > 0 && estimate > request.MaxBytes)
            {
                return OperationResult<ScreenCaptureResult>.Fail(
                    "too_large",
                    $"Screenshot JSON would exceed the {request.MaxBytes} byte limit.");
            }

            return OperationResult<ScreenCaptureResult>.Ok(new ScreenCaptureResult(
                target.Target,
                target.Bounds,
                new DesktopSize(target.Bounds.Width, target.Bounds.Height),
                previewSize,
                target.Dpi,
                target.Displays,
                target.WindowHandle,
                target.WindowTitle,
                previewBase64,
                fullBase64,
                Process.GetCurrentProcess().SessionId,
                fidelity));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ExternalException)
        {
            return OperationResult<ScreenCaptureResult>.Fail("capture_failed", ex.Message);
        }
        finally
        {
            WindowEnumerator.RestoreDpi(previousDpi);
        }
    }

    /// <summary>Encodes a top-down 32bpp BGRA buffer as PNG. When <paramref name="maxEdge"/> is set, Fant-scales first.</summary>
    internal static unsafe byte[] EncodePng(ReadOnlySpan<byte> bgra, int width, int height, int? maxEdge = null)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Bitmap dimensions must be positive.");
        }

        int stride = checked(width * 4);
        if (bgra.Length < checked(stride * height))
        {
            throw new ArgumentException("Pixel buffer is smaller than width * height * 4.", nameof(bgra));
        }

        int destWidth = width;
        int destHeight = height;
        if (maxEdge is > 0 && Math.Max(width, height) > maxEdge)
        {
            double scale = (double)maxEdge.Value / Math.Max(width, height);
            destWidth = Math.Max(1, (int)Math.Round(width * scale));
            destHeight = Math.Max(1, (int)Math.Round(height * scale));
        }

        HRESULT init = Win32PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
        bool initialized = init.Succeeded;
        try
        {
            return EncodePngCore(bgra, width, height, stride, destWidth, destHeight);
        }
        finally
        {
            if (initialized)
            {
                Win32PInvoke.CoUninitialize();
            }
        }
    }

    internal static DesktopSize ReadPngSize(ReadOnlySpan<byte> png)
    {
        if (png.Length < 24 || png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G')
        {
            throw new InvalidOperationException("Buffer is not a PNG.");
        }

        int width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return new DesktopSize(width, height);
    }


    private static OperationResult<ResolvedCapture> Resolve(ScreenCaptureRequest request, IReadOnlyList<DisplayInfo> displays)
    {
        DesktopRect? virtualScreen = VirtualScreen(displays);
        if (virtualScreen is null)
        {
            return OperationResult<ResolvedCapture>.Fail("desktop_unavailable", "No displays are attached.");
        }

        if (request.Target == ScreenCaptureTarget.Desktop)
        {
            return OkResolved(ScreenCaptureTarget.Desktop, virtualScreen, displays.Select(item => item.GdiDeviceName).ToList(), virtualScreen.DpiOr(displays, 96), null, null);
        }

        if (request.Target == ScreenCaptureTarget.Display)
        {
            DisplayInfo? display = FindDisplay(displays, request.Display);
            return display is null
                ? OperationResult<ResolvedCapture>.Fail(
                    "display_not_found",
                    string.IsNullOrWhiteSpace(request.Display)
                        ? "display is required when target is 'display'."
                        : $"Display '{request.Display}' was not found.")
                : OkResolved(ScreenCaptureTarget.Display, display.Bounds, [display.GdiDeviceName], display.Dpi, null, null);
        }

        if (request.Target == ScreenCaptureTarget.Region)
        {
            if (request.Region is null || request.Region.Width <= 0 || request.Region.Height <= 0)
            {
                return OperationResult<ResolvedCapture>.Fail("invalid_request", "region needs x, y, width and height greater than zero.");
            }

            DesktopRect? clipped = request.Region.Intersection(virtualScreen);
            return clipped is null
                ? OperationResult<ResolvedCapture>.Fail("empty_region", "The rectangle does not intersect the virtual screen.")
                : OkResolved(ScreenCaptureTarget.Region, clipped, Intersecting(displays, clipped), clipped.DpiOr(displays, 96), null, null);
        }

        if (request.Target == ScreenCaptureTarget.Window)
        {
            return ResolveWindow(request, displays);
        }

        return OperationResult<ResolvedCapture>.Fail("invalid_request", "target must be 'desktop', 'display', 'window' or 'region'.");
    }

    private static OperationResult<ResolvedCapture> ResolveWindow(ScreenCaptureRequest request, IReadOnlyList<DisplayInfo> displays)
    {
        if (!string.IsNullOrWhiteSpace(request.WindowHandle))
        {
            if (!WindowInspection.TryParseHandle(request.WindowHandle, out HWND hwnd) || !Win32PInvoke.IsWindow(hwnd))
            {
                return OperationResult<ResolvedCapture>.Fail("window_not_found", "windowHandle is not an existing window.");
            }

            if (Win32PInvoke.IsIconic(hwnd))
            {
                return OperationResult<ResolvedCapture>.Fail("window_minimized", "The window is minimized.");
            }

            return DescribeWindow(hwnd, displays);
        }

        if (request.Pids.Count == 0 && request.ProcessNames.Count == 0 && string.IsNullOrWhiteSpace(request.TitlePattern))
        {
            return OperationResult<ResolvedCapture>.Fail(
                "invalid_request",
                "window needs windowHandle, or at least one of pid, processName and titleRegex.");
        }

        WindowSelector selector = new(
            request.Pids,
            request.ProcessNames,
            request.TitlePattern,
            null,
            false,
            false,
            11);
        WindowListResult listed = WindowEnumerator.List(selector);
        IReadOnlyList<WindowInfo> matches = listed.Windows;
        if (matches.Count == 0)
        {
            return OperationResult<ResolvedCapture>.Fail("window_not_found", "No visible top-level window matched.");
        }

        if (matches.Count > 1)
        {
            string candidates = string.Join(", ", matches.Take(10).Select(window => $"{window.Handle} '{window.Title}'"));
            return OperationResult<ResolvedCapture>.Fail(
                "ambiguous_window",
                $"Matched {matches.Count} windows: {candidates}.");
        }

        WindowInfo match = matches[0];
        if (match.Minimized)
        {
            return OperationResult<ResolvedCapture>.Fail("window_minimized", "The window is minimized.");
        }

        if (!WindowInspection.TryParseHandle(match.Handle, out HWND found) || !Win32PInvoke.IsWindow(found))
        {
            return OperationResult<ResolvedCapture>.Fail("window_not_found", "The matched window no longer exists.");
        }

        return DescribeWindow(found, displays, match.Title);
    }

    private static OperationResult<ResolvedCapture> DescribeWindow(HWND hwnd, IReadOnlyList<DisplayInfo> displays, string? title = null)
    {
        if (!Win32PInvoke.GetWindowRect(hwnd, out RECT raw))
        {
            return OperationResult<ResolvedCapture>.Fail("window_not_found", "The window no longer exists.");
        }

        DesktopRect windowRect = new(raw.left, raw.top, raw.right - raw.left, raw.bottom - raw.top);
        DesktopRect bounds = ExtendedFrameBounds(hwnd) ?? windowRect;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return OperationResult<ResolvedCapture>.Fail("empty_region", "The window has an empty frame.");
        }

        title ??= WindowInspection.Title(hwnd);
        int dpi = WindowDpi(hwnd);
        return OkResolved(
            ScreenCaptureTarget.Window,
            bounds,
            Intersecting(displays, bounds),
            dpi,
            WindowInspection.FormatHandle(hwnd),
            title,
            hwnd,
            windowRect);
    }

    private static OperationResult<ResolvedCapture> OkResolved(
        string target,
        DesktopRect bounds,
        IReadOnlyList<string> displayNames,
        int dpi,
        string? handle,
        string? title,
        HWND hwnd = default,
        DesktopRect? windowRect = null) =>
        OperationResult<ResolvedCapture>.Ok(new ResolvedCapture(target, bounds, displayNames, dpi, handle, title, hwnd, windowRect));

    private static OperationResult<byte[]> CopyPixels(ResolvedCapture target, bool includeCursor)
    {
        if (target.Bounds.Width > 32767 || target.Bounds.Height > 32767)
        {
            return OperationResult<byte[]>.Fail("too_large", "The capture region exceeds 32767 pixels on one edge.");
        }

        HDC screen = Win32PInvoke.GetDC(HWND.Null);
        if (screen.IsNull)
        {
            return OperationResult<byte[]>.Fail("desktop_unavailable", "GetDC failed for the virtual screen.");
        }

        HDC memory = default;
        HBITMAP bitmap = default;
        HGDIOBJ previous = default;
        try
        {
            memory = Win32PInvoke.CreateCompatibleDC(screen);
            if (memory.IsNull)
            {
                return OperationResult<byte[]>.Fail("capture_failed", "CreateCompatibleDC failed.");
            }

            BITMAPINFO info = default;
            info.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            info.bmiHeader.biWidth = target.Bounds.Width;
            info.bmiHeader.biHeight = -target.Bounds.Height;
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = 0;
            unsafe
            {
                void* bits = null;
                bitmap = Win32PInvoke.CreateDIBSection(memory, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, default, 0);
                if (bitmap.IsNull || bits is null)
                {
                    return OperationResult<byte[]>.Fail("capture_failed", "CreateDIBSection failed.");
                }

                previous = Win32PInvoke.SelectObject(memory, bitmap);
                if (!Blit(target, screen, memory))
                {
                    return OperationResult<byte[]>.Fail("desktop_unavailable", "The desktop could not be captured (BitBlt or PrintWindow failed).");
                }

                if (includeCursor)
                {
                    DrawCursor(memory, target.Bounds);
                }

                int stride = checked(target.Bounds.Width * 4);
                byte[] pixels = new byte[checked(stride * target.Bounds.Height)];
                new ReadOnlySpan<byte>(bits, pixels.Length).CopyTo(pixels);
                ForceOpaqueAlpha(pixels);
                return OperationResult<byte[]>.Ok(pixels);
            }
        }
        finally
        {
            if (!previous.IsNull && !memory.IsNull)
            {
                Win32PInvoke.SelectObject(memory, previous);
            }

            if (!bitmap.IsNull)
            {
                Win32PInvoke.DeleteObject(bitmap);
            }

            if (!memory.IsNull)
            {
                Win32PInvoke.DeleteDC(memory);
            }

            Win32PInvoke.ReleaseDC(HWND.Null, screen);
        }
    }

    private static bool Blit(ResolvedCapture target, HDC screen, HDC memory)
    {
        if (target.Target == ScreenCaptureTarget.Window && !target.Window.IsNull)
        {
            if (target.WindowRect is { } windowRect &&
                (windowRect.X != target.Bounds.X || windowRect.Y != target.Bounds.Y ||
                 windowRect.Width != target.Bounds.Width || windowRect.Height != target.Bounds.Height))
            {
                return CropPrintedWindow(target.Window, screen, memory, windowRect, target.Bounds);
            }

            if (Win32PInvoke.PrintWindow(target.Window, memory, (PRINT_WINDOW_FLAGS)PwRenderFullContent))
            {
                return true;
            }
        }

        return ScreenBlit(screen, memory, target.Bounds);
    }

    private static bool CropPrintedWindow(HWND window, HDC screen, HDC memory, DesktopRect windowRect, DesktopRect frame)
    {
        if (windowRect.Width is <= 0 or > 32767 || windowRect.Height is <= 0 or > 32767)
        {
            return ScreenBlit(screen, memory, frame);
        }

        HDC full = Win32PInvoke.CreateCompatibleDC(screen);
        if (full.IsNull)
        {
            return ScreenBlit(screen, memory, frame);
        }

        HBITMAP bitmap = Win32PInvoke.CreateCompatibleBitmap(screen, windowRect.Width, windowRect.Height);
        if (bitmap.IsNull)
        {
            Win32PInvoke.DeleteDC(full);
            return ScreenBlit(screen, memory, frame);
        }

        HGDIOBJ previous = Win32PInvoke.SelectObject(full, bitmap);
        try
        {
            if (!Win32PInvoke.PrintWindow(window, full, (PRINT_WINDOW_FLAGS)PwRenderFullContent))
            {
                return ScreenBlit(screen, memory, frame);
            }

            int dx = frame.X - windowRect.X;
            int dy = frame.Y - windowRect.Y;
            if (dx >= 0 && dy >= 0 && dx + frame.Width <= windowRect.Width && dy + frame.Height <= windowRect.Height)
            {
                return Win32PInvoke.BitBlt(memory, 0, 0, frame.Width, frame.Height, full, dx, dy, ROP_CODE.SRCCOPY);
            }

            return ScreenBlit(screen, memory, frame);
        }
        finally
        {
            if (!previous.IsNull)
            {
                Win32PInvoke.SelectObject(full, previous);
            }

            Win32PInvoke.DeleteObject(bitmap);
            Win32PInvoke.DeleteDC(full);
        }
    }

    private static void ForceOpaqueAlpha(Span<byte> bgra)
    {
        for (int index = 3; index < bgra.Length; index += 4)
        {
            bgra[index] = 255;
        }
    }

    private static bool ScreenBlit(HDC screen, HDC memory, DesktopRect bounds) =>
        Win32PInvoke.BitBlt(
            memory,
            0,
            0,
            bounds.Width,
            bounds.Height,
            screen,
            bounds.X,
            bounds.Y,
            (ROP_CODE)((uint)ROP_CODE.SRCCOPY | CaptureBlt));

    private static unsafe void DrawCursor(HDC hdc, DesktopRect bounds)
    {
        CURSORINFO cursor = default;
        cursor.cbSize = (uint)sizeof(CURSORINFO);
        if (!Win32PInvoke.GetCursorInfo(&cursor) || (cursor.flags & CURSORINFO_FLAGS.CURSOR_SHOWING) == 0 || cursor.hCursor.IsNull)
        {
            return;
        }

        ICONINFO icon = default;
        int hotX = 0;
        int hotY = 0;
        if (Win32PInvoke.GetIconInfo(cursor.hCursor, &icon))
        {
            hotX = (int)icon.xHotspot;
            hotY = (int)icon.yHotspot;
            if (!icon.hbmMask.IsNull)
            {
                Win32PInvoke.DeleteObject(icon.hbmMask);
            }

            if (!icon.hbmColor.IsNull)
            {
                Win32PInvoke.DeleteObject(icon.hbmColor);
            }
        }

        int x = cursor.ptScreenPos.X - bounds.X - hotX;
        int y = cursor.ptScreenPos.Y - bounds.Y - hotY;
        Win32PInvoke.DrawIconEx(hdc, x, y, cursor.hCursor, 0, 0, 0, default, DI_FLAGS.DI_NORMAL);
    }

    private static unsafe byte[] EncodePngCore(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int stride,
        int destWidth,
        int destHeight)
    {
        IWICImagingFactory* factory;
        Win32PInvoke.CoCreateInstance(
                Win32PInvoke.CLSID_WICImagingFactory,
                null,
                CLSCTX.CLSCTX_INPROC_SERVER,
                out factory)
            .ThrowOnFailure();

        IWICBitmap* bitmap = null;
        IWICBitmapScaler* scaler = null;
        IStream* stream = null;
        IWICBitmapEncoder* encoder = null;
        IWICBitmapFrameEncode* frame = null;
        IPropertyBag2* bag = null;
        try
        {
            Guid format = Win32PInvoke.GUID_WICPixelFormat32bppBGRA;
            fixed (byte* pointer = bgra)
            {
                factory->CreateBitmapFromMemory(
                    (uint)width,
                    (uint)height,
                    &format,
                    (uint)stride,
                    (uint)bgra.Length,
                    pointer,
                    &bitmap);
            }

            IWICBitmapSource* source = (IWICBitmapSource*)bitmap;
            if (destWidth != width || destHeight != height)
            {
                factory->CreateBitmapScaler(&scaler);
                scaler->Initialize(source, (uint)destWidth, (uint)destHeight, WICBitmapInterpolationMode.WICBitmapInterpolationModeFant);
                source = (IWICBitmapSource*)scaler;
            }

            Win32PInvoke.CreateStreamOnHGlobal(default, true, &stream).ThrowOnFailure();
            Guid pngFormat = Win32PInvoke.GUID_ContainerFormatPng;
            encoder = factory->CreateEncoder(&pngFormat, null);
            encoder->Initialize(stream, WICBitmapEncoderCacheOption.WICBitmapEncoderNoCache);
            encoder->CreateNewFrame(&frame, &bag);
            frame->Initialize(bag);
            frame->SetSize((uint)destWidth, (uint)destHeight);
            frame->SetPixelFormat(&format);
            frame->WriteSource(source, null);
            frame->Commit();
            encoder->Commit();

            ulong end = 0;
            stream->Seek(0, SeekOrigin.End, &end);
            if (end == 0 || end > int.MaxValue)
            {
                throw new InvalidOperationException("WIC produced an empty PNG.");
            }

            stream->Seek(0, SeekOrigin.Begin, null);
            byte[] png = new byte[end];
            uint read = 0;
            fixed (byte* output = png)
            {
                stream->Read(output, (uint)png.Length, &read).ThrowOnFailure();
            }

            if (read != png.Length)
            {
                throw new InvalidOperationException("WIC produced a short PNG.");
            }

            return png;
        }
        finally
        {
            if (frame is not null)
            {
                frame->Release();
            }

            if (bag is not null)
            {
                bag->Release();
            }

            if (encoder is not null)
            {
                encoder->Release();
            }

            if (stream is not null)
            {
                stream->Release();
            }

            if (scaler is not null)
            {
                scaler->Release();
            }

            if (bitmap is not null)
            {
                bitmap->Release();
            }

            factory->Release();
        }
    }

    private static DesktopRect? VirtualScreen(IReadOnlyList<DisplayInfo> displays)
    {
        if (displays.Count == 0)
        {
            return null;
        }

        int left = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        int top = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        int width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
        int height = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);
        if (width > 0 && height > 0)
        {
            return new DesktopRect(left, top, width, height);
        }

        int minX = displays.Min(display => display.Bounds.X);
        int minY = displays.Min(display => display.Bounds.Y);
        int maxX = displays.Max(display => display.Bounds.X + display.Bounds.Width);
        int maxY = displays.Max(display => display.Bounds.Y + display.Bounds.Height);
        return new DesktopRect(minX, minY, maxX - minX, maxY - minY);
    }

    private static DisplayInfo? FindDisplay(IReadOnlyList<DisplayInfo> displays, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "primary", StringComparison.OrdinalIgnoreCase))
        {
            return displays.FirstOrDefault(display => display.Primary) ?? displays.FirstOrDefault();
        }

        string trimmed = name.Trim();
        return displays.FirstOrDefault(display =>
            string.Equals(display.GdiDeviceName, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> Intersecting(IReadOnlyList<DisplayInfo> displays, DesktopRect bounds) =>
        displays.Where(display => bounds.Intersects(display.Bounds)).Select(display => display.GdiDeviceName).ToList();

    /// <summary>
    ///     Checks that the input desktop is this session's default desktop. With <paramref name="requireDesktopNames" />
    ///     unreadable desktop names count as unavailable, which input injection needs; screenshots tolerate them.
    /// </summary>
    internal static bool IsInputDesktopAvailable(out string? error, bool requireDesktopNames = false) =>
        TryReadDesktopRelation(out error, out bool inactive, requireDesktopNames) && !inactive;

    private static unsafe bool TryReadDesktopRelation(out string? error, out bool inactive, bool requireDesktopNames = false)
    {
        error = null;
        inactive = false;
        HDESK input = Win32PInvoke.OpenInputDesktop(0, false, (DESKTOP_ACCESS_FLAGS)DesktopReadObjects);
        if (input.IsNull)
        {
            error = "The input desktop is not available (lock screen, UAC, or a secure desktop).";
            return false;
        }

        try
        {
            string? inputName = DesktopName((nint)input.Value);
            HDESK current = Win32PInvoke.GetThreadDesktop(Win32PInvoke.GetCurrentThreadId());
            string? currentName = current.IsNull ? null : DesktopName((nint)current.Value);
            if (requireDesktopNames && (string.IsNullOrEmpty(inputName) || string.IsNullOrEmpty(currentName)))
            {
                error = "The input desktop could not be identified, so input was not sent.";
                return false;
            }

            if (DesktopSessionPolicy.IsSecure(inputName))
            {
                error = "The input desktop is not available (lock screen, UAC, or a secure desktop).";
                return false;
            }

            if (!string.IsNullOrEmpty(inputName) &&
                !string.IsNullOrEmpty(currentName) &&
                !string.Equals(inputName, currentName, StringComparison.OrdinalIgnoreCase))
            {
                inactive = true;
                error = "The input desktop is not this session's default desktop (lock screen, UAC, or a secure desktop).";
            }

            return true;
        }
        finally
        {
            Win32PInvoke.CloseDesktop(input);
        }
    }

    private static unsafe string? DesktopName(nint handle)
    {
        uint needed = 0;
        Win32PInvoke.GetUserObjectInformation((HANDLE)handle, USER_OBJECT_INFORMATION_INDEX.UOI_NAME, null, 0, &needed);
        if (needed == 0)
        {
            return null;
        }

        byte[] buffer = new byte[needed];
        fixed (byte* pointer = buffer)
        {
            if (!Win32PInvoke.GetUserObjectInformation(
                    (HANDLE)handle,
                    USER_OBJECT_INFORMATION_INDEX.UOI_NAME,
                    pointer,
                    needed,
                    &needed))
            {
                return null;
            }
        }

        return buffer.Length < 2 ? null : MemoryMarshal.Cast<byte, char>(buffer.AsSpan()).ToString().TrimEnd('\0');
    }

    private static unsafe DesktopRect? ExtendedFrameBounds(HWND window)
    {
        RECT bounds = default;
        return Win32PInvoke.DwmGetWindowAttribute(
                   window,
                   Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
                   &bounds,
                   (uint)sizeof(RECT))
               .Succeeded
            ? new DesktopRect(bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top)
            : null;
    }

    private static int WindowDpi(HWND window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
        {
            return 96;
        }

        uint dpi = Win32PInvoke.GetDpiForWindow(window);
        return dpi == 0 ? 96 : (int)dpi;
    }

    private sealed record ResolvedCapture(
        string Target,
        DesktopRect Bounds,
        IReadOnlyList<string> Displays,
        int Dpi,
        string? WindowHandle,
        string? WindowTitle,
        HWND Window,
        DesktopRect? WindowRect);
}

internal static class DesktopRectExtensions
{
    public static int DpiOr(this DesktopRect bounds, IReadOnlyList<DisplayInfo> displays, int fallback)
    {
        DisplayInfo? match = displays.FirstOrDefault(display =>
            display.Bounds.X == bounds.X && display.Bounds.Y == bounds.Y &&
            display.Bounds.Width == bounds.Width && display.Bounds.Height == bounds.Height);
        return match?.Dpi
               ?? displays.FirstOrDefault(display => bounds.Intersects(display.Bounds))?.Dpi
               ?? fallback;
    }
}
