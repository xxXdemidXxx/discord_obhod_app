using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ZapretDesktop {
    internal sealed class DriverRegistration {
        public string Path;
        public uint Type,Start,State;
    }
    internal static class DriverServiceRecovery {
        // Repair only a stopped, demand-start WinDivert driver whose file is
        // positively missing. Never stop a driver or rewrite an external path.
        internal static bool MissingFile(string path) {
            string normalized=EngineHost.NormalizeDriverPath(path);
            if(normalized.Length<3 || !Char.IsLetter(normalized[0]) || normalized[1]!=':' || normalized[2]!='\\')return false;
            string name=System.IO.Path.GetFileName(normalized);
            if(!String.Equals(name,"WinDivert64.sys",StringComparison.OrdinalIgnoreCase) && !String.Equals(name,"WinDivert32.sys",StringComparison.OrdinalIgnoreCase))return false;
            try{File.GetAttributes(normalized);return false;}
            catch(FileNotFoundException){return true;}
            catch(DirectoryNotFoundException){return true;}
            catch(UnauthorizedAccessException){return false;}
            catch(IOException){return false;}
        }
        static bool Eligible(DriverRegistration value,Func<string,bool> missing) {
            return value!=null && value.Type==1 && value.Start==3 && value.State==1 && missing(value.Path);
        }
        internal static bool TryRepair(Func<DriverRegistration> read,Action delete,Func<bool> conflict,Func<string,bool> missing,Action<string> log) {
            if(conflict())return false;
            var before=read();if(before==null)return false;
            if(log!=null)log("WinDivert: состояние службы "+before.State+" · тип "+before.Type+" · запуск "+before.Start);
            if(!Eligible(before,missing))return false;
            var current=read();
            if(conflict() || !Eligible(current,missing) || !String.Equals(before.Path,current.Path,StringComparison.OrdinalIgnoreCase))return false;
            delete();
            if(log!=null)log("Удалена остановленная регистрация WinDivert с отсутствующим файлом: "+current.Path+". Оригинальная DLL зарегистрирует драйвер текущего ядра.");
            return true;
        }
        static bool Conflict() {
            var processes=Process.GetProcessesByName("winws");bool result=processes.Length>0;foreach(var process in processes)process.Dispose();return result;
        }
        internal static void Repair(Action<string> log) {
            bool locked=false;
            using(var mutex=new Mutex(false,"WinDivertDriverInstallMutex")) {
                try {
                    try{locked=mutex.WaitOne(5000);}catch(AbandonedMutexException){locked=true;}
                    if(!locked)throw new EngineSetupException("WinDivert занят установкой другим приложением. Повторите запуск.");
                    using(var service=new NativeService())TryRepair(service.Read,service.Delete,Conflict,MissingFile,log);
                }catch(Win32Exception ex){throw new EngineSetupException("Не удалось проверить или очистить устаревшую регистрацию WinDivert.\n"+ex.Message,ex);}
                finally{if(locked)mutex.ReleaseMutex();}
            }
        }
        sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid {
            public ServiceHandle():base(true){}
            protected override bool ReleaseHandle(){return CloseServiceHandle(handle);}
        }
        [StructLayout(LayoutKind.Sequential)] struct Config {
            public uint Type,Start,Error;
            public IntPtr Path,Group;
            public uint Tag;
            public IntPtr Dependencies,Account,Display;
        }
        [StructLayout(LayoutKind.Sequential)] struct Status {
            public uint Type,State,Controls,Exit,SpecificExit,Checkpoint,Wait;
        }
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern ServiceHandle OpenSCManager(string machine,string database,uint access);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern ServiceHandle OpenService(ServiceHandle manager,string name,uint access);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryServiceConfig(ServiceHandle service,IntPtr buffer,uint size,out uint needed);
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool QueryServiceStatus(ServiceHandle service,out Status status);
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool DeleteService(ServiceHandle service);
        [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr handle);
        sealed class NativeService : IDisposable {
            readonly ServiceHandle manager;
            ServiceHandle service;
            public NativeService() {
                manager=OpenSCManager(null,null,1);
                if(manager.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            public DriverRegistration Read() {
                if(service==null) {
                    service=OpenService(manager,"WinDivert",5);
                    if(service.IsInvalid){int error=Marshal.GetLastWin32Error();service.Dispose();service=null;if(error==1060)return null;if(error==1072)throw Pending();throw new Win32Exception(error);}
                }
                uint needed;QueryServiceConfig(service,IntPtr.Zero,0,out needed);int code=Marshal.GetLastWin32Error();
                if(code!=122 || needed==0 || needed>65536)throw new Win32Exception(code);
                IntPtr buffer=Marshal.AllocHGlobal((int)needed);
                try {
                    if(!QueryServiceConfig(service,buffer,needed,out needed))throw new Win32Exception(Marshal.GetLastWin32Error());
                    var config=(Config)Marshal.PtrToStructure(buffer,typeof(Config));Status status;
                    if(!QueryServiceStatus(service,out status))throw new Win32Exception(Marshal.GetLastWin32Error());
                    return new DriverRegistration{Path=Marshal.PtrToStringUni(config.Path),Type=config.Type,Start=config.Start,State=status.State};
                }finally{Marshal.FreeHGlobal(buffer);}
            }
            public void Delete() {
                using(var deletion=OpenService(manager,"WinDivert",0x10000)) {
                    if(deletion.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
                    if(!DeleteService(deletion)){int error=Marshal.GetLastWin32Error();if(error!=1072)throw new Win32Exception(error);}
                }
                service.Dispose();service=null;
                // SCM removes the registration only once every handle is closed.
                var watch=Stopwatch.StartNew();
                do {
                    using(var check=OpenService(manager,"WinDivert",4)) {
                        if(check.IsInvalid){int error=Marshal.GetLastWin32Error();if(error==1060)return;if(error!=1072)throw new Win32Exception(error);}
                        else throw new EngineSetupException("Регистрация WinDivert изменилась во время восстановления. Повторите запуск.");
                    }
                    Thread.Sleep(100);
                }while(watch.ElapsedMilliseconds<2000);
                throw Pending();
            }
            static EngineDriverException Pending(){return new EngineDriverException("WinDivert ожидает удаления службы Windows. Перезагрузите компьютер и повторите запуск.");}
            public void Dispose(){if(service!=null)service.Dispose();manager.Dispose();}
        }
    }
}