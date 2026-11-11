using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace DroidNest
{
    public sealed class PhoneDisplayHost : HwndHost
    {
        private const int GwlStyle = -16;
        private const int GwlExStyle = -20;
        private const int WsChild = 0x40000000;
        private const int WsVisible = 0x10000000;
        private const int WsClipChildren = 0x02000000;
        private const int WsClipSiblings = 0x04000000;
        private const int WsPopup = unchecked((int)0x80000000);
        private const int WindowFrame = 0x00C00000 | 0x00040000 | 0x00080000 | 0x00020000 | 0x00010000;
        private const int ExtendedFrame = 0x00040000 | 0x00000100 | 0x00000200 | 0x00000001 | 0x00020000 | 0x00000004;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpFrameChanged = 0x0020;
        private IntPtr container;
        private IntPtr display;
        private uint displayProcess;
        private uint displayThread;
        private IntPtr originalParent;
        private uint originalParentProcess;
        private uint originalParentThread;
        private int originalStyle;
        private int originalExStyle;
        private IntPtr keyboardHook;
        private readonly KeyboardHookCallback keyboardCallback;
        private bool f11Pressed;
        private bool escapePressed;
        private bool modifierFPressed;
        private readonly System.Windows.Threading.DispatcherTimer sizeTimer;

        public event Action FullscreenRequested;
        public bool IsFullscreen { get; set; }

        public event Action<string> DisplayError;

        public PhoneDisplayHost()
        {
            Focusable = true;
            keyboardCallback = OnNativeKeyboard;
            sizeTimer = new System.Windows.Threading.DispatcherTimer();
            sizeTimer.Interval = TimeSpan.FromMilliseconds(250);
            sizeTimer.Tick += delegate { try { SizeDisplay(0); } catch (Win32Exception error) { ReportError(error.Message); } };
        }

        public bool IsAttached
        {
            get { return GetAttachedWindow() != IntPtr.Zero; }
        }

        public IntPtr AttachedHandle
        {
            get { return GetAttachedWindow(); }
        }

        public void Attach(IntPtr displayWindow)
        {
            VerifyAccess();
            if (container == IntPtr.Zero || !IsWindow(container))
                throw new InvalidOperationException("The phone display host has not been loaded.");
            if (displayWindow == IntPtr.Zero || !IsWindow(displayWindow) || displayWindow == container)
                throw new ArgumentException("The Android display window is no longer available.", "displayWindow");
            if (GetAttachedWindow() == displayWindow)
            {
                ResizeDisplay();
                ShowWindow(displayWindow, 5);
                return;
            }

            Detach();
            uint process;
            uint thread = GetWindowThreadProcessId(displayWindow, out process);
            if (thread == 0 || process == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot identify the Android display window.");

            IntPtr parent = GetParent(displayWindow);
            uint parentProcess = 0;
            uint parentThread = parent == IntPtr.Zero ? 0 : GetWindowThreadProcessId(parent, out parentProcess);
            int style = ReadStyle(displayWindow, GwlStyle);
            int exStyle = ReadStyle(displayWindow, GwlExStyle);
            ShowWindow(displayWindow, 0);
            try
            {
                WriteStyle(displayWindow, GwlStyle, (style & ~(WsPopup | WindowFrame | WsVisible)) | WsChild | WsClipSiblings);
                WriteStyle(displayWindow, GwlExStyle, exStyle & ~ExtendedFrame);
                Reparent(displayWindow, container);
            }
            catch
            {
                // A failed SetParent must not leave a hidden, restyled top-level window.
                if (IsSameWindow(displayWindow, process, thread) && GetParent(displayWindow) == parent)
                {
                    WriteStyle(displayWindow, GwlStyle, style & ~WsVisible);
                    WriteStyle(displayWindow, GwlExStyle, exStyle);
                }
                throw;
            }

            display = displayWindow;
            displayProcess = process;
            displayThread = thread;
            originalParent = parent;
            originalParentProcess = parentProcess;
            originalParentThread = parentThread;
            originalStyle = style;
            originalExStyle = exStyle;
            try
            {
                InstallKeyboardHook();
                SizeDisplay(SwpFrameChanged);
                ShowWindow(display, 5);
                sizeTimer.Start();
            }
            catch
            {
                Detach();
                throw;
            }
        }

        public void Detach()
        {
            VerifyAccess();
            IntPtr window = GetAttachedWindow();
            if (window == IntPtr.Zero)
                return;

            ShowWindow(window, 0);
            IntPtr parent = IsSameWindow(originalParent, originalParentProcess, originalParentThread)
                ? originalParent : IntPtr.Zero;
            // Keep ownership records if reparenting fails: destroying the container would
            // otherwise also destroy the foreign child. The caller may retry Detach.
            Reparent(window, parent);
            int style = originalStyle & ~WsVisible;
            if (parent == IntPtr.Zero)
                style &= ~WsChild;
            int exStyle = originalExStyle;
            uint process = displayProcess;
            uint thread = displayThread;
            ClearDisplay();
            if (!IsSameWindow(window, process, thread))
                return;
            WriteStyle(window, GwlStyle, style);
            WriteStyle(window, GwlExStyle, exStyle);
            if (!SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoZOrder | SwpNoActivate | SwpFrameChanged | 0x0001 | 0x0002))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot restore the Android display window frame.");
        }

        public void ResizeDisplay()
        {
            VerifyAccess();
            SizeDisplay(0);
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            container = CreateWindowEx(0, "STATIC", "DroidNest phone display",
                WsChild | WsVisible | WsClipChildren | WsClipSiblings,
                0, 0, 0, 0, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (container == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the phone display container.");
            return new HandleRef(this, container);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            // Do not destroy this HWND if Detach fails while it still owns the foreign child.
            Detach();
            if (hwnd.Handle != IntPtr.Zero && IsWindow(hwnd.Handle) && !DestroyWindow(hwnd.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot destroy the phone display container.");
            container = IntPtr.Zero;
        }

        protected override bool TabIntoCore(TraversalRequest request)
        {
            if (!IsAttached)
                return false;
            Focus();
            FocusDisplay();
            return true;
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            FocusDisplay();
        }

        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0005) // WM_SIZE: the container's client rectangle is already in native pixels.
            {
                try { SizeDisplay(0); }
                catch (Win32Exception error) { ReportError(error.Message); }
            }
            else if (msg == 0x0007) // WM_SETFOCUS
            {
                FocusDisplay();
            }
            else if (msg == 0x0210 && (wParam.ToInt64() & 0xffff) == 0x0201) // WM_PARENTNOTIFY / WM_LBUTTONDOWN
            {
                Focus();
                Dispatcher.BeginInvoke(new Action(FocusDisplay));
            }
            return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        }

        private void FocusDisplay()
        {
            IntPtr window = GetAttachedWindow();
            if (window != IntPtr.Zero)
            {
                uint ownThread = GetCurrentThreadId();
                bool attached = ownThread != displayThread && AttachThreadInput(ownThread, displayThread, true);
                try { SetFocus(window); }
                finally { if (attached) AttachThreadInput(ownThread, displayThread, false); }
            }
        }

        private void SizeDisplay(uint flags)
        {
            IntPtr window = GetAttachedWindow();
            if (window == IntPtr.Zero)
                return;
            NativeRect bounds;
            if (!GetClientRect(container, out bounds))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot measure the phone display container.");
            // Layout/minimize transitions may temporarily have no drawable area.
            // Keep the last video size until a positive client rectangle is available.
            if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
                return;
            NativeRect actual;
            if (flags == 0 && GetClientRect(window, out actual) &&
                actual.Right - actual.Left == bounds.Right - bounds.Left &&
                actual.Bottom - actual.Top == bounds.Bottom - bounds.Top) return;
            // Do not multiply WPF device-independent dimensions by DPI a second time.
            if (!SetWindowPos(window, IntPtr.Zero, 0, 0,
                Math.Max(0, bounds.Right - bounds.Left), Math.Max(0, bounds.Bottom - bounds.Top),
                SwpNoZOrder | SwpNoActivate | flags))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resize the Android display window.");
        }

        private IntPtr GetAttachedWindow()
        {
            if (display != IntPtr.Zero && (!IsSameWindow(display, displayProcess, displayThread) ||
                container == IntPtr.Zero || !IsWindow(container) || GetParent(display) != container))
                ClearDisplay();
            return display;
        }

        private void ClearDisplay()
        {
            sizeTimer.Stop();
            RemoveKeyboardHook();
            display = IntPtr.Zero;
            displayProcess = displayThread = 0;
            originalParent = IntPtr.Zero;
            originalParentProcess = originalParentThread = 0;
            originalStyle = originalExStyle = 0;
        }

        private void InstallKeyboardHook()
        {
            if (keyboardHook != IntPtr.Zero)
                return;
            keyboardHook = SetWindowsHookEx(13, keyboardCallback, GetModuleHandle(null), 0);
            if (keyboardHook == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot route Android display fullscreen shortcuts.");
        }

        private void RemoveKeyboardHook()
        {
            if (keyboardHook != IntPtr.Zero)
            {
                if (!UnhookWindowsHookEx(keyboardHook))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot release Android display keyboard routing.");
                keyboardHook = IntPtr.Zero;
            }
            f11Pressed = escapePressed = modifierFPressed = false;
        }

        private IntPtr OnNativeKeyboard(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && OwnsKeyboardFocus())
            {
                int message = wParam.ToInt32();
                bool down = message == 0x0100 || message == 0x0104;
                bool up = message == 0x0101 || message == 0x0105;
                int key = Marshal.ReadInt32(lParam);
                bool modifierF = key == 0x46 && (modifierFPressed ||
                    (GetAsyncKeyState(0xA3) & 0x8000) != 0);
                if ((down || up) && (key == 0x7A || modifierF || (key == 0x1B && (IsFullscreen || escapePressed))))
                {
                    bool alreadyPressed = key == 0x7A ? f11Pressed : (modifierF ? modifierFPressed : escapePressed);
                    if (key == 0x7A)
                        f11Pressed = down;
                    else if (modifierF)
                        modifierFPressed = down;
                    else
                        escapePressed = down;
                    if (down && !alreadyPressed)
                    {
                        // Keep this system-wide hook short; window changes run after it returns.
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (GetAttachedWindow() == IntPtr.Zero)
                                return;
                            Action handler = FullscreenRequested;
                            if (handler != null)
                                handler();
                        }));
                    }
                    return new IntPtr(1);
                }
            }
            else
            {
                f11Pressed = escapePressed = modifierFPressed = false;
            }
            return CallNextHookEx(keyboardHook, code, wParam, lParam);
        }

        private bool OwnsKeyboardFocus()
        {
            if (!IsSameWindow(display, displayProcess, displayThread) || container == IntPtr.Zero ||
                GetParent(display) != container || GetForegroundWindow() != GetAncestor(container, 2))
                return false;
            GuiThreadInfo info = new GuiThreadInfo();
            info.Size = Marshal.SizeOf(typeof(GuiThreadInfo));
            return GetGUIThreadInfo(0, ref info) &&
                (info.Focus == display || IsChild(display, info.Focus));
        }

        private static bool IsSameWindow(IntPtr window, uint process, uint thread)
        {
            if (window == IntPtr.Zero || !IsWindow(window))
                return false;
            uint actualProcess;
            return GetWindowThreadProcessId(window, out actualProcess) == thread && actualProcess == process;
        }

        private static int ReadStyle(IntPtr window, int index)
        {
            SetLastError(0);
            int value = GetWindowLong(window, index);
            int error = Marshal.GetLastWin32Error();
            if (value == 0 && error != 0)
                throw new Win32Exception(error, "Cannot read the Android display window style.");
            return value;
        }

        private static void WriteStyle(IntPtr window, int index, int value)
        {
            SetLastError(0);
            int previous = SetWindowLong(window, index, value);
            int error = Marshal.GetLastWin32Error();
            if (previous == 0 && error != 0)
                throw new Win32Exception(error, "Cannot update the Android display window style.");
        }

        private static void Reparent(IntPtr window, IntPtr parent)
        {
            SetLastError(0);
            IntPtr previous = SetParent(window, parent);
            int error = Marshal.GetLastWin32Error();
            if (previous == IntPtr.Zero && error != 0)
                throw new Win32Exception(error, "Cannot attach or detach the Android display window.");
        }

        private void ReportError(string message)
        {
            Action<string> handler = DisplayError;
            if (handler != null)
                handler(message);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr window, IntPtr parent);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr window, out NativeRect bounds);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr window);
        [DllImport("kernel32.dll")]
        private static extern void SetLastError(uint error);

        private delegate IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public int Size;
            public uint Flags;
            public IntPtr Active;
            public IntPtr Focus;
            public IntPtr Capture;
            public IntPtr MenuOwner;
            public IntPtr MoveSize;
            public IntPtr Caret;
            public NativeRect CaretRect;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int hook, KeyboardHookCallback callback, IntPtr module, uint thread);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string module);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsChild(IntPtr parent, IntPtr window);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first, uint second, bool attach);
    }
}
