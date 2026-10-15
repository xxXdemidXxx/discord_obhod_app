using System;
using System.Collections;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ZapretDesktop {
    public static class DnsDiagnostics {
        static void ObserveFault(Task task) { task.ContinueWith(t=>{var ignored=t.Exception;},TaskContinuationOptions.OnlyOnFaulted); }
        public static async Task<string> Run(CancellationToken token) {
            var report=new StringBuilder("DNS для www.youtube.com\r\n\r\n");
            foreach(var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(x=>x.OperationalStatus==OperationalStatus.Up && x.NetworkInterfaceType!=NetworkInterfaceType.Loopback)) {
                try {report.AppendLine(adapter.Name+": DNS "+String.Join(", ",adapter.GetIPProperties().DnsAddresses.Select(x=>x.ToString())));} catch(NetworkInformationException){}
            }
            report.AppendLine(); bool systemOk=false,dohOk=false;
            var lookup=Dns.GetHostAddressesAsync("www.youtube.com");
            ObserveFault(lookup);
            if(await Task.WhenAny(lookup,Task.Delay(5000,token))==lookup) {
                try {var addresses=await lookup; systemOk=addresses.Length>0; report.AppendLine("DNS Windows: "+String.Join(", ",addresses.Select(x=>x.ToString()).Take(5)));}
                catch(SocketException ex){report.AppendLine("DNS Windows: ошибка · "+ex.Message);}
            } else report.AppendLine("DNS Windows: нет ответа за 5 секунд.");
            token.ThrowIfCancellationRequested();
            using(var handler=new HttpClientHandler{UseProxy=false}) using(var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(7)}) {
                try {
                    using(var response=await client.GetAsync("https://dns.google/resolve?name=www.youtube.com&type=A",token)) {
                        response.EnsureSuccessStatusCode();
                        try{dohOk=DnsOverHttps.Parse(await response.Content.ReadAsStringAsync()).Addresses.Length>0;}catch(IOException){dohOk=false;}
                        report.AppendLine("Google DNS через HTTPS: "+(dohOk?"имя найдено":"имя не найдено"));
                    }
                } catch(Exception ex){if(token.IsCancellationRequested)throw new OperationCanceledException(token);report.AppendLine("Google DNS через HTTPS: "+ex.GetBaseException().Message);}
            }
            report.AppendLine();
            if(!systemOk && dohOk) report.AppendLine("Windows не определяет имя, а Google DoH определяет. Попробуйте «Открыть YouTube в отдельном окне»: оно использует DNS через HTTPS без изменения Windows. Перебор DPI-стратегий сам по себе не исправляет DNS.");
            else if(systemOk) report.AppendLine("Сейчас Windows определяет имя YouTube. Повторите HTTPS-проверку: ошибка могла быть временной. Если соединение не проходит, попробуйте автоподбор стратегий.");
            else report.AppendLine("Имя не удалось проверить обоими способами. Проверьте интернет и DNS сетевого подключения.");
            report.AppendLine("\r\nКороткие инструкции: вкладка «Помощь» → «DNS в браузере» или «DNS в Windows». Проверки YouTube сначала используют DNS Windows, а при его ошибке повторяются через DNS по HTTPS (DoH).");
            return report.ToString();
        }
    }
}
