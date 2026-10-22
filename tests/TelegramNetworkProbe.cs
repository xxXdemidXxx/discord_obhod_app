using System;using System.Linq;using System.Threading;using System.Net;using System.Net.Sockets;using System.Threading.Tasks;using ZapretDesktop;
class TelegramNetworkProbe {
    static async Task<string> Relay(int dc,CancellationToken token){
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        try{
            var server=Task.Run(async()=>{using(var accepted=await listener.AcceptTcpClientAsync()){byte[] header=new byte[64];await TelegramWire.Exact(accepted.GetStream(),header,token);bool media;int actual=TelegramWire.Dc(header,out media);await TelegramWire.Relay(accepted.GetStream(),header,actual,media,token,Console.WriteLine);}});
            string result;using(var client=new TcpClient())using(token.Register(()=>client.Close())){await client.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port);var stream=client.GetStream();result=await TelegramWire.Bounded(TelegramWire.Exchange(dc,bytes=>stream.WriteAsync(bytes,0,bytes.Length,token),async()=>{byte[] bytes=new byte[4096];int count=await stream.ReadAsync(bytes,0,bytes.Length,token);if(count==0)throw new System.IO.EndOfStreamException();return System.Linq.Enumerable.Take(bytes,count).ToArray();}),token);}
            await server;return "TCP -> local relay -> WSS -> "+result;
        }finally{listener.Stop();}
    }
    static int Main(string[] args){int success=0,modes=args.Contains("--diagnostic")?3:2;for(int mode=0;mode<modes;mode++)for(int dc=1;dc<=(args.Contains("--quick")?1:5);dc++){using(var cancel=new CancellationTokenSource(12000)){try{Console.WriteLine((mode==0?TelegramWire.Probe(dc,cancel.Token):mode==1?Relay(dc,cancel.Token):TelegramWire.ProbeTcp(dc,cancel.Token)).GetAwaiter().GetResult());success++;}catch(Exception ex){Console.WriteLine("mode="+mode+" DC"+dc+": "+ex.GetBaseException().Message);}}}return success==modes*(args.Contains("--quick")?1:5)?0:1;}
}
