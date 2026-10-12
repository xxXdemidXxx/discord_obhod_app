using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ZapretDesktop;
[assembly: AssemblyVersion("0.4.4.0")]
class LegacyBridge {
    sealed class Feed : HttpMessageHandler {
        public string Manifest,Archive;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=request.RequestUri.AbsolutePath.EndsWith("update.json")?(HttpContent)new StringContent(File.ReadAllText(Manifest)):new ByteArrayContent(File.ReadAllBytes(Archive))});
        }
    }
    static int Main(string[] args) {
        try{Run(args).GetAwaiter().GetResult();return 0;}catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
    static async Task Run(string[] args) {
        using(var updater=new ShellUpdates(new Feed{Manifest=args[1],Archive=args[2]})) {
            var release=await updater.Check();if(release==null)throw new Exception("Old updater did not discover signed release outer fields");
            string staged=await updater.Stage(release,args[0]);
            if(File.Exists(Path.Combine(Path.GetDirectoryName(staged),"release-proof.json")))throw new Exception("Old updater unexpectedly copied new proof");
            await ShellUpdates.Launch(staged,Path.Combine(args[0],"Obhod.exe"),true);
            File.WriteAllText(Path.Combine(args[0],"legacy-parent-ready.txt"),"ready");
        }
    }
}
