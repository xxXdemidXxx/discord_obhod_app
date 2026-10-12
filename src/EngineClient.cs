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
        Dictionary<string,object> reply;volatile bool running,connected;Exception failure;
        public event Action<string> Log;
        public bool Running {get{return running;}}
        public bool Conflict {get{if(running)return false;var processes=Process.GetProcessesByName("winws");bool found=processes.Length>0;foreach(var process in processes)process.Dispose();return found;}}
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
                    reader=Task.Run(()=>{try{while(connected){var message=HostWire.Read(pipe);string type=(string)message["type"];if(type=="reply"){reply=message;response.Set();}else if(type=="log" && Log!=null)Log((string)message["text"]);}}catch(Exception ex){failure=ex;connected=false;running=false;response.Set();}});
                    if(observer==null)observer=new Timer(_=>{if(!Monitor.TryEnter(gate))return;try{if(connected)Ask("status",null,null);}catch{running=false;}finally{Monitor.Exit(gate);}},null,800,800);
                }
            }catch{connected=false;pipe.Dispose();pipe=null;throw;}
            finally{try{Directory.Delete(staging,true);}catch{}}
        }
        void Ask(string action,string engine,string strategy) {
            lock(gate) {
                if(!connected)throw new IOException("Компонент запуска отключён.");reply=null;response.Reset();
                HostWire.Write(pipe,new{action=action,engine=engine,strategy=strategy});
                if(!response.WaitOne(15000)){connected=false;pipe.Dispose();running=false;throw new IOException("Компонент запуска не ответил.");}
                if(reply==null)throw new IOException("Связь с компонентом запуска прервана.",failure);
                running=Convert.ToBoolean(reply["running"]);string error=(string)reply["error"];
                if(error.Length>0){object kind;if(reply.TryGetValue("error_kind",out kind) && (string)kind=="setup")throw new EngineSetupException("Не удалось подготовить ядро.\n"+error);throw new EngineStartException(error);}
            }
        }
        public void Start(string engine,string strategy) {
            lock(gate) {
            try {
                Trust.VerifyEngine(engine);
                if(!String.Equals(Path.GetDirectoryName(Path.GetFullPath(engine)),Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data","engines").TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Ядро должно находиться в папке приложения.");
                Connect();
            }catch(Exception ex){throw new EngineSetupException("Не удалось подготовить запуск ядра.\n"+ex.Message,ex);}
            try{Ask("start",Path.GetFileName(engine),strategy);}catch(IOException ex){throw new EngineSetupException("Не удалось связаться с компонентом запуска. Повторите запуск ByPass.\n"+ex.Message,ex);}
            }
        }
        public void Stop(){if(connected)Ask("stop",null,null);running=false;}
        public void Dispose(){if(observer!=null)observer.Dispose();lock(gate){try{if(connected)Ask("shutdown",null,null);}catch{}connected=false;if(pipe!=null)pipe.Dispose();if(reader!=null)try{reader.Wait(2000);}catch{}}response.Dispose();}
    }
}
