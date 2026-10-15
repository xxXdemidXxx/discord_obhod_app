using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretDesktop {
    public sealed class EngineRunner : IDisposable {
        NamedPipeServerStream pipe;Task reader;string helperHash;Timer observer;
        readonly object gate=new object();readonly AutoResetEvent response=new AutoResetEvent(false);
        Dictionary<string,object> reply;volatile bool running,connected,ready;Exception failure;volatile string driverFailure;
        public event Action<string> Log;
        volatile StartupState startup=new StartupState();
        internal bool StartupEnabled {get{return startup.Owned;}}
        internal string StartupStrategy {get{return startup.Strategy;}}
        internal string StartupEngine {get{return startup.Engine;}}
        internal void RefreshStartup(){if(!Monitor.TryEnter(gate))return;try{startup=StartupService.Read(AppDomain.CurrentDomain.BaseDirectory);}finally{Monitor.Exit(gate);}}
        public bool Running {get{return startup.Owned?startup.Running:running;}}
        public bool Ready {get{return startup.Owned?startup.Running:running && ready;}} public string DriverFailure {get{return driverFailure;}}
#if TESTING
        internal Func<bool> ConflictCheck=null; internal Action<bool,string,string> TestStartupChange=null;
#endif
        public bool Conflict {
            get {
#if TESTING
                if(ConflictCheck!=null)return ConflictCheck();
#endif
                if(running && !startup.Owned)return false;
                var processes=Process.GetProcessesByName("winws");bool found=System.Linq.Enumerable.Any(processes,p=>!startup.Owned || (uint)p.Id!=startup.ProcessId);
                foreach(var process in processes)process.Dispose();return found;
            }
        }
        void Connect() {
            if(connected)return;
            if(pipe!=null)pipe.Dispose();
            if(reader!=null){try{reader.Wait(2000);}catch{}reader=null;}
            string name="ByPass-"+Guid.NewGuid().ToString("N");
            var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),PipeAccessRights.FullControl,AccessControlType.Allow));
            pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,65536,65536,security);
            byte[] bytes;using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("ZapretDesktop.EngineHost")) {
                if(stream==null)throw new InvalidDataException("Отсутствует компонент запуска ядра.");using(var memory=new MemoryStream()){stream.CopyTo(memory);bytes=memory.ToArray();}
            }
            using(var sha=System.Security.Cryptography.SHA256.Create())helperHash=Trust.Hex(sha.ComputeHash(bytes));
            string staging=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ByPass","helper-"+Guid.NewGuid().ToString("N"));
            Trust.NoLinks(staging);Directory.CreateDirectory(staging);string image=Path.Combine(staging,"ByPass.EngineHost.exe");File.WriteAllBytes(image,bytes);
            try {
                using(var locked=new FileStream(image,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    if(Trust.Hash(image)!=helperHash)throw new InvalidDataException("Компонент запуска изменён.");
                    var pending=pipe.BeginWaitForConnection(null,null);
                    using(var bootstrap=Process.Start(new ProcessStartInfo(image,"--bootstrap "+Process.GetCurrentProcess().Id+" "+name){UseShellExecute=true,Verb="runas",WorkingDirectory=staging,WindowStyle=ProcessWindowStyle.Hidden})) {
                        if(!pending.AsyncWaitHandle.WaitOne(45000))throw new IOException("Компонент запуска не ответил. Подтвердите запрос Windows и повторите запуск.");
                        pipe.EndWaitForConnection(pending);
                    }
                    uint peer;if(!HostWire.GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out peer))throw new IOException("Не удалось проверить компонент запуска.");
                    {
                        string actual=HostWire.Image((int)peer),expected=Path.Combine(HostWire.ProtectedRoot,"host-"+helperHash,"ByPass.EngineHost.exe");
                        Trust.NoLinks(actual);
                        if(!String.Equals(actual,expected,StringComparison.OrdinalIgnoreCase) || Trust.Hash(actual)!=helperHash)throw new InvalidDataException("Неизвестный компонент запуска.");
                    }
                    connected=true;failure=null;
                    reader=Task.Run(()=>{try{while(connected){var message=HostWire.Read(pipe);string type=(string)message["type"];if(type=="reply"){reply=message;response.Set();}else if(type=="log"){string line=(string)message["text"];if(EngineDriverException.IsFailure(line))driverFailure=line;if(line.Contains("windivert initialized. capture is started."))ready=true;if(Log!=null)Log(line);}}}catch(Exception ex){failure=ex;connected=false;running=false;response.Set();}});
                    if(observer==null)observer=new Timer(_=>{if(!Monitor.TryEnter(gate))return;try{if(connected)Ask("status",null,null);}catch{running=false;}finally{Monitor.Exit(gate);}},null,800,800);
                }
            }catch{connected=false;pipe.Dispose();pipe=null;throw;}
            finally{try{Directory.Delete(staging,true);}catch{}}
        }
        void Ask(string action,string engine,string strategy) {
            lock(gate) {
                if(!connected)throw new IOException("Компонент запуска отключён.");reply=null;response.Reset();
                HostWire.Write(pipe,new{action=action,engine=engine,strategy=strategy});
                if(!response.WaitOne(45000)){connected=false;pipe.Dispose();running=false;throw new IOException("Компонент запуска не ответил.");}
                if(reply==null)throw new IOException("Связь с компонентом запуска прервана.",failure);
                running=Convert.ToBoolean(reply["running"]);RefreshStartup();if(!running)ready=false;string error=(string)reply["error"];
                if(error.Length>0){object kind;if(reply.TryGetValue("error_kind",out kind) && (string)kind=="driver")throw new EngineDriverException(error);if(reply.TryGetValue("error_kind",out kind) && (string)kind=="setup")throw new EngineSetupException("Не удалось подготовить ядро.\n"+error);throw new EngineStartException(error);}
            }
        }
        public void Start(string engine,string strategy) {
            lock(gate) {
            ready=false;driverFailure=null;
            try {
                Trust.VerifyEngine(engine);
                if(!String.Equals(Path.GetDirectoryName(Path.GetFullPath(engine)),Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data","engines").TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Ядро должно находиться в папке приложения.");
                Connect();
            }catch(Exception ex){throw new EngineSetupException("Не удалось подготовить запуск ядра.\n"+ex.Message,ex);}
            try{Ask("start",Path.GetFileName(engine),strategy);}catch(IOException ex){throw new EngineSetupException("Не удалось связаться с компонентом запуска. Повторите запуск ByPass.\n"+ex.Message,ex);}
            }
        }
        public void Stop(){lock(gate){ready=false;if(StartupEnabled && !connected)Connect();if(connected)Ask("stop",null,null);running=false;}}
        internal void SetStartup(bool enabled,string engine,string strategy) {
            lock(gate){
#if TESTING
                if(TestStartupChange!=null){TestStartupChange(enabled,engine,strategy);RefreshStartup();return;}
#endif
                Connect();Ask(enabled?"startup-enable":"startup-disable",enabled?Path.GetFileName(engine):null,enabled?strategy:null);RefreshStartup();}
        }
        internal void SyncStartup(string engine,string strategy) {
            lock(gate){RefreshStartup();if(!StartupEnabled)return;Connect();Ask("startup-sync",Path.GetFileName(engine),strategy);}
        }
        internal static async Task WaitForReady(Func<bool> running,Func<bool> ready,CancellationToken token,int timeoutMilliseconds=10000,Func<string> driverError=null) {
            var watch=Stopwatch.StartNew();
            while(true) {
                token.ThrowIfCancellationRequested();
                string error=driverError==null?null:driverError();if(!String.IsNullOrEmpty(error))throw new EngineDriverException(error);if(!running()) {
                    // stderr may arrive just after the process exit notification.
                    if(driverError!=null){await Task.Delay(100,token);error=driverError();if(!String.IsNullOrEmpty(error))throw new EngineDriverException(error);}
                    throw new EngineStartException("Ядро завершилось до начала перехвата. Подробности в журнале.");
                }
                if(ready())return;
                if(watch.ElapsedMilliseconds>=timeoutMilliseconds)throw new EngineStartException("Ядро не подтвердило начало перехвата за 10 секунд. Подробности в журнале.");
                await Task.Delay(100,token);
            }
        }
        public void Dispose(){if(observer!=null)observer.Dispose();lock(gate){try{if(connected)Ask("shutdown",null,null);}catch{}connected=false;if(pipe!=null)pipe.Dispose();if(reader!=null)try{reader.Wait(2000);}catch{}}response.Dispose();}
    }
}
