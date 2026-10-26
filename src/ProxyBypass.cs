using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretDesktop {
    internal sealed class ProxyBypass : IDisposable {
        internal const string Version="0.17.3";
        internal const string ArchiveHash="70d2c94147193cb915f9c6eb5144b8d404dacbcfa90bda2383b6b211afafa456";
        internal const string ImageHash="eb53ceeeb981cc6735ac24bb1e51e725280b86630e80fdf19ddc4ee4a5b54ef4";
        internal const string Url="https://github.com/hufrea/byedpi/releases/download/v0.17.3/byedpi-17.3-x86_64-w64.zip";
        internal static readonly string[] Profiles={"split","disorder","tlsrec"};
        Process process;OwnedJob job;FileStream imageLock;DnsOverHttps dns;YouTubeDnsProxy browserProxy,checkProxy;
        CancellationTokenSource lifetime;
        int[] closingPorts=new int[0];
        internal int RawPort {get;private set;}
        internal bool Running {get{var current=process;if(current==null)return false;try{return !current.HasExited;}catch(InvalidOperationException){return false;}}}
        internal bool HasResources {get{return process!=null || browserProxy!=null || checkProxy!=null;}}
        internal int Port {get{if(!Running || browserProxy==null)throw new InvalidOperationException("ByeDPI выключен.");return browserProxy.Port;}}
        internal int CheckPort {get{if(!Running || checkProxy==null)throw new InvalidOperationException("ByeDPI выключен.");return checkProxy.Port;}}
        internal static string Image(string root){return Path.Combine(root,"data","alternatives","byedpi-"+Version,"ciadpi.exe");}
        internal static string Arguments(string profile,int port,ScopeOptions scope) {
            if(!Profiles.Contains(profile) || port<1 || port>65535)throw new InvalidDataException("Неизвестный вариант ByeDPI.");
            return "--ip 127.0.0.1 --port "+port+" --no-udp --proto tls,http --hosts "+Strategy.Quote(":"+ServiceScope.Domains(scope).Replace(',',' '))+" "+
                (profile=="split"?"--split 1+s":profile=="disorder"?"--split 1+s --disorder 3+s":"--tlsrec 1+s")+" --auto=none";
        }
        internal static async Task Ensure(string root,CancellationToken token) {
            string image=Image(root);Trust.NoLinks(image);
            string license=Path.Combine(Path.GetDirectoryName(image),"LICENSE.txt");Trust.NoLinks(license);
            if(!File.Exists(license))using(var input=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("ZapretDesktop.ByeDpiLicense")){
                if(input==null)throw new InvalidDataException("Нет лицензии ByeDPI в сборке.");Directory.CreateDirectory(Path.GetDirectoryName(image));using(var output=new FileStream(license,FileMode.CreateNew,FileAccess.Write,FileShare.Read))input.CopyTo(output);
            }
            if(File.Exists(image)){if(Trust.Hash(image)!=ImageHash)throw new InvalidDataException("ByeDPI изменён. Установите заново проверенный пакет ByPass.");return;}
            string directory=Path.GetDirectoryName(image);Directory.CreateDirectory(directory);string temp=Path.Combine(directory,Guid.NewGuid().ToString("N")+".zip");
            try {
                using(var client=new HttpClient{Timeout=TimeSpan.FromSeconds(45)})using(var response=await client.GetAsync(Url,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false)){
                    response.EnsureSuccessStatusCode();if(response.Content.Headers.ContentLength>1024*1024)throw new IOException("Слишком большой архив ByeDPI.");
                    using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                        byte[] buffer=new byte[8192];int count,total=0;while((count=await input.ReadAsync(buffer,0,buffer.Length,token).ConfigureAwait(false))>0){total+=count;if(total>1024*1024)throw new IOException("Слишком большой архив ByeDPI.");await output.WriteAsync(buffer,0,count,token).ConfigureAwait(false);}
                    }
                }
                if(Trust.Hash(temp)!=ArchiveHash)throw new InvalidDataException("SHA-256 официального архива ByeDPI не совпал.");
                byte[] bytes;using(var archive=ZipFile.OpenRead(temp)){
                    var entries=archive.Entries.Where(x=>x.FullName=="ciadpi.exe").ToArray();if(entries.Length!=1 || entries[0].Length!=129024)throw new InvalidDataException("Неожиданный состав ByeDPI.");
                    using(var input=entries[0].Open())using(var memory=new MemoryStream()){input.CopyTo(memory);bytes=memory.ToArray();}
                }
                using(var hash=System.Security.Cryptography.SHA256.Create())if(Trust.Hex(hash.ComputeHash(bytes))!=ImageHash)throw new InvalidDataException("SHA-256 ByeDPI не совпал.");
                using(var output=new FileStream(image,FileMode.CreateNew,FileAccess.Write,FileShare.Read))output.Write(bytes,0,bytes.Length);
            }finally{if(File.Exists(temp))File.Delete(temp);}
        }
        internal void Start(string root,string profile,ScopeOptions scope,Action<string> log) {
            if(Running)throw new InvalidOperationException("Сначала выключите предыдущий движок.");
            if(HasResources)Stop();
            Ensure(root,CancellationToken.None).GetAwaiter().GetResult();string image=Image(root);
            imageLock=new FileStream(image,FileMode.Open,FileAccess.Read,FileShare.Read);
            try {
                lifetime=new CancellationTokenSource();var cancel=lifetime.Token;
                if(Trust.Hash(image)!=ImageHash)throw new InvalidDataException("ByeDPI изменён перед запуском.");
                int port;var reservation=new TcpListener(IPAddress.Loopback,0);reservation.Start();port=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
                RawPort=port;process=new Process{StartInfo=new ProcessStartInfo(image,Arguments(profile,port,scope)){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(image),RedirectStandardOutput=true,RedirectStandardError=true}};
                process.OutputDataReceived+=(s,e)=>{if(e.Data!=null && log!=null)log("ByeDPI: "+e.Data);};process.ErrorDataReceived+=(s,e)=>{if(e.Data!=null && log!=null)log("ByeDPI: "+e.Data);};
                if(!process.Start())throw new IOException("ByeDPI не запустился.");job=new OwnedJob(process);process.BeginOutputReadLine();process.BeginErrorReadLine();
                var watch=Stopwatch.StartNew();bool listening=false;
                while(Running && watch.ElapsedMilliseconds<5000){try{using(var probe=new TcpClient()){probe.Connect(IPAddress.Loopback,port);probe.ReceiveTimeout=500;var stream=probe.GetStream();stream.Write(new byte[]{5,1,0},0,3);listening=stream.ReadByte()==5 && stream.ReadByte()==0;}if(listening)break;}catch(SocketException){}catch(IOException){}Thread.Sleep(50);}
                if(!listening || !Running)throw new IOException("ByeDPI не подтвердил локальное подключение.");
                dns=new DnsOverHttps();Func<string,IPAddress,int,Task<TcpClient>> route=async(host,address,destination)=>{
                    if(ServiceScope.Allows(host,scope))return await Socks(port,address,destination,cancel).ConfigureAwait(false);
                    var direct=new TcpClient(address.AddressFamily);try{using(cancel.Register(()=>direct.Close())){var pending=direct.ConnectAsync(address,destination);if(await Task.WhenAny(pending,Task.Delay(6000,cancel)).ConfigureAwait(false)!=pending)throw new IOException("Тайм-аут прямого соединения.");await pending.ConfigureAwait(false);return direct;}}catch{direct.Close();throw;}
                };browserProxy=new YouTubeDnsProxy(dns.Resolve,router:route);checkProxy=new YouTubeDnsProxy(dns.Resolve,httpConnect:true,router:route,allowed:host=>ServiceScope.Allows(host,scope));
                if(log!=null)log("ByeDPI "+Version+" включён · только отдельное окно · профиль "+profile+" · WinDivert не используется.");
            }catch{Stop();throw;}
        }
        internal static async Task<TcpClient> Socks(int port,IPAddress address,int destination,CancellationToken token=default(CancellationToken)) {
            var client=new TcpClient();client.ReceiveTimeout=6000;client.SendTimeout=6000;
            try {
                using(token.Register(()=>client.Close())){
                await client.ConnectAsync(IPAddress.Loopback,port).ConfigureAwait(false);var stream=client.GetStream();
                stream.Write(new byte[]{5,1,0},0,3);if(stream.ReadByte()!=5 || stream.ReadByte()!=0)throw new IOException("ByeDPI отклонил SOCKS5.");
                var ip=address.GetAddressBytes();var command=new byte[6+ip.Length];command[0]=5;command[1]=1;command[3]=(byte)(ip.Length==4?1:4);Buffer.BlockCopy(ip,0,command,4,ip.Length);command[command.Length-2]=(byte)(destination>>8);command[command.Length-1]=(byte)destination;stream.Write(command,0,command.Length);
                var header=Read(stream,4);if(header[0]!=5 || header[1]!=0 || header[2]!=0)throw new IOException("ByeDPI не подключился к сайту.");int length=header[3]==1?4:header[3]==4?16:header[3]==3?Read(stream,1)[0]:-1;if(length<0)throw new IOException("Неверный ответ SOCKS5.");Read(stream,length+2);token.ThrowIfCancellationRequested();return client;}
            }catch{client.Close();throw;}
        }
        static byte[] Read(Stream stream,int count){var result=new byte[count];int offset=0;while(offset<count){int read=stream.Read(result,offset,count-offset);if(read==0)throw new EndOfStreamException();offset+=read;}return result;}
        internal void Stop() {
            closingPorts=closingPorts.Concat(new[]{RawPort,browserProxy==null?0:browserProxy.Port,checkProxy==null?0:checkProxy.Port}).Where(x=>x>0).Distinct().ToArray();
            if(lifetime!=null){lifetime.Cancel();lifetime.Dispose();lifetime=null;}
            if(checkProxy!=null){checkProxy.Dispose();checkProxy=null;}
            if(browserProxy!=null){browserProxy.Dispose();browserProxy=null;}if(dns!=null){dns.Dispose();dns=null;}
            if(job!=null){job.Dispose();job=null;}
            if(process!=null){if(!process.HasExited){process.Kill();if(!process.WaitForExit(5000))throw new EngineStopException("ByeDPI ещё не завершился.");}process.Dispose();process=null;}
            if(imageLock!=null){imageLock.Dispose();imageLock=null;}
            var watch=Stopwatch.StartNew();while(closingPorts.Length>0 && System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x=>closingPorts.Contains(x.Port))){if(watch.ElapsedMilliseconds>3000)throw new EngineStopException("Процесс ByeDPI завершён, но закрытие его локальных портов не подтверждено.");Thread.Sleep(50);}closingPorts=new int[0];RawPort=0;
        }
        public void Dispose(){Stop();}
    }
}
