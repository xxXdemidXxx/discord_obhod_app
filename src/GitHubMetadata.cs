using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
namespace ZapretDesktop {
    public sealed class UpdateCheckException : IOException {
        public UpdateCheckException(string message) : base(message) { }
    }
    public static class GitHubMetadata {
        static readonly Dictionary<string,DateTimeOffset> blocked=new Dictionary<string,DateTimeOffset>();
        public static async Task<string> Get(HttpClient client,string url,bool allowMissing=false) {
            DateTimeOffset until;
            lock(blocked)if(blocked.TryGetValue(url,out until) && until>DateTimeOffset.UtcNow)
                throw new UpdateCheckException("GitHub временно ограничил проверку. Попробуйте позже.");
            using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15))) {
                try {
                    using(var response=await client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,timeout.Token)) {
                        if(allowMissing && response.StatusCode==HttpStatusCode.NotFound)return null;
                        if(response.StatusCode==HttpStatusCode.Forbidden || (int)response.StatusCode==429) {
                            var body=await response.Content.ReadAsStringAsync();IEnumerable<string> values;
                            bool limited=(int)response.StatusCode==429 || body.IndexOf("rate limit",StringComparison.OrdinalIgnoreCase)>=0 || (response.Headers.TryGetValues("X-RateLimit-Remaining",out values) && String.Join("",values)=="0");
                            if(limited) {
                                until=DateTimeOffset.UtcNow.AddMinutes(1);long reset;
                                if(response.Headers.RetryAfter!=null){if(response.Headers.RetryAfter.Delta.HasValue)until=DateTimeOffset.UtcNow+response.Headers.RetryAfter.Delta.Value;else if(response.Headers.RetryAfter.Date.HasValue)until=response.Headers.RetryAfter.Date.Value;}
                                else if(response.Headers.TryGetValues("X-RateLimit-Reset",out values) && Int64.TryParse(String.Join("",values),out reset))try{until=new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero).AddSeconds(reset);}catch(ArgumentOutOfRangeException){}
                                if(until<DateTimeOffset.UtcNow.AddSeconds(1))until=DateTimeOffset.UtcNow.AddMinutes(1);
                                lock(blocked)blocked[url]=until;
                                throw new UpdateCheckException("GitHub временно ограничил проверку. Попробуйте позже.");
                            }
                        }
                        if(!response.IsSuccessStatusCode)throw new UpdateCheckException("Не удалось проверить обновления. Попробуйте позже.");
                        if(response.Content.Headers.ContentLength>1024*1024)throw new InvalidDataException("Описание обновления слишком большое.");
                        using(var input=await response.Content.ReadAsStreamAsync())using(var output=new MemoryStream()) {
                            var buffer=new byte[8192];int read;
                            while((read=await input.ReadAsync(buffer,0,buffer.Length,timeout.Token))>0){if(output.Length+read>1024*1024)throw new InvalidDataException("Описание обновления слишком большое.");output.Write(buffer,0,read);}
                            return System.Text.Encoding.UTF8.GetString(output.ToArray()).TrimStart('\ufeff');
                        }
                    }
                }catch(OperationCanceledException){throw new UpdateCheckException("Проверка не ответила. Попробуйте позже.");}
            }
        }
    }
}
