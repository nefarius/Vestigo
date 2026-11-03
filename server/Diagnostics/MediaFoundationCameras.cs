using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace Vestigo.Diagnostics;

/// <summary>
///     Lists video-capture sources. A selected source is activated only long enough to read advertised media types,
///     then shut down. No sample is read.
/// </summary>
internal sealed class MediaFoundationCameras : ICameraFormatSource
{
    public OperationResult<CameraFormatList> List(string? device, int limit)
    {
        if (!MediaFoundationSupport.TryStart(out string? code))
        {
            return OperationResult<CameraFormatList>.Fail(
                code!,
                code == "windows_required" ? "Camera format enumeration requires Windows." : "Media Foundation is not available.");
        }

        try
        {
            return Read(device, limit);
        }
        catch (Exception ex) when (ex.HResult < 0)
        {
            return OperationResult<CameraFormatList>.Fail(MediaFoundationSupport.CodeFor(ex.HResult)!, "The camera format list could not be read.");
        }
    }

    private static unsafe OperationResult<CameraFormatList> Read(string? device, int limit)
    {
        IMFAttributes* attributes = null;
        IMFActivate** activates = null;
        uint count = 0;
        try
        {
            HRESULT created = PInvoke.MFCreateAttributes(&attributes, 1);
            if (created.Failed || attributes is null)
            {
                return OperationResult<CameraFormatList>.Fail("unavailable", "Media Foundation is not available.");
            }

            Guid typeKey = PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE;
            Guid video = PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID;
            attributes->SetGUID(&typeKey, &video);
            HRESULT enumerated = PInvoke.MFEnumDeviceSources(attributes, out activates, out count);
            if (enumerated.Failed)
            {
                return OperationResult<CameraFormatList>.Fail(MediaFoundationSupport.CodeFor(enumerated.Value)!, "The camera list could not be read.");
            }

            if (activates is null || count == 0)
            {
                return OperationResult<CameraFormatList>.Ok(new CameraFormatList("enumerated", false, false, false, []));
            }

            if (count > 256)
            {
                return OperationResult<CameraFormatList>.Fail("media_failed", "The camera list was larger than expected.");
            }

            return string.IsNullOrWhiteSpace(device)
                ? Enumerate(activates, count, limit)
                : Advertise(activates, count, device.Trim(), limit);
        }
        finally
        {
            ReleaseAll(activates, count);
            if (attributes is not null)
            {
                attributes->Release();
            }
        }
    }

    private static unsafe OperationResult<CameraFormatList> Enumerate(IMFActivate** activates, uint count, int limit)
    {
        List<CameraDevice> devices = [];
        uint take = Math.Min(count, (uint)limit);
        for (uint index = 0; index < take; index++)
        {
            if (activates[index] is not null)
            {
                devices.Add(Identity(activates[index]));
            }
        }

        return OperationResult<CameraFormatList>.Ok(new CameraFormatList("enumerated", false, false, count > take, devices));
    }

    private static unsafe OperationResult<CameraFormatList> Advertise(IMFActivate** activates, uint count, string device, int limit)
    {
        IMFActivate* match = null;
        int matches = 0;
        for (uint index = 0; index < count; index++)
        {
            IMFActivate* activate = activates[index];
            if (activate is null || !Matches(activate, device, linkOnly: true))
            {
                continue;
            }

            match = activate;
            matches = 1;
            break;
        }

        if (match is null)
        {
            for (uint index = 0; index < count; index++)
            {
                IMFActivate* activate = activates[index];
                if (activate is null || !Matches(activate, device, linkOnly: false))
                {
                    continue;
                }

                matches++;
                match = activate;
            }
        }

        if (matches == 0 || match is null)
        {
            return OperationResult<CameraFormatList>.Fail("not_found", "No video capture device matched the requested id or name.");
        }

        if (matches > 1)
        {
            return OperationResult<CameraFormatList>.Fail("ambiguous_device", "More than one video capture device has that name. Pass its symbolic link.");
        }

        CameraDevice described = Describe(match, limit, out bool truncated);
        return OperationResult<CameraFormatList>.Ok(new CameraFormatList("advertised", true, false, truncated, [described]));
    }

    private static unsafe bool Matches(IMFActivate* activate, string device, bool linkOnly)
    {
        string? link = MediaFoundationSupport.ReadString(activate, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);
        if (link is not null && link.Equals(device, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (linkOnly)
        {
            return false;
        }

        string? name = MediaFoundationSupport.ReadString(activate, PInvoke.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME);
        return name is not null && name.Equals(device, StringComparison.OrdinalIgnoreCase);
    }

    private static unsafe CameraDevice Identity(IMFActivate* activate) =>
        new(
            MediaFoundationSupport.ReadString(activate, PInvoke.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME),
            MediaFoundationSupport.ReadString(activate, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK),
            []);

    private static unsafe CameraDevice Describe(IMFActivate* activate, int limit, out bool truncated)
    {
        truncated = false;
        CameraDevice identity = Identity(activate);
        Guid iid = typeof(IMFMediaSource).GUID;
        void* unknown = activate->ActivateObject(&iid);
        if (unknown is null)
        {
            throw new InvalidOperationException { HResult = unchecked((int)0x80004005) };
        }

        IMFMediaSource* source = (IMFMediaSource*)unknown;
        IMFPresentationDescriptor* presentation = null;
        try
        {
            source->CreatePresentationDescriptor(&presentation);
            if (presentation is null)
            {
                return identity;
            }

            uint streams;
            presentation->GetStreamDescriptorCount(&streams);
            List<CameraFormat> formats = [];
            uint streamCount = Math.Min(streams, 8);
            for (uint stream = 0; stream < streamCount && formats.Count < limit; stream++)
            {
                ReadStream(presentation, stream, formats, limit);
            }

            truncated = formats.Count >= limit;
            return identity with { Formats = formats };
        }
        finally
        {
            if (presentation is not null)
            {
                presentation->Release();
            }

            try
            {
                source->Shutdown();
            }
            catch (Exception ex) when (ex.HResult < 0)
            {
            }

            source->Release();
            activate->ShutdownObject();
        }
    }

    private static unsafe void ReadStream(IMFPresentationDescriptor* presentation, uint stream, List<CameraFormat> formats, int limit)
    {
        BOOL selected;
        IMFStreamDescriptor* descriptor = null;
        IMFMediaTypeHandler* handler = null;
        try
        {
            presentation->GetStreamDescriptorByIndex(stream, &selected, &descriptor);
            if (descriptor is null)
            {
                return;
            }

            descriptor->GetMediaTypeHandler(&handler);
            if (handler is null)
            {
                return;
            }

            uint types;
            handler->GetMediaTypeCount(&types);
            for (uint index = 0; index < types && formats.Count < limit; index++)
            {
                IMFMediaType* mediaType = null;
                try
                {
                    handler->GetMediaTypeByIndex(index, &mediaType);
                    if (mediaType is not null)
                    {
                        formats.Add(Format(mediaType));
                    }
                }
                finally
                {
                    if (mediaType is not null)
                    {
                        mediaType->Release();
                    }
                }
            }
        }
        finally
        {
            if (handler is not null)
            {
                handler->Release();
            }

            if (descriptor is not null)
            {
                descriptor->Release();
            }
        }
    }

    private static unsafe CameraFormat Format(IMFMediaType* mediaType)
    {
        string? subtype = MediaFoundationSupport.ReadGuid(mediaType, PInvoke.MF_MT_SUBTYPE, out Guid subtypeId)
            ? MediaFoundationSupport.SubtypeName(subtypeId)
            : null;
        int? width = null;
        int? height = null;
        double? rate = null;
        if (MediaFoundationSupport.ReadUInt64(mediaType, PInvoke.MF_MT_FRAME_SIZE, out ulong size))
        {
            width = (int)(size >> 32);
            height = (int)(size & 0xFFFFFFFF);
        }

        if (MediaFoundationSupport.ReadUInt64(mediaType, PInvoke.MF_MT_FRAME_RATE, out ulong ratio))
        {
            uint numerator = (uint)(ratio >> 32);
            uint denominator = (uint)(ratio & 0xFFFFFFFF);
            if (denominator != 0)
            {
                rate = numerator / (double)denominator;
            }
        }

        string? interlace = MediaFoundationSupport.ReadUInt32(mediaType, PInvoke.MF_MT_INTERLACE_MODE, out uint mode)
            ? MediaFoundationSupport.Interlace(mode)
            : null;
        return new CameraFormat(subtype, width, height, rate, interlace);
    }

    private static unsafe void ReleaseAll(IMFActivate** activates, uint count)
    {
        if (activates is null)
        {
            return;
        }

        for (uint index = 0; index < count; index++)
        {
            if (activates[index] is not null)
            {
                activates[index]->Release();
            }
        }

        PInvoke.CoTaskMemFree(activates);
    }
}
