using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SW2URDF.Test
{
    /// <summary>
    /// Runs a test of a form on a thread of its own where nothing can be shown: an exception in an
    /// event handler fails the test instead of opening the exception window, and dialogs are not created.
    /// </summary>
    internal static class SilentFormThread
    {
        private const int CbtHook = 5;
        private const int CreateWindowCode = 3;
        private const int Timeout = 30000;

        public static void Run(Action test)
        {
            Exception failure = null;
            int dialogs = 0;
            Thread thread = new Thread(() =>
            {
                Application.ThreadException += (sender, e) => failure = failure ?? e.Exception;
                HookProc proc = null;
                IntPtr hook = IntPtr.Zero;
                proc = (code, window, data) =>
                {
                    StringBuilder name = new StringBuilder(64);
                    if (code == CreateWindowCode && GetClassName(window, name, name.Capacity) > 0 && name.ToString() == "#32770")
                    {
                        dialogs++;
                        return new IntPtr(1);
                    }
                    return CallNextHookEx(hook, code, window, data);
                };
                hook = SetWindowsHookEx(CbtHook, proc, IntPtr.Zero, GetCurrentThreadId());
                try
                {
                    if (hook == IntPtr.Zero) throw new InvalidOperationException("No hook against dialogs");
                    test();
                }
                catch (Exception e)
                {
                    failure = failure ?? e;
                }
                finally
                {
                    UnhookWindowsHookEx(hook);
                    GC.KeepAlive(proc);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            if (!thread.Join(Timeout)) throw new TimeoutException("The form test does not end");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            if (dialogs > 0) throw new InvalidOperationException("The form wanted to show a dialog");
        }

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder name, int size);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
