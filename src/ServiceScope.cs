using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ZapretDesktop {
    internal sealed class ScopeOptions {
        public bool Strict=true, YouTube=true, Discord=true, Voice=false;
    }
    // Shell-owned allowlist. Never expand to entire Google/Cloudflare networks.
    internal static class ServiceScope {
        internal const string RelativePath="utils/service-scope.json";
        internal const string YouTubeDomains="youtube.com,youtu.be,youtube-nocookie.com,googlevideo.com,ytimg.com,yt3.ggpht.com,yt4.ggpht.com,yt3.googleusercontent.com,youtubei.googleapis.com,youtube.googleapis.com,youtubeembeddedplayer.googleapis.com,jnn-pa.googleapis.com,wide-youtube.l.google.com,youtube-ui.l.google.com,yt-video-upload.l.google.com,ytimg.l.google.com";
        internal const string DiscordDomains="discord.com,discord.gg,discordapp.com,discordapp.net,discord.media,discordcdn.com,discord.gift,discordstatus.com,dis.gd,discord-attachments-uploads-prd.storage.googleapis.com";
        internal static ScopeOptions Read(string engine) {
            string path=Path.Combine(engine,RelativePath);if(!File.Exists(path))return null;
            Trust.NoLinks(path);if(new FileInfo(path).Length>2048)throw new InvalidDataException("Повреждены ограничения сервисов.");
            return new JavaScriptSerializer().Deserialize<ScopeOptions>(File.ReadAllText(path))??new ScopeOptions();
        }
        internal static void Write(string engine,ScopeOptions options) {
            if(!options.YouTube && !options.Discord)throw new InvalidDataException("Выберите YouTube или Discord.");
            string path=Path.Combine(engine,RelativePath),temp=path+".tmp";Trust.NoLinks(path);Trust.NoLinks(temp);Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(temp,new JavaScriptSerializer().Serialize(options),new UTF8Encoding(false));
            if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
        }
        internal static string Domains(ScopeOptions options) {return String.Join(",",new[]{options.YouTube?YouTubeDomains:"",options.Discord?DiscordDomains:""}.Where(x=>x.Length>0));}
        internal static bool Allows(string host,ScopeOptions options) {
            host=host.TrimEnd('.').ToLowerInvariant();
            return Domains(options).Split(',').Any(x=>host==x || host.EndsWith("."+x,StringComparison.Ordinal));
        }
        internal static void Apply(List<string> tokens,ScopeOptions options) {
            if(options==null || !options.Strict)return;
            if(!options.YouTube && !options.Discord)throw new InvalidDataException("Нет включённых сервисов.");
            int first=tokens.FindIndex(x=>x.StartsWith("--filter-",StringComparison.Ordinal));
            if(first<0 || tokens.Any(x=>x.StartsWith("--wf-raw",StringComparison.Ordinal)))throw new InvalidDataException("Стратегия не поддерживает ограничение сервисов.");
            var result=tokens.Take(first).Select(x=>x.StartsWith("--wf-tcp=")?"--wf-tcp=80,443"+(options.Discord?",2053,2083,2087,2096,8443":""):x.StartsWith("--wf-udp=")?"--wf-udp=443"+(options.Discord && options.Voice?",19294-19344,50000-50100":""):x).ToList();
            var profile=new List<string>();int kept=0;
            Action flush=()=>{
                if(profile.Count==0)return;
                bool voice=profile.Any(x=>x.StartsWith("--filter-l7=discord"));
                bool ipset=profile.Any(x=>x.StartsWith("--ipset="));
                bool google=profile.Any(x=>x.StartsWith("--hostlist=") && x.IndexOf("list-google.txt",StringComparison.OrdinalIgnoreCase)>=0);
                bool general=profile.Any(x=>x.StartsWith("--hostlist=") && x.IndexOf("list-general",StringComparison.OrdinalIgnoreCase)>=0);
                bool media=profile.Contains("--hostlist-domains=discord.media");
                if(ipset || voice && (!options.Discord || !options.Voice) || google && !options.YouTube || media && !options.Discord){profile.Clear();return;}
                if(!voice && !google && !general && !media){profile.Clear();return;}
                var clean=profile.Where(x=>!x.StartsWith("--hostlist=") && !x.StartsWith("--hostlist-domains=")).ToList();
                for(int i=clean.Count-1;i>=0;i--)if(clean[i].StartsWith("--dpi-desync-fooling=")){
                    var parts=clean[i].Substring("--dpi-desync-fooling=".Length).Split(',').Where(x=>x!="ts").ToArray();
                    if(parts.Length==0)clean.RemoveAt(i);else clean[i]="--dpi-desync-fooling="+String.Join(",",parts);
                }
                if(!voice)clean.Add("--hostlist-domains="+(media?"discord.media":google?YouTubeDomains:Domains(options)));
                if(kept++>0)result.Add("--new");result.AddRange(clean);profile.Clear();
            };
            foreach(string token in tokens.Skip(first)){if(token=="--new")flush();else profile.Add(token);}flush();
            if(kept==0)throw new InvalidDataException("В стратегии нет профиля для выбранных сервисов.");
            tokens.Clear();tokens.AddRange(result);
        }
    }
}
