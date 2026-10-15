using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ZapretDesktop {
    internal sealed class DnsAnswer {
        public IPAddress[] Addresses=new IPAddress[0];
        public int Ttl;
    }
    // Explicit per-request resolver. This never changes a Windows adapter,
    // browser preference, hosts file, proxy setting or certificate policy.
    internal sealed class DnsOverHttps : IDisposable {
        readonly HttpClient client;
        internal DnsOverHttps(HttpMessageHandler handler=null) {
            client=new HttpClient(handler??new HttpClientHandler{UseProxy=false,SslProtocols=System.Security.Authentication.SslProtocols.Tls12}){Timeout=TimeSpan.FromSeconds(7),MaxResponseContentBufferSize=65536};
        }
        internal static string Host(string value) {
            if(String.IsNullOrWhiteSpace(value) || value.Length>253 || value.EndsWith(".",StringComparison.Ordinal))throw new InvalidDataException("Некорректное имя DNS.");
            string host=new System.Globalization.IdnMapping().GetAscii(value).ToLowerInvariant();IPAddress literal;
            if(IPAddress.TryParse(host,out literal) || Uri.CheckHostName(host)!=UriHostNameType.Dns || host.IndexOf('.')<1)throw new InvalidDataException("Нужно доменное имя.");
            return host;
        }
        internal static bool Public(IPAddress address) {
            if(address.AddressFamily!=AddressFamily.InterNetwork)return false;
            var b=address.GetAddressBytes();
            return !(b[0]==0 || b[0]==10 || b[0]==127 || b[0]>=224 || b[0]==169 && b[1]==254 || b[0]==172 && b[1]>=16 && b[1]<=31 || b[0]==192 && (b[1]==168 || b[1]==0 && (b[2]==0 || b[2]==2) || b[1]==88 && b[2]==99) || b[0]==198 && b[1]==51 && b[2]==100 || b[0]==203 && b[1]==0 && b[2]==113 || b[0]==100 && b[1]>=64 && b[1]<=127 || b[0]==198 && (b[1]==18 || b[1]==19));
        }
        internal static DnsAnswer Parse(string json) {
            if(json==null || json.Length>65536)throw new InvalidDataException("Ответ DNS слишком большой.");
            var value=new JavaScriptSerializer{MaxJsonLength=65536}.Deserialize<Dictionary<string,object>>(json);object status,raw;
            if(value==null || !value.TryGetValue("Status",out status) || Convert.ToInt32(status)!=0 || !value.TryGetValue("Answer",out raw) || !(raw is ArrayList))throw new IOException("DNS через HTTPS не вернул адрес.");
            var addresses=new List<IPAddress>();int ttl=300;
            foreach(var item in (ArrayList)raw) {
                var record=item as Dictionary<string,object>;object type,data,lifetime;IPAddress address;
                if(record==null || !record.TryGetValue("type",out type) || Convert.ToInt32(type)!=1 || !record.TryGetValue("data",out data) || !(data is string) || !IPAddress.TryParse((string)data,out address) || !Public(address))continue;
                addresses.Add(address);
                if(record.TryGetValue("TTL",out lifetime))ttl=Math.Min(ttl,Math.Max(0,Convert.ToInt32(lifetime)));else ttl=0;
            }
            if(addresses.Count==0)throw new IOException("DNS через HTTPS не вернул публичный IPv4-адрес.");
            return new DnsAnswer{Addresses=addresses.Distinct().Take(16).ToArray(),Ttl=ttl};
        }
        internal async Task<DnsAnswer> Resolve(string host,CancellationToken token) {
            host=Host(host);
            using(var response=await client.GetAsync("https://dns.google/resolve?name="+Uri.EscapeDataString(host)+"&type=A",token).ConfigureAwait(false)) {
                response.EnsureSuccessStatusCode();return Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
        }
        public void Dispose(){client.Dispose();}
    }
}
