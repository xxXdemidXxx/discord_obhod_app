using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretDesktop {
    internal sealed class YouTubeDnsSession : IDisposable {
        readonly DnsOverHttps dns;
        readonly YouTubeDnsProxy proxy;
        readonly string profile;
        internal static string Browser() {
            var roots=new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)};
            foreach(string root in roots.Where(x=>!String.IsNullOrEmpty(x)))foreach(string name in new[]{@"Microsoft\Edge\Application\msedge.exe",@"Google\Chrome\Application\chrome.exe"}){string path=Path.Combine(root,name);if(File.Exists(path))return path;}
            foreach(string name in new[]{@"Microsoft\Edge\Application\msedge.exe",@"Google\Chrome\Application\chrome.exe"}){string local=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),name);if(File.Exists(local))return local;}
            throw new InvalidOperationException("Для отдельного окна нужен установленный Microsoft Edge или Google Chrome. Настройки Windows не изменены.");
        }
        internal static string Arguments(string profile,int port) {
            if(port<1 || port>65535)throw new ArgumentOutOfRangeException("port");
            return String.Join(" ",new[]{"--user-data-dir="+Path.GetFullPath(profile),"--proxy-server=socks5://127.0.0.1:"+port,"--proxy-bypass-list=<-loopback>","--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1","--disable-quic","--no-first-run","--no-default-browser-check","--new-window","https://www.youtube.com/"}.Select(Strategy.Quote));
        }
        internal YouTubeDnsSession(string root) {
            profile=Path.Combine(root,"data","youtube-browser","session-"+Guid.NewGuid().ToString("N"));
            dns=new DnsOverHttps();proxy=new YouTubeDnsProxy(dns.Resolve);
        }
        internal async Task Open(CancellationToken token) {
            string browser=Browser();await dns.Resolve("www.youtube.com",token);
            token.ThrowIfCancellationRequested();Trust.NoLinks(profile);Directory.CreateDirectory(profile);
            using(var process=Process.Start(new ProcessStartInfo(browser,Arguments(profile,proxy.Port)){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(browser)})) {
                if(process==null)throw new IOException("Не удалось открыть отдельное окно YouTube.");
            }
        }
        public void Dispose(){proxy.Dispose();dns.Dispose();}
    }
}
