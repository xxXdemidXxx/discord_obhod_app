using System;using System.ComponentModel;using System.Diagnostics;using System.IO;using System.Linq;using System.Runtime.InteropServices;using System.ServiceProcess;
namespace ZapretDesktop {
    internal static class ExternalBypass {
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenSCManager(string machine,string database,uint access);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenService(IntPtr manager,string name,uint access);
        [DllImport("advapi32.dll",SetLastError=true)]static extern bool DeleteService(IntPtr service);
        [DllImport("advapi32.dll")]static extern bool CloseServiceHandle(IntPtr handle);
        internal static string ZapretImage(string command){
            command=Environment.ExpandEnvironmentVariables(command??"").Trim();
            if(command.StartsWith("\"")){int end=command.IndexOf('"',1);return end>1?command.Substring(1,end-1):"";}
            int endExe=command.IndexOf(".exe",StringComparison.OrdinalIgnoreCase);return endExe<0?"":command.Substring(0,endExe+4);
        }
        internal static bool IsZapretCommand(string command){
            string image=ZapretImage(command);return Path.IsPathRooted(image) && String.Equals(Path.GetFileName(image),"winws.exe",StringComparison.OrdinalIgnoreCase);
        }
        internal static bool CanRemove(string name,string command){
            if(String.Equals(name,StartupService.Name,StringComparison.OrdinalIgnoreCase) || !IsZapretCommand(command))return false;
            string image=Path.GetFullPath(ZapretImage(command));
            return !image.StartsWith(Path.GetFullPath(HostWire.ProtectedRoot).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);
        }
        internal static void RemoveService(string expected,Func<string> command,Func<bool> stopped,Action stop,Action delete){
            if(!String.Equals(expected,command(),StringComparison.Ordinal))throw new IOException("Параметры старого zapret изменились. Повторите проверку.");
            if(!stopped())stop();
            if(!String.Equals(expected,command(),StringComparison.Ordinal) || !stopped())throw new IOException("Служба старого zapret изменилась или запущена повторно. Удаление отменено.");
            delete();
        }
        internal static void Remove(){
            using(var root=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services",false)){
                if(root==null)throw new IOException("Службы Windows недоступны.");
                foreach(string name in root.GetSubKeyNames()){
                    string image;using(var key=root.OpenSubKey(name,false)){if(key==null)continue;image=Convert.ToString(key.GetValue("ImagePath"));}
                    if(!CanRemove(name,image))continue;
                    IntPtr manager=OpenSCManager(null,null,1),handle=IntPtr.Zero;
                    try{
                        if(manager==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
                        handle=OpenService(manager,name,0x10000);if(handle==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
                        using(var service=new ServiceController(name)){
                            RemoveService(image,()=>{using(var key=root.OpenSubKey(name,false))return key==null?null:Convert.ToString(key.GetValue("ImagePath"));},
                                ()=>{service.Refresh();return service.Status==ServiceControllerStatus.Stopped;},
                                ()=>{service.Refresh();if(service.Status!=ServiceControllerStatus.StopPending)service.Stop();service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(12));},
                                ()=>{if(!DeleteService(handle))throw new Win32Exception(Marshal.GetLastWin32Error());});
                        }
                    }finally{if(handle!=IntPtr.Zero)CloseServiceHandle(handle);if(manager!=IntPtr.Zero)CloseServiceHandle(manager);}
                }
            }
            Stop(null,true);
        }
        [StructLayout(LayoutKind.Sequential)]struct Status{public uint Type,State,Controls,Exit,SpecificExit,Checkpoint,Wait,Pid,Flags;}
        [DllImport("advapi32.dll",SetLastError=true)]static extern bool QueryServiceStatusEx(IntPtr service,int level,out Status status,int size,out int needed);
        // Explicit GUI command only. Never deletes a service, startup registration,
        // driver, file or a process selected by a caller-supplied path/PID.
        internal static void Stop(Action<string> log,bool legacyOnly=false){
            var processes=Process.GetProcessesByName("winws");
            try{
                foreach(var process in processes){
                    string image;long started;
                    try{image=HostWire.Image(process.Id);started=process.StartTime.ToUniversalTime().Ticks;}catch{if(process.HasExited)continue;throw;}
                    if(!Path.GetFileName(image).Equals("winws.exe",StringComparison.OrdinalIgnoreCase))continue;
                    if(legacyOnly && !CanRemove("",image))continue;
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
                var remaining=Process.GetProcessesByName("winws");try{if(remaining.Any(p=>!legacyOnly || CanRemove("",HostWire.Image(p.Id))))throw new IOException("Обход запустился повторно. Отключите его автозапуск в исходной программе.");}finally{foreach(var item in remaining)item.Dispose();}
            }finally{foreach(var process in processes)process.Dispose();}
        }
    }
}
