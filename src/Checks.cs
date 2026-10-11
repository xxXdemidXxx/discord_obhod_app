using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretDesktop {
    public sealed class ServiceTarget {
        public string Name, Url;
        public ServiceTarget(string name, string url) { Name = name; Url = url; }
    }
    public sealed class CheckResult {
        public int State; // 0: unavailable; 1: successful HTTP response; 2: server replied with an error
        public string Detail; public long Milliseconds;
        public string Failure = "";
    }
    public static class ServiceChecks {
        public static readonly ServiceTarget[] Targets = {
            new ServiceTarget("YouTube · сайт", "https://www.youtube.com/"),
            new ServiceTarget("YouTube · изображения", "https://i.ytimg.com/vi/jNQXAC9IVRw/hqdefault.jpg"),
            new ServiceTarget("Discord · сайт", "https://discord.com/"),
            new ServiceTarget("Discord · API", "https://discord.com/api/v10/gateway"),
            new ServiceTarget("GitHub · обновления", "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest")
        };
        public static async Task<CheckResult> Run(ServiceTarget target, CancellationToken token, int timeoutSeconds = 12) {
            var watch = Stopwatch.StartNew();
            using (var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = true })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) }) {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Obhod/"+ShellUpdates.Current);
                try {
                    using (var response = await client.GetAsync(target.Url, HttpCompletionOption.ResponseHeadersRead, token)) {
                        return new CheckResult { State = response.IsSuccessStatusCode ? 1 : 2,
                            Detail = response.IsSuccessStatusCode ? "Доступен · HTTP " + (int)response.StatusCode : "Ответ сервера · HTTP " + (int)response.StatusCode,
                            Milliseconds = watch.ElapsedMilliseconds };
                    }
                } catch (OperationCanceledException) {
                    return new CheckResult { State = 0, Detail = token.IsCancellationRequested ? "Отменено" : "Тайм-аут · "+timeoutSeconds+" секунд", Failure = token.IsCancellationRequested ? "cancelled" : "timeout", Milliseconds = watch.ElapsedMilliseconds };
                } catch (HttpRequestException ex) {
                    return FromException(ex, watch.ElapsedMilliseconds);
                }
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
                if (ex is AuthenticationException || (web != null && (web.Status == WebExceptionStatus.TrustFailure || web.Status == WebExceptionStatus.SecureChannelFailure))) {failure = "tls"; detail = "Ошибка TLS";}
            }
            return new CheckResult {State = 0, Failure = failure, Detail = detail + " | " + error.GetBaseException().Message, Milliseconds = elapsed};
        }
        public static async Task<CheckResult[]> Batch(CancellationToken token, Action<int, CheckResult> completed, int timeoutSeconds = 12) {
            // Do not reuse an HTTP connection or .NET's cached DNS between strategies.
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.DnsRefreshTimeout = 0;
            return await Task.WhenAll(Targets.Select(async (target,index) => {
                var result = await Run(target, token, timeoutSeconds);
                token.ThrowIfCancellationRequested();
                if (completed != null) completed(index,result);
                return result;
            }));
        }
    }
}
