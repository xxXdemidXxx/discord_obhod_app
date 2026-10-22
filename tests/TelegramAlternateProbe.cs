using System;using System.Linq;using System.Net;using System.Net.WebSockets;using System.Threading;using ZapretDesktop;
class TelegramAlternateProbe {
    static int Main(){int passed=0;foreach(int dc in new[]{2,4})using(var cancel=new CancellationTokenSource(8000)){
        try{using(var ws=TelegramWire.ConnectAt(dc,false,IPAddress.Parse("149.154.167.220"),cancel.Token).GetAwaiter().GetResult()){
            string result=TelegramWire.Exchange(dc,bytes=>ws.SendAsync(new ArraySegment<byte>(bytes),WebSocketMessageType.Binary,true,cancel.Token),async()=>{var bytes=new byte[4096];var frame=await ws.ReceiveAsync(new ArraySegment<byte>(bytes),cancel.Token);if(frame.MessageType!=WebSocketMessageType.Binary)throw new System.IO.InvalidDataException("No binary MTProto response");return bytes.Take(frame.Count).ToArray();}).GetAwaiter().GetResult();
            Console.WriteLine("149.154.167.220 + verified TLS/SNI kws"+dc+": "+result);ws.Abort();passed++;
        }}catch(Exception ex){Console.WriteLine("DC"+dc+": "+ex.Message);}
    }return passed==2?0:1;}
}
