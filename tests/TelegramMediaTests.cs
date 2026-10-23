using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace ZapretDesktop {
    internal static class TelegramMediaChecks {
        static byte[] Header(short dc,byte protocol){
            var init=TelegramWire.Random(64);for(int i=56;i<60;i++)init[i]=protocol;
            Array.Copy(BitConverter.GetBytes(dc),0,init,60,2);var header=(byte[])init.Clone();
            using(var ctr=TelegramWire.Outgoing(init)){var encrypted=ctr.Transform(init);Array.Copy(encrypted,56,header,56,8);}return header;
        }
        internal static async Task Run(Action<bool,string> check){
            foreach(short id in new short[]{203,-203,2,-2,4,-4})foreach(byte protocol in new byte[]{0xef,0xee,0xdd}){
                bool media;check(TelegramWire.Dc(Header(id,protocol),out media)==Math.Abs(id) && media==(id<0),"Direct MTProto CDN/media identity preserved: "+id+" / "+protocol);
            }
            foreach(short id in new short[]{0,6,10002,-10002,204}){
                bool rejected=false;try{bool media;TelegramWire.Dc(Header(id,0xef),out media);}catch(InvalidDataException){rejected=true;}
                check(rejected,"Unknown and test DC cannot silently become a production DC: "+id);
            }
            bool noMasterMapping=false;try{TelegramWire.Endpoint(203);}catch(InvalidDataException){noMasterMapping=true;}check(noMasterMapping,"CDN203 cannot be routed to an ordinary DC2 hostname");
            check(TelegramWire.RelayEndpoint(203,TelegramWire.RelayDomains[0]).Host.StartsWith("kws203."),"CDN203 has a distinct reserved relay hostname");
            foreach(string domain in new[]{"localhost","evil.example",TelegramWire.RelayDomains[0]+".evil.example"}){bool rejected=false;try{TelegramWire.RelayEndpoint(203,domain);}catch(InvalidDataException){rejected=true;}check(rejected,"Unreviewed relay hostname rejected: "+domain);}
            foreach(string address in new[]{"127.0.0.1","10.0.0.1","192.168.1.2","172.16.0.1","169.254.169.254","100.64.0.1","::1","224.0.0.1"})check(!TelegramWire.PublicRelayAddress(IPAddress.Parse(address)),"Relay DNS cannot dial internal or reserved address: "+address);
            var native=new[]{IPAddress.Parse("149.154.167.99"),IPAddress.Parse("149.154.167.99")};
            check(TelegramWire.Routes(2,native,true).Select(x=>x.ToString()).SequenceEqual(new[]{"149.154.167.99","149.154.167.220"}),"DC2 media prefers native WS IPs over shared redirect");
            check(TelegramWire.Routes(2,native).First().ToString()=="149.154.167.220","Existing message route priority preserved");
            check(TelegramWire.PublicRelayAddress(IPAddress.Parse("104.21.10.20")),"Public relay DNS address permitted");
            var lines=new List<string>();var now=DateTime.UtcNow;
            using(var stats=new TelegramTelemetry(lines.Add,0,()=>now)){
                for(int i=0;i<5000;i++)stats.Event("Telegram: WSS DC203 media · ip · "+i);
                check(lines.Count==1,"Reconnect storm emits one route event rather than 5000 journal writes");
                stats.Open(203,true);stats.Transfer(1500000,2000000);stats.Failure(new OperationCanceledException());stats.Failure(new EndOfStreamException());stats.Report();
                check(lines.Count==2 && lines[1].Contains("CDN 1") && lines[1].Contains("получено 2000000") && lines[1].Contains("ошибок 0") && lines[1].Contains("4999"),"Periodic telemetry retains CDN, byte and suppressed-event counts; expected closes are quiet");
                stats.Report();check(lines.Count==2,"Unchanged telemetry does not repeatedly write an idle summary");
                stats.Close(203,true);stats.Failure(new IOException("fixture unreachable"));now=now.AddSeconds(61);stats.Event("Telegram: WSS DC203 media · recovered");stats.Report();
                check(lines.Count==4 && lines.Last().Contains("активных 0") && lines.Last().Contains("ошибок 1"),"Route recovery and genuine failures remain visible after throttling");
            }
            await RelayLarge(check).ConfigureAwait(false);
            int fileProbes=0;var result=await ServiceChecks.TelegramProbe(CancellationToken.None,2,(dc,media,token)=>{if(media)Interlocked.Increment(ref fileProbes);if(dc==203)throw new IOException("fixture blocked CDN");return Task.FromResult("fixture protocol available");}).ConfigureAwait(false);
            check(fileProbes==3 && result.AvailableEndpoints==5 && result.MediaAvailableEndpoints==2 && result.MediaTotalEndpoints==3 && result.State==2,"All healthy main DCs cannot hide a failing CDN/file probe");
        }
        static async Task RelayLarge(Action<bool,string> check){
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            byte[] up=new byte[3*1024*1024+37],down=new byte[2*1024*1024+19];new Random(93).NextBytes(up);new Random(94).NextBytes(down);
            var header=Header(-203,0xef);var ws=new MediaSocket(down);var lines=new List<string>();
            using(var deadline=new CancellationTokenSource(15000))using(var app=new TcpClient())using(var stats=new TelegramTelemetry(lines.Add,0)){
                try {
                    var accept=listener.AcceptTcpClientAsync();await app.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port).ConfigureAwait(false);
                    using(var relayPeer=await accept.ConfigureAwait(false)){
                        int requestedDc=0;bool requestedMedia=false;
                        var relay=TelegramWire.Relay(relayPeer.GetStream(),header,203,true,deadline.Token,stats.Event,stats,(dc,media,token)=>{requestedDc=dc;requestedMedia=media;return Task.FromResult<WebSocket>(ws);});
                        var read=ReadExact(app.GetStream(),down.Length,deadline.Token);
                        int offset=0;while(offset<up.Length){int n=Math.Min(offset%65521+1,up.Length-offset);await app.GetStream().WriteAsync(up,offset,n,deadline.Token).ConfigureAwait(false);offset+=n;}
                        byte[] actualDown=await read.ConfigureAwait(false);
                        while(ws.SentCount<header.Length+up.Length){deadline.Token.ThrowIfCancellationRequested();await Task.Delay(5,deadline.Token).ConfigureAwait(false);}
                        app.Close();await relay.ConfigureAwait(false);
                        check(requestedDc==203 && requestedMedia,"Multi-megabyte CDN stream stays on CDN203/media, not a main-account DC");
                        check(ws.Sent.SequenceEqual(header.Concat(up)),"All 3MiB uplink bytes, including original obfuscation header, pass unchanged across arbitrary TCP chunks");
                        check(actualDown.SequenceEqual(down),"All 2MiB downlink bytes survive fragmented and empty binary WS messages");
                        stats.Report();check(lines.Count==1 && lines[0].Contains("получено "+down.Length),"Large relay logs one aggregate instead of per-packet or per-channel events");
                    }
                }finally{listener.Stop();ws.Dispose();}
            }
        }
        static async Task<byte[]> ReadExact(Stream stream,int size,CancellationToken token){var result=new byte[size];int offset=0;while(offset<size){int n=await stream.ReadAsync(result,offset,size-offset,token).ConfigureAwait(false);if(n==0)throw new EndOfStreamException();offset+=n;}return result;}
        sealed class MediaSocket : WebSocket {
            readonly byte[] down;readonly MemoryStream sent=new MemoryStream();readonly object gate=new object();int offset,reads;WebSocketState state=WebSocketState.Open;
            internal MediaSocket(byte[] down){this.down=down;}
            internal long SentCount{get{lock(gate)return sent.Length;}}
            internal byte[] Sent{get{lock(gate)return sent.ToArray();}}
            public override WebSocketCloseStatus? CloseStatus{get{return null;}}
            public override string CloseStatusDescription{get{return null;}}
            public override WebSocketState State{get{return state;}}
            public override string SubProtocol{get{return "binary";}}
            public override void Abort(){state=WebSocketState.Aborted;}
            public override void Dispose(){state=WebSocketState.Closed;}
            public override Task CloseAsync(WebSocketCloseStatus status,string description,CancellationToken token){state=WebSocketState.Closed;return Task.FromResult(0);}
            public override Task CloseOutputAsync(WebSocketCloseStatus status,string description,CancellationToken token){return CloseAsync(status,description,token);}
            public override Task SendAsync(ArraySegment<byte> bytes,WebSocketMessageType type,bool end,CancellationToken token){token.ThrowIfCancellationRequested();lock(gate)sent.Write(bytes.Array,bytes.Offset,bytes.Count);return Task.FromResult(0);}
            public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> bytes,CancellationToken token){
                if(++reads==1)return new WebSocketReceiveResult(0,WebSocketMessageType.Binary,false);
                if(offset==down.Length){await Task.Delay(Timeout.Infinite,token).ConfigureAwait(false);throw new OperationCanceledException(token);}
                int count=Math.Min(Math.Min(bytes.Count,reads%2==0?19:65497),down.Length-offset);Array.Copy(down,offset,bytes.Array,bytes.Offset,count);offset+=count;
                return new WebSocketReceiveResult(count,WebSocketMessageType.Binary,offset==down.Length);
            }
        }
    }
}
