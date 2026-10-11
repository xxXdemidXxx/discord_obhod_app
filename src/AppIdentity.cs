using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.IO;
using System.Windows.Forms;

namespace ZapretDesktop {
    public static class AppShortcut {
        public static void Create() {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Обход.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell"); object shell = null, shortcut = null;
            try {
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] {path});
                var type = shortcut.GetType();
                if (File.Exists(path)) {
                    var target = Convert.ToString(type.InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null));
                    if (!String.Equals(target, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) && !(String.Equals(Path.GetDirectoryName(target), AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) && System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(target),@"^Obhod(?:-[0-9.]+)?\.exe$"))) throw new IOException("На рабочем столе уже есть другой ярлык «Обход». Переименуйте его перед созданием нового.");
                }
                type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] {Application.ExecutablePath});
                type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] {AppDomain.CurrentDomain.BaseDirectory});
                type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] {Application.ExecutablePath + ",0"});
                type.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] {"Обход для Discord и YouTube"});
                type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            } finally {
                if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null) Marshal.FinalReleaseComObject(shell);
            }
        }
    }
    public static class AppIcon {
        public static Icon Load() {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZapretDesktop.Icon")) {
                if (stream == null) return (Icon)SystemIcons.Application.Clone();
                using (var icon = new Icon(stream, new Size(32,32))) return (Icon)icon.Clone();
            }
        }
    }
    public static class AppActivation {
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr changeInfo);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint processId);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        public static readonly uint Message = RegisterWindowMessage("ZapretDesktop-Flowseal-Show");
        public static void Allow(IntPtr handle) { ChangeWindowMessageFilterEx(handle, Message, 1, IntPtr.Zero); }
        public static void ShowExisting() {
            for (int attempt = 0; attempt < 10; attempt++) {
                var handle = FindWindow(null, "Обход");
                bool legacy = handle == IntPtr.Zero;
                if (legacy) handle = FindWindow(null, "Zapret Desktop");
                if (handle != IntPtr.Zero) {
                    uint processId; GetWindowThreadProcessId(handle, out processId); AllowSetForegroundWindow(processId);
                    if (legacy) {ShowWindow(handle, 9); SetForegroundWindow(handle);}
                    else PostMessage(handle, Message, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                Thread.Sleep(100);
            }
        }
    }
}
