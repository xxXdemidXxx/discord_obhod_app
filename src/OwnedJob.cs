using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ZapretDesktop {
    // Closing the app's handle terminates only its own child, including on a crash.
    public sealed class OwnedJob : IDisposable {
        [StructLayout(LayoutKind.Sequential)] struct BasicLimits {
            public long ProcessTime, JobTime; public uint Flags;
            public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses;
            public UIntPtr Affinity; public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] struct IoCounters {
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        }
        [StructLayout(LayoutKind.Sequential)] struct ExtendedLimits {
            public BasicLimits Basic; public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint length);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        readonly SafeFileHandle handle;
        public OwnedJob(Process process) {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimits(); limits.Basic.Flags = 0x2000;
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))) || !AssignProcessToJobObject(handle, process.Handle)) {
                var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "Не удалось привязать ядро к приложению.");
            }
        }
        public void Dispose() { handle.Dispose(); }
    }
}
