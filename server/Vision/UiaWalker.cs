using System.Diagnostics;
using System.Runtime.InteropServices;

using Vestigo.Desktop;
using Vestigo.Processes;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Accessibility;

namespace Vestigo.Vision;

/// <summary>
///     Reads visible, on-screen UI Automation elements for the windows that overlap a capture. Runs inside the
///     session helper. Values of edit controls are never read, so passwords and typed text are not disclosed;
///     only names and roles are. Uses the CsWin32 COM interfaces directly: a UI Automation wrapper package would pull
///     in the WindowsDesktop runtime as a deployment requirement.
/// </summary>
public static unsafe class UiaWalker
{
    public const int MaxElements = 600;

    public const int MaxWindows = 40;

    private const int MaxDescendantsPerWindow = 4000;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    private static readonly Dictionary<int, string> Roles = new()
    {
        [50000] = "button", [50001] = "calendar", [50002] = "checkbox", [50003] = "combobox", [50004] = "edit",
        [50005] = "hyperlink", [50006] = "image", [50007] = "listitem", [50008] = "list", [50009] = "menu",
        [50010] = "menubar", [50011] = "menuitem", [50012] = "progressbar", [50013] = "radiobutton", [50014] = "scrollbar",
        [50015] = "slider", [50016] = "spinner", [50017] = "statusbar", [50018] = "tab", [50019] = "tabitem",
        [50020] = "text", [50021] = "toolbar", [50022] = "tooltip", [50023] = "tree", [50024] = "treeitem",
        [50025] = "custom", [50026] = "group", [50027] = "thumb", [50028] = "datagrid", [50029] = "dataitem",
        [50030] = "document", [50031] = "splitbutton", [50032] = "window", [50033] = "pane", [50034] = "header",
        [50035] = "headeritem", [50036] = "table", [50037] = "titlebar", [50038] = "separator"
    };

    /// <summary>Controls worth listing even without a name.</summary>
    private static readonly HashSet<int> Interactive = [50000, 50002, 50003, 50004, 50005, 50007, 50011, 50013, 50015, 50016, 50019, 50024, 50029, 50030, 50031];

    private static readonly HashSet<int> Skipped = [50014, 50032, 50038];

    /// <summary>
    ///     <paramref name="windows" /> must be in z-order, topmost first. An element whose center lies inside an
    ///     earlier window is treated as covered and skipped.
    /// </summary>
    public static (IReadOnlyList<ScreenElement> Elements, string Status) Collect(IReadOnlyList<WindowInfo> windows, DesktopRect bounds)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return ([], "uia_unavailable: Windows 10 or later is required.");
        }

        List<ScreenElement> result = [];
        string status = "ok";
        bool initialized = PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED).Succeeded;
        IUIAutomation* automation = null;
        IUIAutomationCacheRequest* request = null;
        IUIAutomationCondition* condition = null;
        try
        {
            HRESULT created = CUIAutomation.CreateInstance(out automation);
            if (created.Failed)
            {
                return ([], $"uia_unavailable: 0x{(uint)created.Value:X8}");
            }

            request = automation->CreateCacheRequest();
            request->AddProperty(UIA_PROPERTY_ID.UIA_NamePropertyId);
            request->AddProperty(UIA_PROPERTY_ID.UIA_ControlTypePropertyId);
            request->AddProperty(UIA_PROPERTY_ID.UIA_BoundingRectanglePropertyId);
            request->AddProperty(UIA_PROPERTY_ID.UIA_IsEnabledPropertyId);
            request->AddProperty(UIA_PROPERTY_ID.UIA_IsOffscreenPropertyId);
            request->AddProperty(UIA_PROPERTY_ID.UIA_HasKeyboardFocusPropertyId);
            condition = automation->CreateTrueCondition();

            Stopwatch clock = Stopwatch.StartNew();
            List<DesktopRect> covers = [];
            int visited = 0;
            foreach (WindowInfo window in windows)
            {
                DesktopRect area = window.VisibleBounds ?? window.Bounds;
                if (window.Minimized || window.Cloaked || !area.Intersects(bounds))
                {
                    continue;
                }

                if (visited++ >= MaxWindows || clock.Elapsed > Budget || result.Count >= MaxElements)
                {
                    status = "truncated";
                    break;
                }

                if (WindowInspection.TryParseHandle(window.Handle, out HWND hwnd))
                {
                    try
                    {
                        Walk(automation, request, condition, hwnd, bounds, covers, result, clock);
                    }
                    catch (COMException)
                    {
                        // One unresponsive window must not fail the whole inspection.
                        status = "partial";
                    }
                }

                covers.Add(area);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return (result, "uia_unavailable: " + ex.Message);
        }
        finally
        {
            if (condition is not null)
            {
                condition->Release();
            }

            if (request is not null)
            {
                request->Release();
            }

            if (automation is not null)
            {
                automation->Release();
            }

            if (initialized)
            {
                PInvoke.CoUninitialize();
            }
        }

        return (result, status);
    }

    private static void Walk(
        IUIAutomation* automation,
        IUIAutomationCacheRequest* request,
        IUIAutomationCondition* condition,
        HWND hwnd,
        DesktopRect bounds,
        List<DesktopRect> covers,
        List<ScreenElement> result,
        Stopwatch clock)
    {
        IUIAutomationElement* root = null;
        IUIAutomationElementArray* array = null;
        try
        {
            root = automation->ElementFromHandle(hwnd);
            if (root is null)
            {
                return;
            }

            array = root->FindAllBuildCache(TreeScope.TreeScope_Descendants, condition, request);
            if (array is null)
            {
                return;
            }

            int length = array->Length;
            for (int index = 0; index < Math.Min(length, MaxDescendantsPerWindow); index++)
            {
                if (result.Count >= MaxElements || clock.Elapsed > Budget)
                {
                    return;
                }

                IUIAutomationElement* element = array->GetElement(index);
                if (element is null)
                {
                    continue;
                }

                try
                {
                    AddElement(element, bounds, covers, result);
                }
                finally
                {
                    element->Release();
                }
            }
        }
        finally
        {
            if (array is not null)
            {
                array->Release();
            }

            if (root is not null)
            {
                root->Release();
            }
        }
    }

    private static void AddElement(IUIAutomationElement* element, DesktopRect bounds, List<DesktopRect> covers, List<ScreenElement> result)
    {
        if (element->CachedIsOffscreen)
        {
            return;
        }

        RECT rect = element->CachedBoundingRectangle;
        int width = rect.right - rect.left;
        int height = rect.bottom - rect.top;
        if (width < 2 || height < 2)
        {
            return;
        }

        DesktopRect box = new(rect.left, rect.top, width, height);
        if (!box.Intersects(bounds))
        {
            return;
        }

        int centerX = box.X + width / 2;
        int centerY = box.Y + height / 2;
        if (covers.Any(cover => centerX >= cover.X && centerX < cover.X + cover.Width && centerY >= cover.Y && centerY < cover.Y + cover.Height))
        {
            return;
        }

        int typeId = (int)element->CachedControlType;
        if (Skipped.Contains(typeId))
        {
            return;
        }

        BSTR cachedName = element->CachedName;
        string? name = cachedName.Length > 0 ? cachedName.ToString() : null;
        PInvoke.SysFreeString(cachedName);
        bool named = !string.IsNullOrWhiteSpace(name);
        if (!named && !Interactive.Contains(typeId))
        {
            return;
        }

        result.Add(new ScreenElement(
            0,
            ElementSource.Uia,
            Roles.GetValueOrDefault(typeId, "control"),
            named ? Trim(name!) : null,
            null,
            box,
            (bool)element->CachedIsEnabled,
            element->CachedHasKeyboardFocus ? true : null));
    }

    private static string Trim(string value)
    {
        string single = value.ReplaceLineEndings(" ").Trim();
        return single.Length <= 120 ? single : single[..120];
    }
}
