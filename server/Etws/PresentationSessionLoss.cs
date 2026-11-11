using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.Etw;

namespace Vestigo.Etws;

/// <summary>Reads ETW session loss counters. The realtime reader does not surface them on each event.</summary>
internal static class PresentationSessionLoss
{
    public static unsafe bool Reported(string sessionName)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(sessionName))
        {
            return false;
        }

        const int nameCharacters = 1024;
        int extra = nameCharacters * 2 * sizeof(char);
        int total = sizeof(EVENT_TRACE_PROPERTIES) + extra;
        byte* buffer = (byte*)NativeMemory.Alloc((nuint)total);
        try
        {
            NativeMemory.Clear(buffer, (nuint)total);
            EVENT_TRACE_PROPERTIES* properties = (EVENT_TRACE_PROPERTIES*)buffer;
            properties->Wnode.BufferSize = (uint)total;
            properties->LoggerNameOffset = (uint)sizeof(EVENT_TRACE_PROPERTIES);
            properties->LogFileNameOffset = (uint)(sizeof(EVENT_TRACE_PROPERTIES) + nameCharacters * sizeof(char));
            fixed (char* name = sessionName)
            {
                WIN32_ERROR error = PInvoke.ControlTrace((ulong)0, name, properties, EVENT_TRACE_CONTROL.EVENT_TRACE_CONTROL_QUERY);
                return error == WIN32_ERROR.ERROR_SUCCESS
                    && (properties->EventsLost > 0 || properties->RealTimeBuffersLost > 0 || properties->LogBuffersLost > 0);
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }
}
