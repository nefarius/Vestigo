using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace Vestigo.Diagnostics;

/// <summary>Lists video encoders with <c>MFTEnumEx</c>. Transforms are not activated and no samples are submitted.</summary>
internal sealed class MediaFoundationVideoEncoders : IVideoEncoderSource
{
    public OperationResult<VideoEncoderList> List(int limit)
    {
        if (!MediaFoundationSupport.TryStart(out string? code))
        {
            return OperationResult<VideoEncoderList>.Fail(
                code!,
                code == "windows_required" ? "Video encoder enumeration requires Windows." : "Media Foundation is not available.");
        }

        try
        {
            return OperationResult<VideoEncoderList>.Ok(Read(limit));
        }
        catch (Exception ex) when (ex.HResult < 0)
        {
            return OperationResult<VideoEncoderList>.Fail(MediaFoundationSupport.CodeFor(ex.HResult)!, "The video encoder list could not be read.");
        }
    }

    private static unsafe VideoEncoderList Read(int limit)
    {
        MFT_ENUM_FLAG flags = MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT
            | MFT_ENUM_FLAG.MFT_ENUM_FLAG_ASYNCMFT
            | MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE
            | MFT_ENUM_FLAG.MFT_ENUM_FLAG_LOCALMFT
            | MFT_ENUM_FLAG.MFT_ENUM_FLAG_TRANSCODE_ONLY;
        HRESULT enumerated = PInvoke.MFTEnumEx(PInvoke.MFT_CATEGORY_VIDEO_ENCODER, flags, null, null, out IMFActivate** activates, out uint count);
        if (enumerated.Failed)
        {
            throw new InvalidOperationException { HResult = enumerated.Value };
        }

        List<VideoEncoder> encoders = [];
        try
        {
            if (activates is null || count == 0)
            {
                return Empty(false);
            }

            bool truncated = count > (uint)limit;
            uint take = Math.Min(count, (uint)limit);
            for (uint index = 0; index < count; index++)
            {
                IMFActivate* activate = activates[index];
                if (activate is null)
                {
                    continue;
                }

                try
                {
                    if (index < take)
                    {
                        encoders.Add(Describe(activate));
                    }
                }
                finally
                {
                    activate->Release();
                }
            }

            return new VideoEncoderList("media-foundation", "MFTEnumEx", "enumerated", false, false, truncated, encoders);
        }
        finally
        {
            if (activates is not null)
            {
                PInvoke.CoTaskMemFree(activates);
            }
        }
    }

    private static unsafe VideoEncoder Describe(IMFActivate* activate)
    {
        Guid nameKey = PInvoke.MFT_FRIENDLY_NAME_Attribute;
        Guid classKey = PInvoke.MFT_TRANSFORM_CLSID_Attribute;
        Guid asyncKey = PInvoke.MF_TRANSFORM_ASYNC;
        string? classId = null;
        try
        {
            Guid value;
            activate->GetGUID(&classKey, &value);
            classId = value.ToString("D");
        }
        catch (Exception ex) when (ex.HResult < 0)
        {
        }

        bool async = MediaFoundationSupport.ReadUInt32(activate, asyncKey, out uint flag) && flag != 0;
        string? hardware = MediaFoundationSupport.ReadString(activate, PInvoke.MFT_ENUM_HARDWARE_URL_Attribute);
        return new VideoEncoder(
            MediaFoundationSupport.ReadString(activate, nameKey),
            classId,
            hardware is not null,
            async,
            MediaFoundationSupport.ReadTypes(activate, PInvoke.MFT_INPUT_TYPES_Attributes),
            MediaFoundationSupport.ReadTypes(activate, PInvoke.MFT_OUTPUT_TYPES_Attributes));
    }

    private static VideoEncoderList Empty(bool truncated) =>
        new("media-foundation", "MFTEnumEx", "enumerated", false, false, truncated, []);
}
