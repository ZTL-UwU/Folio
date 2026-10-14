using System.Runtime.InteropServices;

namespace Folio.Controls;

/// <summary>
/// A menu or drop-down list opened under the pointer shows a stale cursor (often a resize arrow)
/// until the pointer moves, so popups show the arrow it gets then right away.
/// </summary>
internal static partial class ArrowCursor
{
    private const int IDC_ARROW = 32512;

    public static void Show() => SetCursor(LoadCursorW(IntPtr.Zero, IDC_ARROW));

    [LibraryImport("user32.dll")] private static partial IntPtr LoadCursorW(IntPtr instance, IntPtr name);
    [LibraryImport("user32.dll")] private static partial IntPtr SetCursor(IntPtr cursor);
}
