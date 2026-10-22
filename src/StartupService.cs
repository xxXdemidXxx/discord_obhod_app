using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace ZapretDesktop {
    internal sealed class StartupState {
        public bool Installed,Owned,Running;
        public uint ProcessId;
        public string Installation,Snapshot,Engine,Strategy,Image;
    }
    // SCM runs verified upstream winws, as service.bat does. Writable GUI files
    // are never installed as a SYSTEM executable.
    internal static class StartupService {
        internal const string Name="ByPass.Startup";
        static string Record {get{return Path.Combine(HostWire.ProtectedRoot,"startup.json");}}
#if TESTING
        internal static Func<string,StartupState> TestRead=null;
#endif
        internal static bool SameInstallation(string a,string b) {
            return !String.IsNullOrEmpty(a) && !String.IsNullOrEmpty(b) && String.Equals(Path.GetFullPath(a).TrimEnd('\\'),Path.GetFullPath(b).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);
        }
        internal static string Command(string snapshot,string strategy) {
            return "\""+Path.Combine(snapshot,"bin","winws.exe")+"\" "+Strategy.Arguments(snapshot,strategy);
        }
        internal static void ValidateRecord(StartupState state,string root,string image) {
            if(state==null || String.IsNullOrEmpty(state.Snapshot) || !SameInstallation(Path.GetDirectoryName(state.Snapshot),root) ||
                !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(state.Snapshot),@"^startup-[a-f0-9]{32}$") || state.Image!=image)
                throw new InvalidDataException("Служба ByPass принадлежит другой установке или её параметры изменены.");
            Trust.NoLinks(state.Snapshot);Trust.VerifyEngine(state.Snapshot);
            string current=Command(state.Snapshot,state.Strategy);
            string legacy="\""+Path.Combine(state.Snapshot,"bin","winws.exe")+"\" "+Strategy.Arguments(state.Snapshot,state.Strategy,false);
            // An upgrade must accept the exact, trusted command of older shells.
            // Configure then rewrites it with the current Telegram passthrough.
            if(current!=image && legacy!=image)throw new InvalidDataException("Параметры службы ByPass изменены.");
        }
        internal static StartupState Read(string installation) {
#if TESTING
            if(TestRead!=null)return TestRead(installation);
#endif
            using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+Name)) {
                if(key==null)return new StartupState();
                var result=new StartupState{Installed=true,Image=Convert.ToString(key.GetValue("ImagePath"))};
                using(var service=new ServiceController(Name))result.Running=service.Status==ServiceControllerStatus.Running;
                IntPtr manager=OpenSCManager(null,null,1),handle=IntPtr.Zero;
                try {
                    if(manager==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
                    handle=OpenService(manager,Name,4);if(handle==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
                    Status status;int needed;if(!QueryServiceStatusEx(handle,0,out status,Marshal.SizeOf(typeof(Status)),out needed))throw new Win32Exception(Marshal.GetLastWin32Error());result.ProcessId=status.Pid;
                }finally{if(handle!=IntPtr.Zero)CloseServiceHandle(handle);if(manager!=IntPtr.Zero)CloseServiceHandle(manager);}
                if(File.Exists(Record)) {try {
                    Trust.NoLinks(Record);
                    var value=new JavaScriptSerializer().Deserialize<StartupState>(File.ReadAllText(Record));
                    if(value!=null && SameInstallation(value.Installation,installation) && value.Image==result.Image) {
                        result.Owned=true;result.Installation=value.Installation;result.Snapshot=value.Snapshot;result.Engine=value.Engine;result.Strategy=value.Strategy;
                    }
                }catch(Exception ex){Trace.TraceError(ex.Message);}}
                return result;
            }
        }
        static StartupState Require(string installation) {
            var state=Read(installation);
            if(state.Installed){if(!state.Owned)throw new InvalidOperationException("Автозапуск ByPass уже установлен из другой папки. Отключите его в той установке.");ValidateRecord(state,HostWire.ProtectedRoot,state.Image);}
            return state;
        }
        internal static bool Conflict(uint ownedPid) {
            var processes=Process.GetProcessesByName("winws");
            try{return processes.Any(p=>(uint)p.Id!=ownedPid);}finally{foreach(var process in processes)process.Dispose();}
        }
        internal static void Configure(string installation,string version,string strategy,System.Collections.Generic.Dictionary<string,byte[]> inputs,bool start,Action<string> log) {
            var old=Require(installation);if(String.IsNullOrEmpty(strategy) || Path.GetFileName(strategy)!=strategy || !inputs.ContainsKey(strategy))throw new InvalidDataException("Не найдена выбранная стратегия.");
            if(Conflict(old.Owned?old.ProcessId:0))throw new InvalidOperationException("Сначала выключите другой winws или службу zapret в исходном приложении.");
            // Validate all source data before touching a working service.
            HostWire.ProtectDirectory(HostWire.ProtectedRoot);
            string snapshot=old.Installed && old.Engine==version?old.Snapshot:Path.Combine(HostWire.ProtectedRoot,"startup-"+Guid.NewGuid().ToString("N"));
            HostWire.ProtectDirectory(snapshot);
            bool configured=false;
            try {
                if(old.Installed)Stop(installation);
                EngineHost.WriteSnapshot(snapshot,inputs,old.Installed && old.Snapshot==snapshot);
                string image=Command(snapshot,strategy);EnableTimestamps(image);
                var state=new StartupState{Installation=Path.GetFullPath(installation),Snapshot=snapshot,Engine=version,Strategy=strategy,Image=image};
                SetImage(image,old.Installed);configured=true;
                WriteRecord(state);
                if(start){DriverServiceRecovery.Repair(log);Start();}
                if(log!=null)log("Автозапуск Windows: служба ByPass · "+strategy+" · "+(start?"запущена":"остановлена"));
            }catch {
                if(!old.Installed){try{if(configured){Delete();if(File.Exists(Record))File.Delete(Record);}}catch(Exception recovery){if(log!=null)log("Автозапуск: не удалось отменить установку: "+recovery.Message);}}
                else {try{SetImage(old.Image,true);WriteRecord(old);if(old.Running)Start();}catch(Exception recovery){if(log!=null)log("Автозапуск: не удалось восстановить службу: "+recovery.Message);}}
                throw;
            }
            // Keep protected snapshots while WinDivert can retain their driver.
        }
        static void WriteRecord(StartupState state) {
            Trust.NoLinks(Record);string temp=Record+".tmp";Trust.NoLinks(temp);
            File.WriteAllText(temp,new JavaScriptSerializer().Serialize(state));HostWire.ProtectFile(temp);
            if(File.Exists(Record))File.Replace(temp,Record,null);else File.Move(temp,Record);
        }
        static void EnableTimestamps(string arguments) {
            if(!Strategy.NeedsTcpTimestamps(arguments))return;
            using(var process=Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"netsh.exe"),"interface tcp set global timestamps=enabled"){UseShellExecute=false,CreateNoWindow=true})) {
                if(!process.WaitForExit(5000))throw new IOException("Не удалось включить TCP timestamps.");
                if(process.ExitCode!=0)throw new IOException("Windows не разрешила включить TCP timestamps.");
            }
        }
        static void Start() {
            using(var service=new ServiceController(Name)){if(service.Status==ServiceControllerStatus.Running)return;service.Start();service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(10));}
        }
        internal static void Stop(string installation) {
            var state=Require(installation);if(!state.Installed)return;
            StopNative();
        }
        static void StopNative() {
            using(var service=new ServiceController(Name)) {
                if(service.Status==ServiceControllerStatus.Stopped)return;
                if(service.Status!=ServiceControllerStatus.StopPending)service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(10));
            }
        }
        internal static void Remove(string installation) {
            var state=Require(installation);if(!state.Installed)return;Delete();
            var watch=Stopwatch.StartNew();bool remains;
            do {using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+Name))remains=key!=null;if(!remains)break;System.Threading.Thread.Sleep(100);}while(watch.ElapsedMilliseconds<3000);
            if(remains)throw new IOException("Windows ещё удаляет службу ByPass. Закройте окно служб и повторите проверку; при необходимости перезагрузите компьютер.");
            if(File.Exists(Record))File.Delete(Record);
        }
        static void SetImage(string image,bool exists) {
            IntPtr manager=OpenSCManager(null,null,exists?1u:3u),service=IntPtr.Zero;
            try {
                if(manager==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
                if(exists){service=OpenService(manager,Name,2);if(service==IntPtr.Zero || !ChangeServiceConfig(service,16,2,1,image,null,IntPtr.Zero,null,null,null,null))throw new Win32Exception(Marshal.GetLastWin32Error());}
                else {service=CreateService(manager,Name,"ByPass — обход при старте Windows",0xF01FF,16,2,1,image,null,IntPtr.Zero,null,null,null);if(service==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());}
            }finally{if(service!=IntPtr.Zero)CloseServiceHandle(service);if(manager!=IntPtr.Zero)CloseServiceHandle(manager);}
        }
        static void Delete() {
            StopNative();IntPtr manager=OpenSCManager(null,null,1),service=IntPtr.Zero;
            try {if(manager==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());service=OpenService(manager,Name,0x10000);if(service==IntPtr.Zero || !DeleteService(service))throw new Win32Exception(Marshal.GetLastWin32Error());}
            finally{if(service!=IntPtr.Zero)CloseServiceHandle(service);if(manager!=IntPtr.Zero)CloseServiceHandle(manager);}
        }
        [StructLayout(LayoutKind.Sequential)] struct Status {public uint Type,State,Controls,Exit,SpecificExit,Checkpoint,Wait,Pid,Flags;}
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr OpenSCManager(string machine,string database,uint access);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr OpenService(IntPtr manager,string name,uint access);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateService(IntPtr manager,string name,string display,uint access,uint type,uint start,uint error,string path,string group,IntPtr tag,string dependencies,string account,string password);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool ChangeServiceConfig(IntPtr service,uint type,uint start,uint error,string path,string group,IntPtr tag,string dependencies,string account,string password,string display);
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool QueryServiceStatusEx(IntPtr service,int level,out Status status,int size,out int needed);
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool DeleteService(IntPtr service);
        [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr handle);
    }
}
