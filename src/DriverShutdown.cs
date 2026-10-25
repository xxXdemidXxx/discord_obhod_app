using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ZapretDesktop {
    public sealed class EngineStopException : Exception {
        public EngineStopException(string message,Exception inner=null):base(message,inner){}
    }
    // Only the last explicitly disabled bypass releases its protected driver.
    // A transient strategy stop must keep both the helper and Telegram alive.
    internal static class DriverShutdown {
        internal static bool OwnedPath(string root,string path) {
            path=EngineHost.NormalizeDriverPath(path);
            if(!Path.IsPathRooted(path))return false;
            string prefix=Path.GetFullPath(root).TrimEnd('\\')+"\\";
            path=Path.GetFullPath(path);
            return path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) &&
                System.Text.RegularExpressions.Regex.IsMatch(path.Substring(prefix.Length),@"^(runtime|startup)-[0-9a-f]{32}\\bin\\WinDivert(32|64)\.sys$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        internal static bool Release(Func<DriverRegistration> read,Action stop,Action delete,Func<bool> consumers,string root) {
            var first=read();if(first==null)return true;
            Validate(first,root);
            string originalPath=first.Path;
            if(first.State!=1 && consumers())throw new EngineStopException("Обходы остановлены. WinDivert используется другой программой; драйвер оставлен работающим.");
            var current=read();if(current==null)return true;
            Validate(current,root);
            if(!String.Equals(originalPath,current.Path,StringComparison.OrdinalIgnoreCase))throw new EngineStopException("Обходы остановлены. Регистрация WinDivert изменилась; выгрузка отменена.");
            if(current.State!=1){if(consumers())throw new EngineStopException("Обходы остановлены. WinDivert ещё используется; закройте другие программы обхода.");stop();}
            current=read();if(current==null)return true;
            Validate(current,root);
            if(current.State!=1 || !String.Equals(originalPath,current.Path,StringComparison.OrdinalIgnoreCase))throw new EngineStopException("Обходы остановлены, но Windows не подтвердила выгрузку WinDivert. Подробности — в журнале.");
            delete();
            if(read()!=null)throw new EngineStopException("WinDivert остановлен, но Windows ещё не удалила его службу. Закройте окно служб; при необходимости перезагрузите компьютер.");
            return true;
        }
        static void Validate(DriverRegistration value,string root) {
            if(value.Type!=1 || value.Start!=3 || !OwnedPath(root,value.Path))throw new EngineStopException("Обходы остановлены. WinDivert принадлежит другой программе или его параметры изменены; служба оставлена без изменений.");
            Trust.NoLinks(EngineHost.NormalizeDriverPath(value.Path));
        }
        internal static void Unload(string engine,Action<string> log) {
            bool locked=false;
            using(var mutex=new Mutex(false,"WinDivertDriverInstallMutex")) {
                try {
                    try{locked=mutex.WaitOne(5000);}catch(AbandonedMutexException){locked=true;}
                    if(!locked)throw new EngineStopException("Обходы остановлены. WinDivert занят другой операцией; выгрузка не подтверждена.");
                    string[] names;
                    using(var services=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services",false)) {
                        if(services==null)throw new IOException("Не удалось проверить службы драйверов.");
                        names=services.GetSubKeyNames().Where(x=>x.StartsWith("WinDivert",StringComparison.OrdinalIgnoreCase)).ToArray();
                    }
                    foreach(string name in names) {
                        if(!String.Equals(name,"WinDivert",StringComparison.OrdinalIgnoreCase))throw new EngineStopException("Обходы остановлены. Найдена другая версия драйвера "+name+"; её служба оставлена без изменений.");
                        using(var service=new NativeDriver(name))Release(service.Read,service.Stop,service.Delete,()=>HasConsumers(engine),HostWire.ProtectedRoot);
                    }
                    if(log!=null)log("Обходы остановлены · WinDivert выгружен · служба драйвера удалена.");
                }catch(EngineStopException){throw;}
                catch(Exception ex){throw new EngineStopException("Обходы остановлены, но полная выгрузка WinDivert не подтверждена. Подробности — в журнале.",ex);}
                finally{if(locked)mutex.ReleaseMutex();}
            }
        }
        // Official REFLECT snapshot pattern: NO_INSTALL never starts a driver.
        // Inspect only; never terminate another application's WinDivert handles.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl,SetLastError=true)] delegate IntPtr Open([MarshalAs(UnmanagedType.LPStr)]string filter,int layer,short priority,ulong flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl,SetLastError=true)] delegate bool Recv(IntPtr handle,byte[] packet,uint length,out uint received,byte[] address);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl,SetLastError=true)] delegate bool Shutdown(IntPtr handle,int how);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl,SetLastError=true)] delegate bool Close(IntPtr handle);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibraryEx(string path,IntPtr reserved,uint flags);
        [DllImport("kernel32.dll",CharSet=CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr library,string name);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr library);
        static T Function<T>(IntPtr library,string name){var address=GetProcAddress(library,name);if(address==IntPtr.Zero)throw new IOException("Нет функции "+name);return (T)(object)Marshal.GetDelegateForFunctionPointer(address,typeof(T));}
        static bool HasConsumers(string engine) {
            if(String.IsNullOrEmpty(engine))throw new IOException("Нет проверенного ядра для проверки WinDivert.");
            Trust.VerifyEngine(engine);string dll=Path.Combine(engine,"bin","WinDivert.dll");Trust.NoLinks(dll);
            IntPtr library=LoadLibraryEx(dll,IntPtr.Zero,8);if(library==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr handle=IntPtr.Zero;Close close=null;
            try {
                var open=Function<Open>(library,"WinDivertOpen");var recv=Function<Recv>(library,"WinDivertRecv");var shutdown=Function<Shutdown>(library,"WinDivertShutdown");close=Function<Close>(library,"WinDivertClose");
                handle=open("true",4,0,0x15);
                if(handle==new IntPtr(-1)){handle=IntPtr.Zero;int error=Marshal.GetLastWin32Error();if(error==1060)return false;throw new Win32Exception(error);}
                if(!shutdown(handle,3))throw new Win32Exception(Marshal.GetLastWin32Error());
                uint received;
                if(recv(handle,new byte[65535],65535,out received,new byte[80]))return true;
                int code=Marshal.GetLastWin32Error();if(code==232)return false;throw new Win32Exception(code);
            }finally{if(handle!=IntPtr.Zero && close!=null)close(handle);FreeLibrary(library);}
        }
        sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid {
            public ServiceHandle():base(true){}
            protected override bool ReleaseHandle(){return CloseServiceHandle(handle);}
        }
        [StructLayout(LayoutKind.Sequential)] struct Config {public uint Type,Start,Error;public IntPtr Path,Group;public uint Tag;public IntPtr Dependencies,Account,Display;}
        [StructLayout(LayoutKind.Sequential)] struct Status {public uint Type,State,Controls,Exit,SpecificExit,Checkpoint,Wait;}
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern ServiceHandle OpenSCManager(string machine,string database,uint access);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern ServiceHandle OpenService(ServiceHandle manager,string name,uint access);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryServiceConfig(ServiceHandle service,IntPtr config,uint size,out uint needed);
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool QueryServiceStatus(ServiceHandle service,out Status status);
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool ControlService(ServiceHandle service,uint control,out Status status);
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool DeleteService(ServiceHandle service);
        [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr handle);
        sealed class NativeDriver : IDisposable {
            readonly string name;readonly ServiceHandle manager;ServiceHandle service;
            internal NativeDriver(string name){this.name=name;manager=OpenSCManager(null,null,1);if(manager.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());}
            internal DriverRegistration Read(){
                if(service==null){service=OpenService(manager,name,0x10025);if(service.IsInvalid){int code=Marshal.GetLastWin32Error();service.Dispose();service=null;if(code==1060)return null;throw new Win32Exception(code);}}
                uint needed;QueryServiceConfig(service,IntPtr.Zero,0,out needed);
                if(needed==0 || needed>65536)throw new Win32Exception(Marshal.GetLastWin32Error());
                IntPtr buffer=Marshal.AllocHGlobal((int)needed);
                try{if(!QueryServiceConfig(service,buffer,needed,out needed))throw new Win32Exception(Marshal.GetLastWin32Error());var config=(Config)Marshal.PtrToStructure(buffer,typeof(Config));Status status;if(!QueryServiceStatus(service,out status))throw new Win32Exception(Marshal.GetLastWin32Error());return new DriverRegistration{Path=Marshal.PtrToStringUni(config.Path),Type=config.Type,Start=config.Start,State=status.State};}finally{Marshal.FreeHGlobal(buffer);}
            }
            internal void Stop(){
                var state=Read();if(state==null || state.State==1)return;
                if(state.State!=3){Status status;if(!ControlService(service,1,out status)){int code=Marshal.GetLastWin32Error();if(code!=1062)throw new Win32Exception(code);}}
                var watch=Stopwatch.StartNew();do{state=Read();if(state==null || state.State==1)return;Thread.Sleep(100);}while(watch.ElapsedMilliseconds<5000);
                throw new IOException("WinDivert не остановился за 5 секунд.");
            }
            internal void Delete(){
                if(!DeleteService(service)){int code=Marshal.GetLastWin32Error();if(code!=1072)throw new Win32Exception(code);}
                service.Dispose();service=null;
                var watch=Stopwatch.StartNew();do{using(var check=OpenService(manager,name,4)){if(check.IsInvalid){int code=Marshal.GetLastWin32Error();if(code==1060)return;if(code!=1072)throw new Win32Exception(code);}else throw new IOException("Регистрация драйвера создана повторно.");}Thread.Sleep(100);}while(watch.ElapsedMilliseconds<2000);
                throw new IOException("Удаление службы WinDivert ещё не завершено.");
            }
            public void Dispose(){if(service!=null)service.Dispose();manager.Dispose();}
        }
    }
}
