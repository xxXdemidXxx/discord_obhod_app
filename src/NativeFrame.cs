using System;
using System.Runtime.InteropServices;
using System.Drawing;
namespace ZapretDesktop {
    public static class NativeFrame {
        public const int Caption=0x00C00000;
        [StructLayout(LayoutKind.Sequential)]
        struct MinMaxInfo {public Point Reserved,MaxSize,MaxPosition,MinTrackSize,MaxTrackSize;}
        public static void ConstrainMaximize(IntPtr data,Rectangle monitor,Rectangle work,Size minimum) {
            var info=(MinMaxInfo)Marshal.PtrToStructure(data,typeof(MinMaxInfo));
            // Windows expects the position relative to the monitor, including
            // monitors whose desktop coordinates are negative.
            info.MaxPosition=new Point(work.Left-monitor.Left,work.Top-monitor.Top);
            info.MaxSize=new Point(work.Width,work.Height);info.MinTrackSize=new Point(minimum.Width,minimum.Height);
            Marshal.StructureToPtr(info,data,false);
        }
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
