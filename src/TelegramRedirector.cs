using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretDesktop {
    // A separate WinDivert handle reflects only Telegram TCP into local listeners.
    // Original MTProto ciphertext is carried over authenticated TLS/WebSocket.
    internal sealed class TelegramRedirector : IDisposable {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl,SetLastError=true)] delegate IntPtr Open([MarshalAs(UnmanagedType.LPStr)]string filter,int layer,short priority,ulong flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl,SetLastError=true)] delegate bool Recv(IntPtr handle,byte[] data,uint length,out uint received,byte[] address);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl,SetLastError=true)] delegate bool Send(IntPtr handle,byte[] data,uint length,out uint sent,byte[] address);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl,SetLastError=true)] delegate bool Checksums(byte[] data,uint length,byte[] address,ulong flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl,SetLastError=true)] delegate bool Shutdown(IntPtr handle,int how);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl,SetLastError=true)] delegate bool Close(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl,SetLastError=true)] delegate bool Compile([MarshalAs(UnmanagedType.LPStr)]string filter,int layer,byte[] output,uint length,out IntPtr error,out uint position);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
        [DllImport("kernel32.dll",CharSet=CharSet.Ansi,ExactSpelling=true)] static extern IntPtr GetProcAddress(IntPtr library,string name);
        [DllImport("kernel32.dll")]static extern bool FreeLibrary(IntPtr library);
        readonly CancellationTokenSource cancel=new CancellationTokenSource();readonly List<TcpListener> listeners=new List<TcpListener>();
        readonly Dictionary<int,int> redirects=new Dictionary<int,int>();readonly ConcurrentDictionary<int,byte> upstream=new ConcurrentDictionary<int,byte>();
        readonly ConcurrentDictionary<TcpClient,byte> clients=new ConcurrentDictionary<TcpClient,byte>();readonly Action<string> log;readonly TelegramTelemetry telemetry;
        IntPtr library,handle;Recv recv;Send send;Checksums checksums;Shutdown shutdown;Close close;Task loop;bool disposed;readonly bool relayAllowed;
        internal bool Running {get{return handle!=IntPtr.Zero && loop!=null && !loop.IsCompleted;}}
        T Function<T>(string name){IntPtr address=GetProcAddress(library,name);if(address==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),name);return (T)(object)Marshal.GetDelegateForFunctionPointer(address,typeof(T));}
        internal TelegramRedirector(string engine,Action<string> log){
            this.log=log;telemetry=new TelegramTelemetry(log);relayAllowed=TelegramRelay.Enabled(engine);
            try{
                // Caller has already copied and independently verified this protected core.
                Trust.VerifyEngine(engine);Trust.NoLinks(Path.Combine(engine,"bin","WinDivert.dll"));
                library=LoadLibraryEx(Path.Combine(engine,"bin","WinDivert.dll"),IntPtr.Zero,8);
                if(library==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"Загрузка проверенной WinDivert.dll");
                var open=Function<Open>("WinDivertOpen");recv=Function<Recv>("WinDivertRecv");send=Function<Send>("WinDivertSend");checksums=Function<Checksums>("WinDivertHelperCalcChecksums");shutdown=Function<Shutdown>("WinDivertShutdown");close=Function<Close>("WinDivertClose");
                foreach(int target in new[]{80,443,5222}){
                    var listener=new TcpListener(IPAddress.IPv6Any,0);listener.Server.DualMode=true;listener.Server.ExclusiveAddressUse=true;listener.Start(128);listeners.Add(listener);
                    int port=((IPEndPoint)listener.LocalEndpoint).Port;redirects.Add(port,target);Task.Run(()=>Accept(listener));
                }
                TelegramWire.SocketPort=(port,add)=>{byte ignored;if(add)upstream[port]=0;else upstream.TryRemove(port,out ignored);};
                string filter=Filter(redirects.Keys);
                handle=open(filter,0,200,0);if(handle==new IntPtr(-1)){handle=IntPtr.Zero;throw new Win32Exception(Marshal.GetLastWin32Error(),"Telegram: WinDivert не начал перехват");}
                loop=Task.Factory.StartNew(Capture,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
                if(log!=null)log("Telegram: прозрачный WSS-мост активен · TCP 80/443/5222 · без настройки клиента.");
                if(log!=null)log(relayAllowed?"Telegram: резерв Flowseal/Cloudflare включён для медиа и CDN; MTProto остаётся зашифрованным.":"Telegram: внешний резерв выключен; заблокированный CDN может быть недоступен.");
                var check=Task.Run(async()=>{try{using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token)){deadline.CancelAfter(12000);await TelegramWire.Warm(deadline.Token,telemetry.Event).ConfigureAwait(false);}}catch(Exception ex){if(!cancel.IsCancellationRequested)telemetry.Failure(ex);}});
            }catch{Dispose();throw;}
        }
        internal static string Filter(IEnumerable<int> ports){
            return "outbound and !loopback and tcp and (("+String.Join(" or ",ports.Select(x=>"tcp.SrcPort == "+x))+") or ((tcp.DstPort == 80 or tcp.DstPort == 443 or tcp.DstPort == 5222) and ("+TelegramFilter.AddressFilter("Dst")+")))";
        }
        // Pure WinDivert user-mode parser; no WinDivertOpen or driver installation.
        internal static void ValidateFilter(string dll,string filter=null){
            IntPtr library=LoadLibraryEx(Path.GetFullPath(dll),IntPtr.Zero,8);if(library==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            try{var compile=(Compile)Marshal.GetDelegateForFunctionPointer(GetProcAddress(library,"WinDivertHelperCompileFilter"),typeof(Compile));IntPtr error;uint position;if(!compile(filter??Filter(new[]{49150,49151,49152}),0,new byte[65536],65536,out error,out position))throw new InvalidDataException("WinDivert filter: "+Marshal.PtrToStringAnsi(error)+" at "+position);}finally{FreeLibrary(library);}
        }
        async Task Accept(TcpListener listener){
            while(!cancel.IsCancellationRequested){TcpClient client;try{client=await listener.AcceptTcpClientAsync().ConfigureAwait(false);}catch(ObjectDisposedException){break;}catch(SocketException){break;}
                if(clients.Count>=128){client.Close();continue;}clients[client]=0;var operation=Handle(client);
            }
        }
        async Task Handle(TcpClient client){
            int dc=0;bool media=false,opened=false;
            try{
                var endpoint=(IPEndPoint)client.Client.RemoteEndPoint;
                if(!TelegramFilter.Contains(endpoint.Address))return;
                client.NoDelay=true;using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token))using(deadline.Token.Register(()=>client.Close())){
                    deadline.CancelAfter(10000);var stream=client.GetStream();byte[] header=new byte[64];await TelegramWire.Exact(stream,header,deadline.Token).ConfigureAwait(false);
                    try{dc=TelegramWire.Dc(header,out media);}catch(InvalidDataException){media=false;}
                    telemetry.Open(dc,media);opened=true;
                    deadline.CancelAfter(Timeout.Infinite);
                    if(dc!=0 && (dc!=203 || relayAllowed))await TelegramWire.Relay(stream,header,dc,media,deadline.Token,telemetry.Event,telemetry,(id,files,token)=>TelegramWire.Connect(id,files,token,telemetry.Event,relayAllowed)).ConfigureAwait(false);
                    else await Passthrough(client,stream,header,endpoint.Address,deadline.Token).ConfigureAwait(false);
                }
            }catch(Exception ex){if(!cancel.IsCancellationRequested)telemetry.Failure(ex);}
            finally{if(opened)telemetry.Close(dc,media);byte ignored;clients.TryRemove(client,out ignored);client.Close();}
        }
        async Task Passthrough(TcpClient client,Stream stream,byte[] header,IPAddress target,CancellationToken token){
            if(target.IsIPv4MappedToIPv6)target=target.MapToIPv4();int destination=redirects[((IPEndPoint)client.Client.LocalEndPoint).Port];
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token)){
                deadline.CancelAfter(10000);using(var remote=await TelegramWire.Dial(target,destination,deadline.Token).ConfigureAwait(false))using(deadline.Token.Register(()=>remote.Dispose())){
                    deadline.CancelAfter(Timeout.Infinite);await remote.WriteAsync(header,0,header.Length,deadline.Token).ConfigureAwait(false);var up=stream.CopyToAsync(remote,65536,deadline.Token);var down=remote.CopyToAsync(stream,65536,deadline.Token);Task first=await Task.WhenAny(up,down).ConfigureAwait(false);Exception failure=first.IsFaulted?first.Exception.GetBaseException():null;deadline.Cancel();client.Close();remote.Dispose();try{await Task.WhenAll(up,down).ConfigureAwait(false);}catch(OperationCanceledException){}catch(IOException){}catch(ObjectDisposedException){}if(failure!=null)throw failure;
                }
            }
        }
        void Capture(){
            byte[] packet=new byte[65575],address=new byte[80];
            try{
                while(!cancel.IsCancellationRequested){uint length;if(!recv(handle,packet,(uint)packet.Length,out length,address)){if(cancel.IsCancellationRequested)break;throw new Win32Exception(Marshal.GetLastWin32Error(),"Telegram WinDivertRecv");}
                    if(Reflect(packet,(int)length,address,redirects,port=>upstream.ContainsKey(port)))checksums(packet,length,address,0);
                    uint sent;if(!send(handle,packet,length,out sent,address) && !cancel.IsCancellationRequested)throw new Win32Exception(Marshal.GetLastWin32Error(),"Telegram WinDivertSend");
                }
            }catch(Exception ex){if(!cancel.IsCancellationRequested && log!=null)log("Telegram: ошибка перехвата · "+ex.Message);var failed=Interlocked.Exchange(ref handle,IntPtr.Zero);if(failed!=IntPtr.Zero)close(failed);}
        }
        internal static bool Reflect(byte[] packet,int length,byte[] address,IDictionary<int,int> routes,Func<int,bool> exempt){
            int tcp,src,dst,size;if(!Packet(packet,length,out tcp,out src,out dst,out size))return false;
            int source=Port(packet,tcp),destination=Port(packet,tcp+2),original;
            if(routes.TryGetValue(source,out original))PutPort(packet,tcp,original);
            else{if(exempt(source))return false;var proxy=routes.FirstOrDefault(x=>x.Value==destination);if(proxy.Key==0)return false;PutPort(packet,tcp+2,proxy.Key);}
            Swap(packet,src,dst,size);address[10]&=0xfd;return true;
        }
        internal static int Port(byte[] packet,int at){return packet[at]*256+packet[at+1];}
        internal static void PutPort(byte[] packet,int at,int port){packet[at]=(byte)(port>>8);packet[at+1]=(byte)port;}
        internal static void Swap(byte[] packet,int a,int b,int length){for(int i=0;i<length;i++){byte value=packet[a+i];packet[a+i]=packet[b+i];packet[b+i]=value;}}
        internal static bool Packet(byte[] data,int length,out int tcp,out int src,out int dst,out int size){
            tcp=src=dst=size=0;if(length<40)return false;
            if(data[0]>>4==4){tcp=(data[0]&15)*4;src=12;dst=16;size=4;if(tcp<20 || length<tcp+20 || data[9]!=6 || (data[6]&0x3f)!=0 || data[7]!=0)return false;}
            else if(data[0]>>4==6){src=8;dst=24;size=16;tcp=40;int next=data[6];while(next==0 || next==43 || next==60){if(tcp+2>length)return false;int step=(data[tcp+1]+1)*8;next=data[tcp];tcp+=step;if(tcp>length)return false;}if(next!=6 || tcp+20>length)return false;}
            else return false;return (data[tcp+12]>>4)*4>=20 && tcp+(data[tcp+12]>>4)*4<=length;
        }
        public void Dispose(){
            if(disposed)return;disposed=true;cancel.Cancel();foreach(var listener in listeners)listener.Stop();foreach(var client in clients.Keys)client.Close();
            TelegramWire.SocketPort=null;
            if(handle!=IntPtr.Zero){shutdown(handle,1);if(loop!=null)loop.Wait(3000);close(handle);handle=IntPtr.Zero;}
            // Do not unload while a native receive is outstanding.
            if(library!=IntPtr.Zero && (loop==null || loop.IsCompleted)){FreeLibrary(library);library=IntPtr.Zero;}
            telemetry.Dispose();
            cancel.Dispose();
        }
    }
}
