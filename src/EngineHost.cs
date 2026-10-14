using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ZapretDesktop {
    internal static class HostWire {
        public const int Limit=65536;
        public static Dictionary<string,object> Read(Stream stream) {
            var header=new byte[4];Exact(stream,header);int length=BitConverter.ToInt32(header,0);
            if(length<2 || length>Limit)throw new InvalidDataException("Недопустимый размер команды.");
            var data=new byte[length];Exact(stream,data);
            return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(data));
        }
        static void Exact(Stream stream,byte[] data){int offset=0,count;while(offset<data.Length){count=stream.Read(data,offset,data.Length-offset);if(count==0)throw new EndOfStreamException();offset+=count;}}
        public static void Write(Stream stream,object message) {
            byte[] data=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(message));
            if(data.Length>Limit)throw new InvalidDataException("Сообщение слишком длинное.");
            lock(stream){byte[] header=BitConverter.GetBytes(data.Length);stream.Write(header,0,4);stream.Write(data,0,data.Length);stream.Flush();}
        }
        [DllImport("kernel32.dll",SetLastError=true)] public static extern bool GetNamedPipeServerProcessId(IntPtr handle,out uint pid);
        [DllImport("kernel32.dll",SetLastError=true)] public static extern bool GetNamedPipeClientProcessId(IntPtr handle,out uint pid);
        [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder path,ref int length);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
        [DllImport("advapi32.dll",SetLastError=true)] static extern bool DuplicateToken(IntPtr token,int level,out IntPtr copy);
        public static void AsCaller(int pid,Action read) {
            IntPtr process=OpenProcess(0x1000,false,pid),token=IntPtr.Zero,copy=IntPtr.Zero;
            try {
                if(process==IntPtr.Zero || !OpenProcessToken(process,10,out token) || !DuplicateToken(token,2,out copy))throw new IOException("Не удалось проверить права клиента.");
                using(WindowsIdentity.Impersonate(copy))read();
            }finally{if(copy!=IntPtr.Zero)CloseHandle(copy);if(token!=IntPtr.Zero)CloseHandle(token);if(process!=IntPtr.Zero)CloseHandle(process);}
        }
        public static string Image(int pid) {
            IntPtr handle=OpenProcess(0x1000,false,pid);if(handle==IntPtr.Zero)throw new IOException("Не удалось проверить процесс.");
            try{var value=new StringBuilder(32768);int length=value.Capacity;if(!QueryFullProcessImageName(handle,0,value,ref length))throw new IOException("Не удалось прочитать путь процесса.");return value.ToString();}finally{CloseHandle(handle);}
        }
        public static string ProtectedRoot {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"ByPass");}}
        public static void ProtectDirectory(string path) {
            Trust.NoLinks(path);Directory.CreateDirectory(path);
            var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
            var admins=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
            acl.SetOwner(admins);
            foreach(var sid in new[]{admins,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})
                acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            Directory.SetAccessControl(path,acl);
        }
        public static void ProtectFile(string path) {
            Trust.NoLinks(path);var acl=new FileSecurity();acl.SetAccessRuleProtection(true,false);
            var admins=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);acl.SetOwner(admins);
            foreach(var sid in new[]{admins,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.ReadAndExecute,AccessControlType.Allow));File.SetAccessControl(path,acl);
        }
    }
    // This entry point is compiled into a separate executable with no HTTP client or GUI.
    internal static class EngineHost {
        [STAThread] static int Main(string[] args) {
            try {
                if(args.Length!=3 || (args[0]!="--bootstrap" && args[0]!="--serve"))return 1;
                int parent=Int32.Parse(args[1]);string pipeName=args[2];
                if(!System.Text.RegularExpressions.Regex.IsMatch(pipeName,@"^ByPass-[a-f0-9]{32}$"))return 1;
                if(!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))return 1;
                if(args[0]=="--bootstrap") {
                    string image=Assembly.GetExecutingAssembly().Location;
                    using(var input=new FileStream(image,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                        byte[] data=new byte[input.Length];int offset=0,count;while(offset<data.Length && (count=input.Read(data,offset,data.Length-offset))>0)offset+=count;
                        if(offset!=data.Length)throw new IOException();
                        string hash;using(var sha=System.Security.Cryptography.SHA256.Create())hash=Trust.Hex(sha.ComputeHash(data));
                        HostWire.ProtectDirectory(HostWire.ProtectedRoot);
                        string folder=Path.Combine(HostWire.ProtectedRoot,"host-"+hash);HostWire.ProtectDirectory(folder);
                        string target=Path.Combine(folder,"ByPass.EngineHost.exe");Trust.NoLinks(target);
                        if(File.Exists(target) && Trust.Hash(target)!=hash)throw new InvalidDataException("Повреждён защищённый компонент.");
                        if(!File.Exists(target))using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None))output.Write(data,0,data.Length);
                        HostWire.ProtectFile(target);
                        Process.Start(new ProcessStartInfo(target,"--serve "+parent+" "+pipeName){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=folder});
                    }
                    return 0;
                }
                Serve(parent,pipeName);return 0;
            }catch{return 1;}
        }
        static void Serve(int parent,string name) {
            using(var gui=Process.GetProcessById(parent))using(var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.None,TokenImpersonationLevel.Impersonation))using(var runner=new DirectEngineRunner()) {
                pipe.Connect(30000);uint server;
                if(!HostWire.GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out server) || server!=parent)throw new InvalidDataException("Неизвестный клиент управления.");
                string installation=Path.GetDirectoryName(HostWire.Image(parent));Trust.NoLinks(installation);
                string snapshot=Path.Combine(HostWire.ProtectedRoot,"runtime-"+Guid.NewGuid().ToString("N"));HostWire.ProtectDirectory(snapshot);
                var snapshots=new List<string>{snapshot};string preparedVersion=null;
                bool done=false;
                runner.Log+=line=>{try{HostWire.Write(pipe,new {type="log",text=line.Length>8192?line.Substring(0,8192):line});}catch{}};
                using(var monitor=new Timer(_=>{try{if(gui.HasExited)pipe.Dispose();}catch{}},null,500,500)) {
                    try {
                        HostWire.Write(pipe,new{type="hello"});
                        while(!done) {
                            var command=HostWire.Read(pipe);string action=(string)command["action"];
                            bool preparing=false;
                            try {
                                switch(action) {
                                    case "startup-enable":
                                    case "startup-sync":
                                    case "start":
                                        preparing=true;
                                        runner.Stop();
                                        string version=(string)command["engine"],strategy=(string)command["strategy"];
                                        if(!System.Text.RegularExpressions.Regex.IsMatch(version,@"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$") || version.Contains(".."))throw new InvalidDataException("Некорректная версия.");
                                        string source=Path.Combine(installation,"data","engines",version);
                                        Dictionary<string,byte[]> inputs=null;
                                        // Read with the caller's token, so the administrator cannot be used to read privileged files.
                                        HostWire.AsCaller(parent,()=>{inputs=ReadInputs(source);});
                                        var startup=StartupService.Read(installation);
                                        if(action=="startup-enable" || action=="startup-sync" || startup.Owned) {
                                            if(action=="startup-sync" && !startup.Owned)throw new InvalidOperationException("Автозапуск не установлен.");
                                            StartupService.Configure(installation,version,strategy,inputs,action!="startup-sync" || startup.Running,line=>HostWire.Write(pipe,new{type="log",text=line}));
                                            preparing=false;break;
                                        }
                                        if(preparedVersion!=null && preparedVersion!=version){snapshot=Path.Combine(HostWire.ProtectedRoot,"runtime-"+Guid.NewGuid().ToString("N"));HostWire.ProtectDirectory(snapshot);snapshots.Add(snapshot);preparedVersion=null;}
                                        WriteSnapshot(snapshot,inputs,preparedVersion!=null);preparedVersion=version;
                                        var driverPaths=RegisteredDriverPaths();
                                        foreach(string driverPath in driverPaths)HostWire.Write(pipe,new{type="log",text="WinDivert: зарегистрированный путь "+driverPath+" · файл "+(File.Exists(NormalizeDriverPath(driverPath))?"есть":"отсутствует")});
                                        foreach(string repaired in RestoreDriverFiles(HostWire.ProtectedRoot,inputs,driverPaths))
                                            HostWire.Write(pipe,new{type="log",text="Восстановлен проверенный драйвер ByPass: "+repaired});
                                        DriverServiceRecovery.Repair(line=>HostWire.Write(pipe,new{type="log",text=line}));
                                        preparing=false;runner.Start(snapshot,strategy);break;
                                    case "startup-disable":runner.Stop();StartupService.Remove(installation);break;
                                    case "stop":runner.Stop();if(StartupService.Read(installation).Owned)StartupService.Stop(installation);break;
                                    case "status":break;
                                    case "shutdown":runner.Stop();done=true;break;
                                    default:throw new InvalidDataException("Неизвестная команда.");
                                }
                                var serviceState=StartupService.Read(installation);
                                HostWire.Write(pipe,new{type="reply",running=serviceState.Owned?serviceState.Running:runner.Running,error=""});
                            }catch(Exception ex){
                                var serviceState=StartupService.Read(installation);
                                        HostWire.Write(pipe,new{type="reply",running=serviceState.Owned?serviceState.Running:runner.Running,error=ex.Message,error_kind=ex is EngineDriverException?"driver":preparing?"setup":"strategy"});}
                        }
                    }finally{runner.Stop();foreach(string folder in snapshots)try{CleanupSnapshot(folder,RegisteredDriverPaths());}catch{}}
                }
            }
        }
        // WinDivert reuses an existing driver's registered ImagePath. Keep that
        // file alive, and repair only a missing, trusted driver in our own runtime.
        internal static string NormalizeDriverPath(string path) {
            path=Environment.ExpandEnvironmentVariables(path??"").Trim().Trim('"');
            if(path.StartsWith(@"\??\",StringComparison.Ordinal))path=path.Substring(4);
            return path;
        }
        static string[] RegisteredDriverPaths() {
            var result=new List<string>();
            using(var services=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services",false)) {
                if(services==null)throw new IOException("Не удалось прочитать пути драйверов Windows.");
                foreach(string name in services.GetSubKeyNames().Where(x=>x.StartsWith("WinDivert",StringComparison.OrdinalIgnoreCase))) {
                    using(var service=services.OpenSubKey(name,false)) {
                        if(service==null)throw new IOException("Не удалось прочитать службу "+name);
                        if(Convert.ToInt32(service.GetValue("Type",0))!=1)continue;
                        string image=service.GetValue("ImagePath",null,Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                        if(!String.IsNullOrEmpty(image))result.Add(image);
                    }
                }
            }
            return result.ToArray();
        }
        internal static string OwnedDriver(string root,string registered) {
            string path=NormalizeDriverPath(registered);
            if(!Path.IsPathRooted(path))return null;
            path=Path.GetFullPath(path);string prefix=Path.GetFullPath(root).TrimEnd('\\')+"\\";
            if(!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))return null;
            string relative=path.Substring(prefix.Length);
            return System.Text.RegularExpressions.Regex.IsMatch(relative,@"^runtime-[0-9a-f]{32}\\bin\\WinDivert(32|64)\.sys$",System.Text.RegularExpressions.RegexOptions.IgnoreCase)?path:null;
        }
        internal static string[] RestoreDriverFiles(string root,Dictionary<string,byte[]> verifiedInputs,IEnumerable<string> registered) {
            var restored=new List<string>();
            foreach(string image in registered) {
                string path=OwnedDriver(root,image);if(path==null || File.Exists(path))continue;
                byte[] bytes;if(!verifiedInputs.TryGetValue("bin/"+Path.GetFileName(path),out bytes))continue;
                Trust.NoLinks(path);
#if TESTING
                Directory.CreateDirectory(Path.GetDirectoryName(path));
#else
                HostWire.ProtectDirectory(root);HostWire.ProtectDirectory(Path.GetDirectoryName(Path.GetDirectoryName(path)));HostWire.ProtectDirectory(Path.GetDirectoryName(path));
#endif
                using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))file.Write(bytes,0,bytes.Length);
#if !TESTING
                HostWire.ProtectFile(path);
#endif
                restored.Add(path);
            }
            return restored.ToArray();
        }
        internal static void CleanupSnapshot(string snapshot,IEnumerable<string> registered) {
            Trust.NoLinks(snapshot);
            var keep=new HashSet<string>(registered.Select(x=>OwnedDriver(Path.GetDirectoryName(snapshot),x)).Where(x=>x!=null),StringComparer.OrdinalIgnoreCase);
            foreach(string file in Trust.Files(snapshot).ToArray())if(!keep.Contains(Path.GetFullPath(file)))try{File.Delete(file);}catch{}
            foreach(string directory in Directory.GetDirectories(snapshot,"*",SearchOption.AllDirectories).OrderByDescending(x=>x.Length))try{Directory.Delete(directory,false);}catch{}
            try{Directory.Delete(snapshot,false);}catch{}
        }
        internal static void WriteSnapshot(string snapshot,Dictionary<string,byte[]> inputs,bool reuse) {
            // WinDivert can keep its driver mapped after winws exits. Reuse the
            // verified immutable files; only user data changes between starts.
            Trust.NoLinks(snapshot);
            if(reuse)Trust.VerifyEngine(snapshot);
            foreach(string path in Trust.Files(snapshot).ToArray()) {
                string relative=path.Substring(Path.GetFullPath(snapshot).TrimEnd('\\').Length+1).Replace('\\','/');
                if(Trust.Mutable(relative) && !inputs.ContainsKey(relative))File.Delete(path);
            }
            foreach(var item in inputs) {
                string path=Path.Combine(snapshot,item.Key);Trust.NoLinks(path);
                if(reuse && !Trust.Mutable(item.Key)) {
                    using(var sha=System.Security.Cryptography.SHA256.Create())
                        if(Trust.Hex(sha.ComputeHash(item.Value))!=Trust.Hash(path))throw new InvalidDataException("Состав ядра изменился. Перезапустите ByPass.");
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllBytes(path,item.Value);
            }
            Trust.VerifyEngine(snapshot);
        }
        internal static Dictionary<string,byte[]> ReadInputs(string source) {
            var entry=Trust.VerifyEngine(source);
            var required=new HashSet<string>(((ArrayList)entry["files"]).Cast<Dictionary<string,object>>().Select(x=>(string)x["path"]),StringComparer.OrdinalIgnoreCase);
            var result=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);long total=0;
            foreach(string path in Trust.Files(source)) {
                string relative=path.Substring(Path.GetFullPath(source).TrimEnd('\\').Length+1).Replace('\\','/');
                if(!required.Contains(relative) && !Trust.Mutable(relative))continue;
                using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    if(file.Length>64*1024*1024 || (total+=file.Length)>200*1024*1024)throw new InvalidDataException("Слишком большие файлы ядра.");
                    var bytes=new byte[file.Length];int offset=0,count;
                    while(offset<bytes.Length && (count=file.Read(bytes,offset,bytes.Length-offset))>0)offset+=count;
                    if(offset!=bytes.Length)throw new IOException("Неполное чтение ядра.");result.Add(relative,bytes);
                }
            }
            return result;
        }
    }
}
