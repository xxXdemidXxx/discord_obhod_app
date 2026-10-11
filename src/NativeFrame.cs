using System;
using System.Runtime.InteropServices;
namespace ZapretDesktop {
    public static class NativeFrame {
        public const int Caption=0x00C00000;
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr window,int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr window,int index,int value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
        public static void Apply(IntPtr window) {
            int style=GetWindowLong(window,-16),clean=style&~Caption;
            if(style!=clean)SetWindowLong(window,-16,clean);
            int disabled=1;DwmSetWindowAttribute(window,2,ref disabled,sizeof(int));
            SetWindowPos(window,IntPtr.Zero,0,0,0,0,0x37); // Recalculate the frame without moving or activating.
        }
    }
}
