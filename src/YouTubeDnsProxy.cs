using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretDesktop {
    // Only this local browser session uses the listener. No Windows proxy or
    // adapter settings are touched; HTTPS remains end-to-end in the browser.
    internal sealed class YouTubeDnsProxy : IDisposable {
        readonly TcpListener listener;
        readonly Func<string,CancellationToken,Task<DnsAnswer>> resolve;
        readonly Func<IPAddress,int,Task<TcpClient>> connect;
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        readonly HashSet<TcpClient> clients=new HashSet<TcpClient>();
        readonly Dictionary<string,CacheEntry> cache=new Dictionary<string,CacheEntry>();
        readonly SemaphoreSlim slots=new SemaphoreSlim(96);
        readonly Task accept;
        int disposed;
        sealed class CacheEntry {internal DnsAnswer Answer;internal DateTime Expires;}
        internal IPAddress Address {get{return ((IPEndPoint)listener.LocalEndpoint).Address;}}
        internal int Port {get{return ((IPEndPoint)listener.LocalEndpoint).Port;}}
        internal YouTubeDnsProxy(Func<string,CancellationToken,Task<DnsAnswer>> resolver,Func<IPAddress,int,Task<TcpClient>> connector=null) {
            resolve=resolver;connect=connector??Connect;
            listener=new TcpListener(IPAddress.Loopback,0);listener.Start(96);accept=Accept();
        }
        async Task Accept() {
            while(!lifetime.IsCancellationRequested) {
                TcpClient client;
                try{client=await listener.AcceptTcpClientAsync().ConfigureAwait(false);}catch(ObjectDisposedException){break;}catch(SocketException){if(lifetime.IsCancellationRequested)break;throw;}
                if(!slots.Wait(0)){client.Close();continue;}
                lock(clients){if(lifetime.IsCancellationRequested){client.Close();slots.Release();break;}clients.Add(client);}
                Observe(Task.Run(()=>Serve(client)));
            }
        }
        static void Observe(Task task){task.ContinueWith(t=>{var ignored=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);}
        static async Task<TcpClient> Connect(IPAddress address,int port) {
            var remote=new TcpClient(AddressFamily.InterNetwork);
            try {
                var pending=remote.ConnectAsync(address,port);
                if(await Task.WhenAny(pending,Task.Delay(6000)).ConfigureAwait(false)!=pending){Observe(pending);throw new IOException("Соединение не установлено за 6 секунд.");}
                await pending.ConfigureAwait(false);return remote;
            }catch{remote.Close();throw;}
        }
        static byte[] Read(Stream stream,int length) {
            var data=new byte[length];int offset=0;
            while(offset<length){int count=stream.Read(data,offset,length-offset);if(count==0)throw new EndOfStreamException();offset+=count;}return data;
        }
        internal static string Destination(Stream stream,out int port) {
            var header=Read(stream,4);
            if(header[0]!=5 || header[1]!=1 || header[2]!=0 || header[3]!=3)throw new IOException("Поддерживается только SOCKS5 CONNECT с доменным именем.");
            int length=Read(stream,1)[0];if(length==0)throw new IOException("Пустое имя сервера.");
            string host=DnsOverHttps.Host(System.Text.Encoding.ASCII.GetString(Read(stream,length)));
            var bytes=Read(stream,2);port=bytes[0]*256+bytes[1];
            if(port!=443 && port!=80)throw new IOException("Разрешены только веб-соединения.");
            return host;
        }
        async Task<DnsAnswer> Resolve(string host) {
            lock(cache){CacheEntry entry;if(cache.TryGetValue(host,out entry) && entry.Expires>DateTime.UtcNow)return entry.Answer;}
            var answer=await resolve(host,lifetime.Token).ConfigureAwait(false);
            if(answer==null || answer.Addresses.Length==0 || answer.Addresses.Any(x=>!DnsOverHttps.Public(x)))throw new IOException("DNS не вернул публичный адрес.");
            if(answer.Ttl>0)lock(cache){if(cache.Count>=512)cache.Clear();cache[host]=new CacheEntry{Answer=answer,Expires=DateTime.UtcNow.AddSeconds(Math.Min(answer.Ttl,300))};}
            return answer;
        }
        async Task Serve(TcpClient client) {
            TcpClient remote=null;
            try {
                client.ReceiveTimeout=10000;client.SendTimeout=10000;client.NoDelay=true;
                var local=client.GetStream();var hello=Read(local,2);
                if(hello[0]!=5 || hello[1]==0)throw new IOException("Неверная версия SOCKS.");
                var methods=Read(local,hello[1]);bool accepted=methods.Contains((byte)0);local.Write(new byte[]{5,accepted?(byte)0:(byte)255},0,2);if(!accepted)return;
                int port;string host=Destination(local,out port);
                var answer=await Resolve(host).ConfigureAwait(false);
                foreach(var address in answer.Addresses.Take(3)) {
                    lifetime.Token.ThrowIfCancellationRequested();
                    try{remote=await connect(address,port).ConfigureAwait(false);break;}catch(SocketException){}catch(IOException){}
                }
                if(remote==null)throw new IOException("Не удалось подключиться к сайту.");
                lock(clients){if(lifetime.IsCancellationRequested)throw new OperationCanceledException();clients.Add(remote);}
                local.Write(new byte[]{5,0,0,1,127,0,0,1,0,0},0,10);
                var upstream=remote.GetStream();var send=local.CopyToAsync(upstream,65536,lifetime.Token);var receive=upstream.CopyToAsync(local,65536,lifetime.Token);
                await Task.WhenAny(send,receive).ConfigureAwait(false);Observe(send);Observe(receive);
            }catch(Exception ex) {
                if(!(ex is IOException) && !(ex is SocketException) && !(ex is OperationCanceledException) && !(ex is ObjectDisposedException))System.Diagnostics.Trace.TraceError(ex.ToString());
                try{client.GetStream().Write(new byte[]{5,1,0,1,0,0,0,0,0,0},0,10);}catch{}
            }finally {
                client.Close();if(remote!=null)remote.Close();
                lock(clients){clients.Remove(client);if(remote!=null)clients.Remove(remote);}slots.Release();
            }
        }
        public void Dispose() {
            if(Interlocked.Exchange(ref disposed,1)!=0)return;
            lifetime.Cancel();listener.Stop();lock(clients){foreach(var client in clients)client.Close();}
            Observe(accept);
        }
    }
}
