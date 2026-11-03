using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace Vestigo.Diagnostics;

/// <summary>Shared Media Foundation startup and attribute reads. Enumeration does not activate a transform.</summary>
internal static class MediaFoundationSupport
{
    private static int _started;

    public static bool TryStart(out string? code)
    {
        code = null;
        if (!OperatingSystem.IsWindows())
        {
            code = "windows_required";
            return false;
        }

        if (Volatile.Read(ref _started) == 1)
        {
            return true;
        }

        try
        {
            HRESULT started = PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_NOSOCKET);
            if (started.Failed)
            {
                code = "unavailable";
                return false;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            code = "unavailable";
            return false;
        }

        Volatile.Write(ref _started, 1);
        return true;
    }

    public static string? CodeFor(int hresult) => unchecked((uint)hresult) switch
    {
        0x80070005 => "access_denied",
        0x80070020 or 0x80070021 => "device_busy",
        0x80070002 or 0x80070490 => "not_found",
        _ => "media_failed"
    };

    public static unsafe string? ReadString(IMFActivate* attributes, Guid key) => ReadString((IMFAttributes*)attributes, key);

    public static unsafe string? ReadString(IMFMediaType* attributes, Guid key) => ReadString((IMFAttributes*)attributes, key);

    public static unsafe bool ReadUInt32(IMFActivate* attributes, Guid key, out uint value)
    {
        Guid pinned = key;
        uint read = 0;
        try
        {
            attributes->GetUINT32(&pinned, &read);
            value = read;
            return true;
        }
        catch (Exception ex) when (ex.HResult < 0)
        {
            value = 0;
            return false;
        }
    }

    public static unsafe bool ReadUInt32(IMFMediaType* attributes, Guid key, out uint value)
    {
        Guid pinned = key;
        uint read = 0;
        try
        {
            attributes->GetUINT32(&pinned, &read);
            value = read;
            return true;
        }
        catch (Exception ex) when (ex.HResult < 0)
        {
            value = 0;
            return false;
        }
    }

    public static unsafe bool ReadUInt64(IMFMediaType* attributes, Guid key, out ulong value)
    {
        Guid pinned = key;
        ulong read = 0;
        try
        {
            attributes->GetUINT64(&pinned, &read);
            value = read;
            return true;
        }
        catch (Exception ex) when (ex.HResult < 0)
        {
            value = 0;
            return false;
        }
    }

    public static unsafe bool ReadGuid(IMFMediaType* attributes, Guid key, out Guid value)
    {
        Guid pinned = key;
        Guid read = default;
        try
        {
            attributes->GetGUID(&pinned, &read);
            value = read;
            return true;
        }
        catch (Exception ex) when (ex.HResult < 0)
        {
            value = default;
            return false;
        }
    }

    public static unsafe List<string> ReadTypes(IMFActivate* activate, Guid key)
    {
        List<string> names = [];
        Guid pinned = key;
        try
        {
            uint size = 0;
            activate->GetBlobSize(&pinned, &size);
            int stride = sizeof(MFT_REGISTER_TYPE_INFO);
            if (size == 0 || size > 16u * (uint)stride || size % (uint)stride != 0)
            {
                return names;
            }

            byte* buffer = stackalloc byte[(int)size];
            uint written = 0;
            activate->GetBlob(&pinned, buffer, size, &written);
            MFT_REGISTER_TYPE_INFO* info = (MFT_REGISTER_TYPE_INFO*)buffer;
            int count = (int)(size / (uint)stride);
            for (int index = 0; index < count; index++)
            {
                names.Add(SubtypeName(info[index].guidSubtype));
            }
        }
        catch (Exception ex) when (ex.HResult < 0)
        {
        }

        return names;
    }

    public static string SubtypeName(Guid subtype)
    {
        if (subtype == PInvoke.MFVideoFormat_H264)
        {
            return "H264";
        }

        if (subtype == PInvoke.MFVideoFormat_HEVC)
        {
            return "HEVC";
        }

        if (subtype == PInvoke.MFVideoFormat_NV12)
        {
            return "NV12";
        }

        if (subtype == PInvoke.MFVideoFormat_YUY2)
        {
            return "YUY2";
        }

        if (subtype == PInvoke.MFVideoFormat_MJPG)
        {
            return "MJPG";
        }

        return subtype.ToString("D");
    }

    public static string Interlace(uint mode) => mode switch
    {
        2 => "progressive",
        3 => "interleaved-upper-first",
        4 => "interleaved-lower-first",
        5 => "single-upper",
        6 => "single-lower",
        7 => "mixed",
        _ => "unknown"
    };

    private static unsafe string? ReadString(IMFAttributes* attributes, Guid key)
    {
        PWSTR text = default;
        Guid pinned = key;
        try
        {
            uint length = 0;
            attributes->GetAllocatedString(&pinned, &text, &length);
            string? value = text.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex.HResult < 0)
        {
            return null;
        }
        finally
        {
            if (text.Value is not null)
            {
                PInvoke.CoTaskMemFree(text.Value);
            }
        }
    }
}
