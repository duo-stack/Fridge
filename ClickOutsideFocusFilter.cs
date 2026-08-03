using System.Runtime.InteropServices;

namespace Fridge;

internal sealed class ClickOutsideFocusFilter : IMessageFilter
{
    private const int WmLeftButtonDown = 0x0201;
    private const int WmRightButtonDown = 0x0204;
    private const int WmMiddleButtonDown = 0x0207;
    private const int WmNonClientLeftButtonDown = 0x00A1;
    private readonly Action<IntPtr> _pointerDown;

    public ClickOutsideFocusFilter(Action<IntPtr> pointerDown)
    {
        _pointerDown = pointerDown;
    }

    public bool PreFilterMessage(ref Message message)
    {
        if (message.Msg is WmLeftButtonDown or WmRightButtonDown or WmMiddleButtonDown or WmNonClientLeftButtonDown)
        {
            _pointerDown(message.HWnd);
        }

        return false;
    }

    public static bool IsHandleInside(Control control, IntPtr targetHandle)
    {
        return targetHandle != IntPtr.Zero &&
               (targetHandle == control.Handle || IsChild(control.Handle, targetHandle));
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parentWindow, IntPtr childWindow);
}
