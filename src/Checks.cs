using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretDesktop {
    public enum ProbeKind { Http, DiscordApp, DiscordGateway, DiscordUpdates, Telegram }
    public sealed class ServiceTarget {
        public string Name, Url;
        public readonly ProbeKind Kind;
        public ServiceTarget(string name, string url, ProbeKind kind = ProbeKind.Http) { Name = name; Url = url; Kind = kind; }
    }
    public sealed class CheckResult {
        public int State; // 0: unavailable; 1: probe passed; 2: unexpected server response
        public string Detail; public long Milliseconds;
        public string Failure = "";
        public bool Informational;public int AvailableEndpoints,TotalEndpoints;
        public string DnsRoute = "windows", SystemDnsFailure = "";
    }
    public static class ServiceChecks {
#if TESTING
        internal static Func<ServiceTarget,CancellationToken,Task<CheckResult>> TestRun=null;
#endif
        public static readonly ServiceTarget[] Targets = {
            new ServiceTarget("YouTube · сайт", "https://www.youtube.com/"),
            new ServiceTarget("YouTube · изображения", "https://i.ytimg.com/vi/jNQXAC9IVRw/hqdefault.jpg"),
            new ServiceTarget("Discord · приложение", "https://discord.com/app", ProbeKind.DiscordApp),
            new ServiceTarget("Discord · Gateway", "https://discord.com/api/v10/gateway", ProbeKind.DiscordGateway),
            new ServiceTarget("Discord · обновления", "https://updates.discord.com/distributions/app/manifests/latest?platform=win&channel=stable&arch=x64", ProbeKind.DiscordUpdates),
            new ServiceTarget("Telegram · MTProto", "MTProto DC1–DC5", ProbeKind.Telegram),
            new ServiceTarget("GitHub · обновления", "https://github.com/xxXdemidXxx/discord_obhod_app/releases/latest")
        };
        static HttpClientHandler DirectHandler(){return new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false,AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate};}
        internal static bool YouTubeTarget(ServiceTarget target){
            Uri url;return target.Kind==ProbeKind.Http && Uri.TryCreate(target.Url,UriKind.Absolute,out url) && url.Scheme=="https" && url.IsDefaultPort && url.UserInfo.Length==0 && (url.Host=="www.youtube.com" || url.Host=="i.ytimg.com");
        }
        public static Task<CheckResult> Run(ServiceTarget target, CancellationToken token, int timeoutSeconds = 12) {
#if TESTING
            if(TestRun!=null)return TestRun(target,token);
#endif
            if(target.Kind==ProbeKind.Telegram)return TelegramProbe(token,timeoutSeconds);
            return RunWithDnsFallback(target,token,timeoutSeconds,
                cancel=>Run(target,cancel,timeoutSeconds,DirectHandler(),CheckGateway),
                cancel=>RunYouTubeDoH(target,cancel,timeoutSeconds));
        }
        internal static async Task<CheckResult> TelegramProbe(CancellationToken token,int timeoutSeconds){
            var watch=Stopwatch.StartNew();using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token)){
                deadline.CancelAfter(timeoutSeconds*1000);
                var results=await Task.WhenAll(Enumerable.Range(1,5).Select(async dc=>{try{return new CheckResult{State=1,Detail=await TelegramWire.Bounded(TelegramWire.ProbeTcp(dc,deadline.Token),deadline.Token).ConfigureAwait(false)};}catch(Exception ex){if(!(ex is SocketException) && !(ex is IOException) && !(ex is OperationCanceledException) && !(ex is ObjectDisposedException))throw;return new CheckResult{State=0,Detail="DC"+dc+": "+(deadline.IsCancellationRequested?(token.IsCancellationRequested?"отменено":"тайм-аут"):ex.Message)};}})).ConfigureAwait(false);
                int passed=results.Count(x=>x.State==1);return new CheckResult{State=passed==5?1:passed>0?2:0,Informational=true,AvailableEndpoints=passed,TotalEndpoints=5,Failure=deadline.IsCancellationRequested?"timeout":passed==5?"":"mtproto",Milliseconds=watch.ElapsedMilliseconds,Detail="Telegram: доступны "+passed+" из 5 дата-центров. "+(passed>0 && passed<5?"Сообщения могут работать через доступные DC; часть подключений и вложений может быть недоступна. ":"")+"Проверка транспорта не подтверждает отправку сообщений. | "+String.Join(" | ",results.Select(x=>x.Detail))};
            }
        }
        internal static async Task<CheckResult> RunWithDnsFallback(ServiceTarget target,CancellationToken token,int timeoutSeconds,Func<CancellationToken,Task<CheckResult>> direct,Func<CancellationToken,Task<CheckResult>> doh){
            var watch=Stopwatch.StartNew();
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token)){
                deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                var result=await direct(deadline.Token);
                if(result.Failure=="dns" && YouTubeTarget(target) && !deadline.IsCancellationRequested){
                    string original=result.Detail;
                    try{result=await doh(deadline.Token);}catch(OperationCanceledException){result=new CheckResult{Failure="cancelled",Detail="Отменено"};}
                    catch(Exception ex){if(!(ex is IOException) && !(ex is SocketException) && !(ex is HttpRequestException))throw;result=FromException(ex,watch.ElapsedMilliseconds);}
                    result.DnsRoute="doh";result.SystemDnsFailure=original;
                    result.Detail=(result.State==1?"Доступен через DoH. ":"Повтор через DoH: ")+result.Detail+" | DNS Windows не работает: "+original;
                }
                if(deadline.IsCancellationRequested){result.State=0;result.Failure=token.IsCancellationRequested?"cancelled":"timeout";result.Detail=(token.IsCancellationRequested?"Отменено":"Тайм-аут · "+timeoutSeconds+" секунд")+" | "+target.Url+(result.SystemDnsFailure.Length>0?" | DNS Windows не работает: "+result.SystemDnsFailure:"");}
                result.Milliseconds=watch.ElapsedMilliseconds;return result;
            }
        }
        internal static async Task<CheckResult> RunYouTubeDoH(ServiceTarget target,CancellationToken token,int timeoutSeconds){
            using(var dns=new DnsOverHttps())using(var proxy=new YouTubeDnsProxy(dns.Resolve,httpConnect:true)){
                var handler=DirectHandler();handler.UseProxy=true;handler.Proxy=new WebProxy(new Uri("http://127.0.0.1:"+proxy.Port),false);
                return await Run(target,token,timeoutSeconds,handler,CheckGateway);
            }
        }
        // Inject transports for offline tests; never use a live VPN connection to validate a strategy.
        internal static async Task<CheckResult> Run(ServiceTarget target, CancellationToken token, int timeoutSeconds,
            HttpMessageHandler handler, Func<Uri,CancellationToken,Task> gateway) {
            var watch = Stopwatch.StartNew(); string stage = target.Url;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }) {
                deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds)); var cancel = deadline.Token;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ByPass/" + ShellUpdates.Current);
                client.DefaultRequestHeaders.ConnectionClose = true;
                client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                try {
                    using (var response = await Get(client, new Uri(target.Url), cancel, target.Kind != ProbeKind.Http)) {
                        if (!response.IsSuccessStatusCode) return HttpError(response, watch.ElapsedMilliseconds, stage);
                        string detail;
                        if (target.Kind == ProbeKind.DiscordGateway) {
                            var json = Parse(await ReadText(response, 64 * 1024, cancel)); object value;
                            Uri url;
                            if (!json.TryGetValue("url", out value) || !Uri.TryCreate(value as string, UriKind.Absolute, out url) ||
                                url.Scheme != "wss" || url.Host != "gateway.discord.gg" || !url.IsDefaultPort || url.UserInfo.Length != 0)
                                throw new InvalidDataException("API не вернул адрес Gateway Discord");
                            url = new UriBuilder(url) { Query = "v=10&encoding=json" }.Uri;
                            stage = url.ToString(); await gateway(url, cancel);
                            detail = "API + WebSocket · Hello и ответ на heartbeat получены";
                        } else if (target.Kind == ProbeKind.DiscordUpdates) {
                            var json = Parse(await ReadText(response, 512 * 1024, cancel)); object value;
                            var full = json.TryGetValue("full", out value) ? value as Dictionary<string,object> : null;
                            Uri download;
                            if (full == null || !full.TryGetValue("url", out value) || !Uri.TryCreate(value as string, UriKind.Absolute, out download) || !DiscordUrl(download))
                                throw new InvalidDataException("Сервер обновлений не вернул адрес файла Discord");
                            stage = download.GetLeftPart(UriPartial.Path);
                            using (var file = await Get(client, download, cancel, true, true)) {
                                if (!file.IsSuccessStatusCode) return HttpError(file, watch.ElapsedMilliseconds, stage);
                                var range = file.Content.Headers.ContentRange;
                                if (file.StatusCode == HttpStatusCode.PartialContent && (range == null || range.Unit != "bytes" || range.From != 0 || range.To != 1023))
                                    throw new InvalidDataException("Неверный диапазон файла обновления");
                                string type = file.Content.Headers.ContentType == null ? "" : file.Content.Headers.ContentType.MediaType;
                                if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || type.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0)
                                    throw new InvalidDataException("Вместо файла обновления получена страница или JSON");
                                var sample = await ReadBody(file, 1024, cancel, true);
                                if (sample.Length != 1024 || sample[0] == (byte)'<') throw new InvalidDataException("Не удалось прочитать начало файла обновления");
                            }
                            detail = "Манифест x64 + CDN · прочитано 1024 байта обновления";
                        } else if (target.Kind == ProbeKind.DiscordApp) {
                            string html = await ReadText(response, 4 * 1024 * 1024, cancel);
                            if (!Regex.IsMatch(html, @"id\s*=\s*[""']app-mount[""']", RegexOptions.IgnoreCase))
                                throw new InvalidDataException("Получена страница без приложения Discord");
                            var scripts = Regex.Matches(html, @"<script\b[^>]*\bsrc\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)
                                .Cast<Match>().Select(x => new Uri(new Uri(target.Url), WebUtility.HtmlDecode(x.Groups[1].Value))).Where(x => x.AbsolutePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase)).ToArray();
                            if (scripts.Length == 0) throw new InvalidDataException("В странице нет загрузочных скриптов Discord");
                            // Sample the first and last bootstrap scripts; this is not a full logged-in client test.
                            foreach (var script in new[] { scripts.First(), scripts.Last() }.Distinct()) {
                                if (!DiscordUrl(script)) throw new InvalidDataException("Неизвестный адрес скрипта Discord");
                                stage = script.GetLeftPart(UriPartial.Path);
                                using (var asset = await Get(client, script, cancel, true)) {
                                    if (!asset.IsSuccessStatusCode) return HttpError(asset, watch.ElapsedMilliseconds, stage);
                                    string body = await ReadText(asset, 16 * 1024 * 1024, cancel);
                                    if (String.IsNullOrWhiteSpace(body) || body.TrimStart().StartsWith("<")) throw new InvalidDataException("Вместо скрипта Discord получена пустая страница или HTML");
                                }
                            }
                            detail = "HTML приложения + выборочная загрузка JS";
                        } else detail = "Доступен · HTTP " + (int)response.StatusCode;
                        return new CheckResult { State = 1, Detail = detail, Milliseconds = watch.ElapsedMilliseconds };
                    }
                } catch (OperationCanceledException) {
                    return new CheckResult { State = 0, Detail = (token.IsCancellationRequested ? "Отменено" : "Тайм-аут · " + timeoutSeconds + " секунд") + " | " + stage,
                        Failure = token.IsCancellationRequested ? "cancelled" : "timeout", Milliseconds = watch.ElapsedMilliseconds };
                } catch (UriFormatException ex) {
                    return new CheckResult { State = 2, Detail = "Некорректный адрес | " + stage + " | " + ex.Message, Failure = "response", Milliseconds = watch.ElapsedMilliseconds };
                } catch (InvalidDataException ex) {
                    return new CheckResult { State = 2, Detail = "Неожиданный ответ | " + stage + " | " + ex.Message, Failure = "response", Milliseconds = watch.ElapsedMilliseconds };
                } catch (Exception ex) {
                    if (!((ex is ObjectDisposedException && deadline.IsCancellationRequested) || ex is HttpRequestException || ex is WebSocketException || ex is IOException || ex is WebException || ex is SocketException || ex is AuthenticationException)) throw;
                    if (deadline.IsCancellationRequested) return new CheckResult { State = 0, Failure = token.IsCancellationRequested ? "cancelled" : "timeout", Detail = (token.IsCancellationRequested ? "Отменено" : "Тайм-аут · " + timeoutSeconds + " секунд") + " | " + stage, Milliseconds = watch.ElapsedMilliseconds };
                    var result = FromException(ex, watch.ElapsedMilliseconds); result.Detail += " | " + stage; return result;
                }
            }
        }
        static CheckResult HttpError(HttpResponseMessage response, long elapsed, string stage) {
            return new CheckResult { State = 2, Detail = "Ответ сервера · HTTP " + (int)response.StatusCode + " | " + stage, Failure = "http", Milliseconds = elapsed };
        }
        static bool DiscordUrl(Uri url) {
            return url.Scheme == "https" && url.IsDefaultPort && url.UserInfo.Length == 0 &&
                (url.Host == "discord.com" || url.Host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase) ||
                 url.Host == "discordapp.net" || url.Host.EndsWith(".discordapp.net", StringComparison.OrdinalIgnoreCase) ||
                 url.Host == "discordapp.com" || url.Host.EndsWith(".discordapp.com", StringComparison.OrdinalIgnoreCase));
        }
        static async Task<HttpResponseMessage> Get(HttpClient client, Uri url, CancellationToken token, bool discord, bool range = false) {
            for (int redirects = 0; ; redirects++) {
                if (discord && !DiscordUrl(url)) throw new InvalidDataException("Перенаправление за пределы серверов Discord");
                using (var request = new HttpRequestMessage(HttpMethod.Get, url)) {
                    if (range) request.Headers.Range = new RangeHeaderValue(0, 1023);
                    var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    int status = (int)response.StatusCode;
                    if (status != 301 && status != 302 && status != 303 && status != 307 && status != 308) return response;
                    var location = response.Headers.Location; response.Dispose();
                    if (redirects >= 5 || location == null) throw new InvalidDataException("Некорректное перенаправление");
                    url = location.IsAbsoluteUri ? location : new Uri(url, location);
                }
            }
        }
        internal static async Task<byte[]> ReadBody(HttpResponseMessage response, int limit, CancellationToken token, bool sample = false) {
            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var memory = new MemoryStream()) {
                var buffer = new byte[8192];
                // Disposing the stream also unblocks Framework streams that do not honour cancellation.
                using (token.Register(() => stream.Dispose())) {
                    while (true) {
                        token.ThrowIfCancellationRequested();
                        int count = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, limit - (int)memory.Length + (sample ? 0 : 1)), token);
                        if (count == 0) break;
                        memory.Write(buffer, 0, count);
                        if (memory.Length > limit) throw new InvalidDataException("Ответ превышает допустимый размер");
                        if (sample && memory.Length == limit) break;
                    }
                }
                token.ThrowIfCancellationRequested(); return memory.ToArray();
            }
        }
        static async Task<string> ReadText(HttpResponseMessage response, int limit, CancellationToken token) {
            return Encoding.UTF8.GetString(await ReadBody(response, limit, token));
        }
        static Dictionary<string,object> Parse(string text) {
            try { var value = new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(text); if (value != null) return value; }
            catch (Exception ex) { if (!(ex is ArgumentException || ex is InvalidOperationException)) throw; }
            throw new InvalidDataException("Некорректный JSON");
        }
        static async Task<string> Receive(WebSocket socket, CancellationToken token) {
            using (var memory = new MemoryStream()) {
                var buffer = new byte[4096];
                while (true) {
                    var message = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (message.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Gateway не вернул текстовое сообщение");
                    memory.Write(buffer, 0, message.Count);
                    if (memory.Length > 64 * 1024) throw new InvalidDataException("Слишком большое сообщение Gateway");
                    if (message.EndOfMessage) return Encoding.UTF8.GetString(memory.ToArray());
                }
            }
        }
        internal static async Task VerifyGateway(WebSocket socket, CancellationToken token) {
            // Discord Gateway: Hello (10), unauthenticated Heartbeat (1), ACK (11). No account token is needed.
            var hello = Parse(await Receive(socket, token)); object op, data, interval;
            var payload = hello.TryGetValue("d", out data) ? data as Dictionary<string,object> : null;
            if (!hello.TryGetValue("op", out op) || !(op is int) || (int)op != 10 || payload == null || !payload.TryGetValue("heartbeat_interval", out interval) || !(interval is int) || (int)interval <= 0)
                throw new InvalidDataException("Gateway не вернул Hello");
            await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("{\"op\":1,\"d\":null}")), WebSocketMessageType.Text, true, token);
            var ack = Parse(await Receive(socket, token));
            if (!ack.TryGetValue("op", out op) || !(op is int) || (int)op != 11) throw new InvalidDataException("Gateway не подтвердил heartbeat");
        }
        static async Task CheckGateway(Uri url, CancellationToken token) {
            using (var socket = new ClientWebSocket()) {
                socket.Options.Proxy = null;
                using (token.Register(() => socket.Abort())) {
                    await socket.ConnectAsync(url, token);
                    await VerifyGateway(socket, token);
                }
                // Do not wait for a close handshake during a bounded connectivity probe.
                socket.Abort();
            }
        }
        public static CheckResult FromException(Exception error, long elapsed) {
            string failure = "connection", detail = "Ошибка подключения";
            for (var ex = error; ex != null; ex = ex.InnerException) {
                var web = ex as WebException; var socket = ex as SocketException;
                if ((web != null && (web.Status == WebExceptionStatus.NameResolutionFailure || web.Status == WebExceptionStatus.ProxyNameResolutionFailure)) ||
                    (socket != null && (socket.SocketErrorCode == SocketError.HostNotFound || socket.SocketErrorCode == SocketError.NoData || socket.SocketErrorCode == SocketError.TryAgain))) {
                    failure = "dns"; detail = "DNS · имя не найдено"; break;
                }
                if (ex is AuthenticationException || (web != null && (web.Status == WebExceptionStatus.TrustFailure || web.Status == WebExceptionStatus.SecureChannelFailure))) { failure = "tls"; detail = "Ошибка TLS"; }
            }
            return new CheckResult { State = 0, Failure = failure, Detail = detail + " | " + error.GetBaseException().Message, Milliseconds = elapsed };
        }
        public static async Task<CheckResult[]> Batch(CancellationToken token, Action<int, CheckResult> completed, int timeoutSeconds = 12, Func<ServiceTarget,bool> enabled=null,CheckResult telegramSnapshot=null) {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.DnsRefreshTimeout = 0;
            return await Task.WhenAll(Targets.Select(async (target,index) => {
                var result = enabled!=null && !enabled(target)?new CheckResult{State=3,Detail="Обход сервиса выключен · проба пропущена"}:target.Kind==ProbeKind.Telegram && telegramSnapshot!=null?telegramSnapshot:await Run(target, token, timeoutSeconds);
                token.ThrowIfCancellationRequested();
                if (completed != null) completed(index,result);
                return result;
            }));
        }
    }
}
