using System;using System.ComponentModel;using System.Diagnostics;using System.IO;using System.Linq;using System.Runtime.InteropServices;using System.ServiceProcess;
namespace ZapretDesktop {
    internal static class ExternalBypass {
        [StructLayout(LayoutKind.Sequential)]struct Status{public uint Type,State,Controls,Exit,SpecificExit,Checkpoint,Wait,Pid,Flags;}
        [DllImport("advapi32.dll",SetLastError=true)]static extern bool QueryServiceStatusEx(IntPtr service,int level,out Status status,int size,out int needed);
        // Explicit GUI command only. Never deletes a service, startup registration,
        // driver, file or a process selected by a caller-supplied path/PID.
        internal static void Stop(Action<string> log){
            var processes=Process.GetProcessesByName("winws");
            try{
                foreach(var process in processes){
                    string image;long started;
                    try{image=HostWire.Image(process.Id);started=process.StartTime.ToUniversalTime().Ticks;}catch{if(process.HasExited)continue;throw;}
                    if(!Path.GetFileName(image).Equals("winws.exe",StringComparison.OrdinalIgnoreCase))continue;
                    foreach(var service in ServiceController.GetServices())using(service){
                        Status status;int needed;
                        try{if(!QueryServiceStatusEx(service.ServiceHandle.DangerousGetHandle(),0,out status,Marshal.SizeOf(typeof(Status)),out needed) || status.Pid!=(uint)process.Id)continue;}catch(InvalidOperationException){continue;}catch(Win32Exception){continue;}
                        if(!service.CanStop)throw new IOException("Служба "+service.ServiceName+" не разрешает остановку.");
                        service.Stop();service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(12));if(log!=null)log("Другой обход: остановлена служба "+service.ServiceName+".");
                    }
                    if(process.HasExited)continue;
                    if(process.StartTime.ToUniversalTime().Ticks!=started || !String.Equals(HostWire.Image(process.Id),image,StringComparison.OrdinalIgnoreCase))throw new IOException("Процесс другого обхода изменился. Повторите остановку.");
                    process.Kill();if(!process.WaitForExit(5000))throw new IOException("Другой winws ещё не завершился.");if(log!=null)log("Другой обход: остановлен winws, PID "+process.Id+".");
                }
                var remaining=Process.GetProcessesByName("winws");try{if(remaining.Any())throw new IOException("Обход запустился повторно. Отключите его автозапуск в исходной программе.");}finally{foreach(var item in remaining)item.Dispose();}
            }finally{foreach(var process in processes)process.Dispose();}
        }
    }
}
