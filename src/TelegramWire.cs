using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretDesktop {
    // MTProto transport obfuscation only. Account keys and message encryption
    // stay inside Telegram. The relay forwards the original stream unchanged.
    internal sealed class TelegramCtr : IDisposable {
        readonly Aes aes;readonly ICryptoTransform encrypt;readonly byte[] counter,pad=new byte[16];int offset=16;
        internal TelegramCtr(byte[] key,byte[] iv){aes=Aes.Create();aes.Mode=CipherMode.ECB;aes.Padding=PaddingMode.None;aes.Key=key;encrypt=aes.CreateEncryptor();counter=(byte[])iv.Clone();}
        internal byte[] Transform(byte[] input){var output=new byte[input.Length];for(int i=0;i<input.Length;i++){if(offset==16){encrypt.TransformBlock(counter,0,16,pad,0);for(int j=15;j>=0 && ++counter[j]==0;j--){}offset=0;}output[i]=(byte)(input[i]^pad[offset++]);}return output;}
        public void Dispose(){encrypt.Dispose();aes.Dispose();Array.Clear(counter,0,counter.Length);Array.Clear(pad,0,pad.Length);}
    }
    internal static class TelegramWire {
        internal static Action<int,bool> SocketPort=null;
        internal static byte[] Random(int length){var bytes=new byte[length];using(var random=RandomNumberGenerator.Create())random.GetBytes(bytes);return bytes;}
        internal static bool KnownDc(int dc){return dc>=1 && dc<=5 || dc==203;}
        internal static Uri Endpoint(int dc,bool media=false){if(dc<1 || dc>5)throw new InvalidDataException("У этого DC нет штатного WSS-имени Telegram.");return new Uri("wss://kws"+dc+(media?"-1":"")+".web.telegram.org/apiws");}
        internal static readonly string[] RelayDomains={"pclead.co.uk","offshor.co.uk","cakeisalie.co.uk","noskomnadzor.co.uk"};
        internal static Uri RelayEndpoint(int dc,string domain){if(!KnownDc(dc) || !RelayDomains.Contains(domain))throw new InvalidDataException("Недопустимый резервный маршрут Telegram.");return new Uri("wss://kws"+dc+"."+domain+"/apiws");}
        internal static TelegramCtr Outgoing(byte[] init){return new TelegramCtr(init.Skip(8).Take(32).ToArray(),init.Skip(40).Take(16).ToArray());}
        internal static TelegramCtr Incoming(byte[] init){byte[] reverse=init.Skip(8).Take(48).Reverse().ToArray();return new TelegramCtr(reverse.Take(32).ToArray(),reverse.Skip(32).ToArray());}
        internal static int Dc(byte[] header,out bool media){
            if(header.Length!=64)throw new InvalidDataException("Неполный заголовок MTProto.");
            byte[] plain;using(var ctr=Outgoing(header))plain=ctr.Transform(header);
            uint protocol=BitConverter.ToUInt32(plain,56);
            if(protocol!=0xefefefef && protocol!=0xeeeeeeee && protocol!=0xdddddddd)throw new InvalidDataException("Соединение не является прямым obfuscated MTProto. Прокси в Telegram должен быть выключен.");
            int id=BitConverter.ToInt16(plain,60);media=id<0;id=Math.Abs(id);
            // CDN203 has its own auth keys and needs a dedicated relay route.
            // Preserve its signed DC id in the original header.
            // Test DCs must never be silently sent into the production cloud.
            if(!KnownDc(id))throw new InvalidDataException("Неизвестный DC в MTProto: "+id);
            return id;
        }
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string,IPAddress[]> resolved=new System.Collections.Concurrent.ConcurrentDictionary<string,IPAddress[]>();
        internal static async Task<T> Bounded<T>(Task<T> task,CancellationToken token){
            if(task.IsCompleted)return await task.ConfigureAwait(false);
            var cancelled=new TaskCompletionSource<bool>();using(token.Register(()=>cancelled.TrySetResult(true))){
                if(await Task.WhenAny(task,cancelled.Task).ConfigureAwait(false)!=task){var observation=task.ContinueWith(f=>{var ignored=f.Exception;},TaskContinuationOptions.OnlyOnFaulted);token.ThrowIfCancellationRequested();throw new OperationCanceledException(token);}
                return await task.ConfigureAwait(false);
            }
        }
        internal static async Task<Stream> Dial(IPAddress address,int destination,CancellationToken token,bool tracked=true){
            var socket=new Socket(address.AddressFamily,SocketType.Stream,ProtocolType.Tcp);int port=0;Action<int,bool> tracking=tracked?SocketPort:null;
            try{
                socket.Bind(new IPEndPoint(address.AddressFamily==AddressFamily.InterNetwork?IPAddress.Any:IPAddress.IPv6Any,0));port=((IPEndPoint)socket.LocalEndPoint).Port;socket.NoDelay=true;if(tracking!=null)tracking(port,true);
                using(token.Register(()=>socket.Close())){
                    // Capture the Socket itself. TcpClient.EndConnect can dereference
                    // Client=null when its cancellation callback closes the client.
                    var pending=Task.Factory.FromAsync((callback,state)=>socket.BeginConnect(new IPEndPoint(address,destination),callback,state),socket.EndConnect,null);
                    await Bounded(pending.ContinueWith(t=>{t.GetAwaiter().GetResult();return true;},TaskContinuationOptions.ExecuteSynchronously),token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if(((IPEndPoint)socket.LocalEndPoint).Port!=port)throw new IOException("Исходный порт сокета изменился после регистрации.");
                    return new TelegramSocketStream(new NetworkStream(socket,true),socket,port,tracking);
                }
            }catch{socket.Close();if(tracking!=null && port!=0)tracking(port,false);if(token.IsCancellationRequested)throw new OperationCanceledException(token);throw;}
        }
        internal static IPAddress[] Routes(int dc,IPAddress[] addresses,bool media=false){
            Endpoint(dc);var routes=new System.Collections.Generic.List<IPAddress>();
            // Telegram's alternate web endpoint used for DC2/DC4 by the
            // upstream TG WS bridge. TLS SNI/certificate remain the actual DC.
            if(dc==4 || dc==2 && !media)routes.Add(IPAddress.Parse("149.154.167.220"));
            routes.AddRange(addresses.Where(x=>x.AddressFamily==AddressFamily.InterNetwork));
            // Shared DC2 redirect can accept resPQ but fail media sessions on
            // non-Premium accounts. Try its native WS servers first for files.
            if(dc==2 && media)routes.Add(IPAddress.Parse("149.154.167.220"));
            return routes.Distinct().Take(4).ToArray();
        }
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string,DateTime> dnsUntil=new System.Collections.Concurrent.ConcurrentDictionary<string,DateTime>();
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string,DateTime> failedUntil=new System.Collections.Concurrent.ConcurrentDictionary<string,DateTime>();
        internal static async Task<WebSocket> Connect(int dc,bool media,CancellationToken token,Action<string> log=null,bool allowRelay=false){
            if(!KnownDc(dc))throw new InvalidDataException("Неизвестный Telegram DC.");
            // CDN203 is a different server/key space, not ordinary DC2 with a
            // different handshake id. Never report DC2 resPQ as CDN success.
            if(dc==203 || media && dc==2 && allowRelay){
                if(!allowRelay)throw new IOException("CDN203: требуется резервный маршрут или доступный прямой TCP.");
                try{return await ConnectRelay(dc,token,log).ConfigureAwait(false);}catch(IOException){token.ThrowIfCancellationRequested();if(dc==203)throw;}
            }
            if(allowRelay){try{return await ConnectEndpoint(dc,media,token,log).ConfigureAwait(false);}catch(IOException){token.ThrowIfCancellationRequested();}return await ConnectRelay(dc,token,log).ConfigureAwait(false);}
            return await ConnectEndpoint(dc,media,token,log).ConfigureAwait(false);
        }
        internal static bool PublicRelayAddress(IPAddress address){if(address.AddressFamily!=AddressFamily.InterNetwork)return false;var b=address.GetAddressBytes();return b[0]!=0 && b[0]!=10 && b[0]!=127 && b[0]<224 && !(b[0]==169 && b[1]==254) && !(b[0]==172 && b[1]>=16 && b[1]<=31) && !(b[0]==192 && (b[1]==168 || b[1]==0)) && !(b[0]==100 && b[1]>=64 && b[1]<=127) && !(b[0]==198 && (b[1]==18 || b[1]==19));}
        internal static async Task<WebSocket> ConnectRelay(int dc,CancellationToken token,Action<string> log=null){
            foreach(string domain in RelayDomains){
                var uri=RelayEndpoint(dc,domain);IPAddress[] addresses;DateTime retry;
                if(failedUntil.TryGetValue(uri.Host,out retry) && retry>DateTime.UtcNow)continue;
                using(var attempt=CancellationTokenSource.CreateLinkedTokenSource(token)){attempt.CancelAfter(2500);
                    try{addresses=await Bounded(Dns.GetHostAddressesAsync(uri.Host),attempt.Token).ConfigureAwait(false);
                        foreach(var address in addresses.Where(PublicRelayAddress).Take(2)){var socket=await ConnectUri(dc,uri,address,attempt.Token).ConfigureAwait(false);if(log!=null)log("Telegram: резерв DC"+dc+" · "+uri.Host+" · TLS/WSS подтверждены");return socket;}
                    }catch(Exception ex){if(!(ex is IOException) && !(ex is OperationCanceledException) && !(ex is SocketException))throw;token.ThrowIfCancellationRequested();}
                    failedUntil[uri.Host]=DateTime.UtcNow.AddSeconds(30);
                }
            }
            throw new IOException("Telegram DC"+dc+": резервные WSS-маршруты недоступны.");
        }
        static async Task<WebSocket> ConnectEndpoint(int dc,bool media,CancellationToken token,Action<string> log,bool suffix=true){
            var uri=Endpoint(dc,media && suffix);IPAddress[] addresses;string dnsError="";DateTime expires;
            if(!resolved.TryGetValue(uri.Host,out addresses) || !dnsUntil.TryGetValue(uri.Host,out expires) || expires<DateTime.UtcNow){
                try{using(var lookup=CancellationTokenSource.CreateLinkedTokenSource(token)){lookup.CancelAfter(2500);addresses=await Bounded(Dns.GetHostAddressesAsync(uri.Host),lookup.Token).ConfigureAwait(false);resolved[uri.Host]=addresses;dnsUntil[uri.Host]=DateTime.UtcNow.AddMinutes(2);}}
                catch(Exception ex){token.ThrowIfCancellationRequested();if(!(ex is SocketException) && !(ex is OperationCanceledException))throw;addresses=new IPAddress[0];dnsError="DNS: "+ex.Message;}
            }
            var errors=new System.Collections.Generic.List<string>();if(dnsError.Length>0)errors.Add(dnsError);
            foreach(var address in Routes(dc,addresses,media)){
                token.ThrowIfCancellationRequested();
                string route=uri.Host+"/"+address;DateTime retry;if(failedUntil.TryGetValue(route,out retry) && retry>DateTime.UtcNow)continue;
                using(var attempt=CancellationTokenSource.CreateLinkedTokenSource(token)){
                    attempt.CancelAfter(3500);var watch=System.Diagnostics.Stopwatch.StartNew();
                    try{var socket=await ConnectAt(dc,media && suffix,address,attempt.Token).ConfigureAwait(false);failedUntil.TryRemove(route,out retry);if(log!=null)log("Telegram: WSS DC"+dc+(media?" media":"")+" · "+uri.Host+" · "+address+" · TLS/upgrade подтверждены");return socket;}
                    catch(Exception ex){if(!(ex is IOException) && !(ex is OperationCanceledException))throw;token.ThrowIfCancellationRequested();failedUntil[route]=DateTime.UtcNow.AddSeconds(15);string error=ex.Message;errors.Add(error);if(log!=null)log("Telegram: маршрут · "+error);}
                }
            }
            IPAddress[] removed;resolved.TryRemove(uri.Host,out removed);token.ThrowIfCancellationRequested();
            if(media && suffix)return await ConnectEndpoint(dc,media,token,log,false).ConfigureAwait(false);
            throw new IOException("Telegram DC"+dc+": нет WSS-маршрута · "+(errors.Count==0?"нет IPv4 адресов":String.Join(" | ",errors)));
        }
        internal static async Task<WebSocket> ConnectAt(int dc,bool media,IPAddress address,CancellationToken token){
            return await ConnectUri(dc,Endpoint(dc,media),address,token).ConfigureAwait(false);
        }
        internal static async Task<WebSocket> ConnectUri(int dc,Uri uri,IPAddress address,CancellationToken token){
            Stream transport=null;string stage="TCP";
            try{
                transport=await Dial(address,443,token).ConfigureAwait(false);
                using(token.Register(()=>transport.Dispose())){
                    stage="TLS";var ssl=new SslStream(transport,false);
                    await Bounded(ssl.AuthenticateAsClientAsync(uri.Host,null,SslProtocols.Tls12,false).ContinueWith(t=>{t.GetAwaiter().GetResult();return true;},TaskContinuationOptions.ExecuteSynchronously),token).ConfigureAwait(false);
                    stage="WebSocket upgrade";string key=Convert.ToBase64String(Random(16));
                    byte[] request=Encoding.ASCII.GetBytes("GET "+uri.PathAndQuery+" HTTP/1.1\r\nHost: "+uri.Host+"\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: "+key+"\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: binary\r\n\r\n");
                    await ssl.WriteAsync(request,0,request.Length,token).ConfigureAwait(false);
                    var header=new StringBuilder();var one=new byte[1];while(header.Length<16384){await Exact(ssl,one,token).ConfigureAwait(false);header.Append((char)one[0]);if(header.ToString().EndsWith("\r\n\r\n"))break;}
                    string response=header.ToString();string accept;using(var sha=SHA1.Create())accept=Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key+"258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                    if(!response.StartsWith("HTTP/1.1 101 ") || !response.Split(new[]{"\r\n"},StringSplitOptions.None).Any(x=>x.Equals("Sec-WebSocket-Accept: "+accept,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Handshake: "+response.Split('\r')[0]);
                    return WebSocket.CreateClientWebSocket(ssl,"binary",65536,65536,TimeSpan.FromSeconds(25),false,WebSocket.CreateClientBuffer(65536,65536));
                }
            }catch(Exception ex){if(transport!=null)transport.Dispose();if(token.IsCancellationRequested)throw new OperationCanceledException("DC"+dc+" "+address+" "+stage+": тайм-аут или отмена",ex,token);throw new IOException("DC"+dc+" "+address+" "+stage+": "+ex.Message,ex);}
        }
        static DateTime warmedUntil=DateTime.MinValue;static int warmedPassed;
        internal static async Task<int> Warm(CancellationToken token,Action<string> log,Func<int,CancellationToken,Task<string>> probe=null){
            if(probe==null && DateTime.UtcNow<warmedUntil)return warmedPassed;
            resolved.Clear();
            if(log!=null)log("Telegram: фоновая проверка WSS · запуск основной стратегии не блокируется.");
            var results=await Task.WhenAll(Enumerable.Range(1,5).Select(async dc=>{try{return await Bounded(probe==null?Probe(dc,token,log):probe(dc,token),token).ConfigureAwait(false);}catch(Exception ex){return "DC"+dc+": "+ex.Message;}})).ConfigureAwait(false);
            int passed=results.Count(x=>x.Contains("nonce подтверждён"));if(log!=null)foreach(var result in results)log("Telegram preflight: "+result);
            if(log!=null)log("Telegram: WSS-проверка "+passed+" / 5 DC"+(passed==0?" · доступ Telegram не подтверждён; основная стратегия продолжает работать.":" · resPQ/nonce подтверждены."));
            if(passed>0 && probe==null){warmedPassed=passed;warmedUntil=DateTime.UtcNow.AddMinutes(5);}return passed;
        }
        internal static async Task Exact(Stream stream,byte[] data,CancellationToken token){int offset=0;while(offset<data.Length){int n=await Bounded(stream.ReadAsync(data,offset,data.Length-offset,token),token).ConfigureAwait(false);if(n==0)throw new EndOfStreamException();offset+=n;}}
        internal static async Task Relay(Stream client,byte[] header,int dc,bool media,CancellationToken token,Action<string> log,TelegramTelemetry telemetry=null,Func<int,bool,CancellationToken,Task<WebSocket>> connect=null){
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token))using(deadline.Token.Register(()=>client.Dispose())){
                deadline.CancelAfter(12000);
                using(var ws=await (connect==null?Connect(dc,media,deadline.Token,log):connect(dc,media,deadline.Token)).ConfigureAwait(false)){
                    await ws.SendAsync(new ArraySegment<byte>(header),WebSocketMessageType.Binary,true,deadline.Token).ConfigureAwait(false);
                    deadline.CancelAfter(Timeout.Infinite);
                    // WS messages are a byte stream per Telegram transport docs.
                    // Forward fragments immediately; never buffer an entire video.
                    var up=Task.Run(async()=>{byte[] bytes=new byte[64*1024];while(true){int n=await client.ReadAsync(bytes,0,bytes.Length,deadline.Token).ConfigureAwait(false);if(n==0)break;await ws.SendAsync(new ArraySegment<byte>(bytes,0,n),WebSocketMessageType.Binary,true,deadline.Token).ConfigureAwait(false);if(telemetry!=null)telemetry.Transfer(n,0);}});
                    var down=Task.Run(async()=>{byte[] bytes=new byte[64*1024];while(true){var result=await ws.ReceiveAsync(new ArraySegment<byte>(bytes),deadline.Token).ConfigureAwait(false);if(result.MessageType==WebSocketMessageType.Close)break;if(result.MessageType!=WebSocketMessageType.Binary)throw new InvalidDataException("Telegram вернул не binary WSS.");await client.WriteAsync(bytes,0,result.Count,deadline.Token).ConfigureAwait(false);if(telemetry!=null)telemetry.Transfer(0,result.Count);}});
                    Task first=await Task.WhenAny(up,down).ConfigureAwait(false);Exception failure=first.IsFaulted?first.Exception.GetBaseException():null;
                    deadline.Cancel();ws.Abort();client.Dispose();
                    try{await Task.WhenAll(up,down).ConfigureAwait(false);}catch(OperationCanceledException){}catch(ObjectDisposedException){}catch(WebSocketException){}catch(IOException){}
                    if(failure!=null)throw failure;
                }
            }
        }
        // Actual unauthenticated MTProto req_pq_multi/resPQ with nonce checking.
        // This is a transport probe, never a logged-in message send.
        internal static async Task<string> Probe(int dc,CancellationToken token,Action<string> log=null,bool media=false,bool allowRelay=false){
            using(var ws=await Connect(dc,media,token,log,allowRelay).ConfigureAwait(false)){
                string result=await Exchange(media?-dc:dc,async bytes=>await ws.SendAsync(new ArraySegment<byte>(bytes),WebSocketMessageType.Binary,true,token).ConfigureAwait(false),async()=>{byte[] data=new byte[4096];var frame=await ws.ReceiveAsync(new ArraySegment<byte>(data),token).ConfigureAwait(false);if(frame.MessageType!=WebSocketMessageType.Binary)throw new InvalidDataException("WSS закрыт без resPQ.");return data.Take(frame.Count).ToArray();}).ConfigureAwait(false);
                ws.Abort();return "WSS + "+result;
            }
        }
        internal static readonly string[] Addresses={"149.154.175.50","149.154.167.51","149.154.175.100","149.154.167.91","149.154.171.5"};
        internal static async Task<string> ProbeTcp(int dc,CancellationToken token,bool media=false){
            if(!KnownDc(dc))throw new InvalidDataException("Неизвестный DC пробы.");
            using(var stream=await Dial(IPAddress.Parse(dc==203?"91.105.192.100":Addresses[dc-1]),443,token,false).ConfigureAwait(false))using(token.Register(()=>stream.Dispose())){
                try{return await Bounded(Exchange(media?-dc:dc,bytes=>stream.WriteAsync(bytes,0,bytes.Length,token),async()=>{byte[] bytes=new byte[4096];int count=await stream.ReadAsync(bytes,0,bytes.Length,token).ConfigureAwait(false);if(count==0)throw new EndOfStreamException();return bytes.Take(count).ToArray();}),token).ConfigureAwait(false);}
                catch{token.ThrowIfCancellationRequested();throw;}
            }
        }
        internal static async Task<string> Exchange(int dc,Func<byte[],Task> write,Func<Task<byte[]>> read,byte protocol=0xef){
            if(protocol!=0xef && protocol!=0xee && protocol!=0xdd)throw new InvalidDataException("Неизвестный формат MTProto.");
            if(!KnownDc(Math.Abs(dc)))throw new InvalidDataException("Неизвестный DC пробы.");
            var init=Random(64);init[0]=0x42;init[56]=init[57]=init[58]=init[59]=protocol;Array.Copy(BitConverter.GetBytes((short)dc),0,init,60,2);
            using(var send=Outgoing(init))using(var receive=Incoming(init)){
                byte[] encrypted=send.Transform(init),header=(byte[])init.Clone();Array.Copy(encrypted,56,header,56,8);
                var nonce=Random(16);int prefixSize=protocol==0xef?1:4;byte[] plain=new byte[prefixSize+40+(protocol==0xdd?12:0)];if(protocol==0xef)plain[0]=10;else Array.Copy(BitConverter.GetBytes(plain.Length-4),0,plain,0,4);
                long messageId=((DateTime.UtcNow.Ticks-621355968000000000L)/10000000L)<<32;
                Array.Copy(BitConverter.GetBytes(messageId),0,plain,prefixSize+8,8);Array.Copy(BitConverter.GetBytes(20),0,plain,prefixSize+16,4);Array.Copy(BitConverter.GetBytes(0xbe7e8ef1u),0,plain,prefixSize+20,4);Array.Copy(nonce,0,plain,prefixSize+24,16);
                await write(header).ConfigureAwait(false);await write(send.Transform(plain)).ConfigureAwait(false);
                using(var memory=new MemoryStream()){
                    while(memory.Length<64*1024){
                        byte[] decoded=receive.Transform(await read().ConfigureAwait(false));memory.Write(decoded,0,decoded.Length);
                        byte[] data=memory.ToArray();if(data.Length<1)continue;int prefix=protocol==0xef?(data[0]==0x7f?4:1):4;if(data.Length<prefix)continue;
                        int length=protocol==0xef?(prefix==1?data[0]*4:(data[1]|data[2]<<8|data[3]<<16)*4):BitConverter.ToInt32(data,0);
                        if(length<40 || length>65536)throw new InvalidDataException("Неверный размер MTProto resPQ.");
                        if(data.Length<prefix+length)continue;
                        if(BitConverter.ToInt64(data,prefix)!=0 || BitConverter.ToUInt32(data,prefix+20)!=0x05162463 || !data.Skip(prefix+24).Take(16).SequenceEqual(nonce))throw new InvalidDataException("MTProto resPQ/nonce не подтверждены.");
                        return "DC"+dc+": MTProto resPQ, nonce подтверждён";
                    }
                    throw new InvalidDataException("Слишком большой ответ MTProto.");
                }
            }
        }
    }
    // Per-bridge bounded telemetry. No packet contents, account keys or paths.
    // Reconnect storms produce one summary per 30s, not one UI/file write per socket.
    internal sealed class TelegramTelemetry : IDisposable {
        readonly Action<string> log;readonly Func<DateTime> clock;readonly object gate=new object();readonly Timer timer;
        readonly System.Collections.Generic.Dictionary<string,DateTime> events=new System.Collections.Generic.Dictionary<string,DateTime>();
        int active,mediaActive,cdnActive,opened,failed,suppressed,eventCount;long sent,received;bool dirty,disposed;string lastFailure="";DateTime eventWindow;
        internal TelegramTelemetry(Action<string> log,int interval=30000,Func<DateTime> clock=null){this.log=log;this.clock=clock??(()=>DateTime.UtcNow);eventWindow=this.clock();if(interval>0)timer=new Timer(_=>Report(),null,interval,interval);}
        void Write(string line){try{if(log!=null)log(line);}catch(IOException){}catch(ObjectDisposedException){}}
        internal void Event(string line){
            lock(gate){if(disposed)return;var now=clock();if((now-eventWindow).TotalSeconds>=30){eventWindow=now;eventCount=0;}
                string key=line.Split(new[]{" · "},StringSplitOptions.None)[0];DateTime previous;
                if(eventCount>=8 || events.TryGetValue(key,out previous) && (now-previous).TotalSeconds<60){suppressed++;dirty=true;return;}
                if(events.Count>=32 && !events.ContainsKey(key)){suppressed++;dirty=true;return;}
                events[key]=now;eventCount++;Write(line);
            }
        }
        internal void Open(int dc,bool media){lock(gate){if(disposed)return;active++;if(media)mediaActive++;if(dc==203)cdnActive++;opened++;dirty=true;}}
        internal void Close(int dc,bool media){lock(gate){if(disposed)return;active--;if(media)mediaActive--;if(dc==203)cdnActive--;dirty=true;}}
        internal void Transfer(int up,int down){lock(gate){if(disposed)return;sent+=up;received+=down;dirty=true;}}
        internal void Failure(Exception error){
            if(error is OperationCanceledException || error is EndOfStreamException || error is ObjectDisposedException)return;
            lock(gate){if(disposed)return;failed++;dirty=true;lastFailure=error.GetBaseException().Message.Replace('\r',' ').Replace('\n',' ');if(lastFailure.Length>180)lastFailure=lastFailure.Substring(0,180);}
        }
        internal void Report(){lock(gate){if(disposed || !dirty)return;
            Write("Telegram: сводка · активных "+active+" (медиа "+mediaActive+", CDN "+cdnActive+") · новых "+opened+" · отправлено "+sent+" Б, получено "+received+" Б · ошибок "+failed+(suppressed>0?" · повторов скрыто "+suppressed:"")+(failed>0?" · последняя: "+lastFailure:""));
            sent=received=0;opened=failed=suppressed=0;dirty=false;
        }}
        public void Dispose(){if(timer!=null)timer.Dispose();lock(gate){if(disposed)return;Report();disposed=true;}}
    }
    internal sealed class TelegramSocketStream : Stream {
        readonly Stream inner;readonly Socket socket;int disposed;readonly int port;readonly Action<int,bool> tracking;
        internal TelegramSocketStream(Stream inner,Socket socket,int port,Action<int,bool> tracking){this.inner=inner;this.socket=socket;this.port=port;this.tracking=tracking;}
        public override bool CanRead{get{return true;}}public override bool CanWrite{get{return true;}}public override bool CanSeek{get{return false;}}
        public override long Length{get{throw new NotSupportedException();}}public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
        public override void Flush(){inner.Flush();}public override long Seek(long offset,SeekOrigin origin){throw new NotSupportedException();}public override void SetLength(long value){throw new NotSupportedException();}
        public override int Read(byte[] b,int o,int n){return inner.Read(b,o,n);}public override void Write(byte[] b,int o,int n){inner.Write(b,o,n);}
        public override Task<int> ReadAsync(byte[] b,int o,int n,CancellationToken t){return inner.ReadAsync(b,o,n,t);}public override Task WriteAsync(byte[] b,int o,int n,CancellationToken t){return inner.WriteAsync(b,o,n,t);}
        // Framework SslStream/WebSocket use APM as well as Task APIs. Stream's
        // default APM adapter serializes read and write: a pending receive can
        // block the send that would cause the reply. Preserve full duplex.
        public override IAsyncResult BeginRead(byte[] b,int o,int n,AsyncCallback callback,object state){return inner.BeginRead(b,o,n,callback,state);}
        public override int EndRead(IAsyncResult result){return inner.EndRead(result);}
        public override IAsyncResult BeginWrite(byte[] b,int o,int n,AsyncCallback callback,object state){return inner.BeginWrite(b,o,n,callback,state);}
        public override void EndWrite(IAsyncResult result){inner.EndWrite(result);}
        protected override void Dispose(bool disposing){if(disposing && Interlocked.Exchange(ref disposed,1)==0){try{inner.Dispose();}finally{socket.Close();if(tracking!=null)tracking(port,false);}}base.Dispose(disposing);}
    }
}
