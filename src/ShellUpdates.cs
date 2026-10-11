using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;



namespace ZapretDesktop {
    public sealed class ShellUpdates : IDisposable {
        public const string Repository = "xxXdemidXxx/discord_obhod_app";
        public static string Current {get{return Assembly.GetExecutingAssembly().GetName().Version.ToString(3);}}
        readonly HttpClient client;
        public ShellUpdates() : this(null) { }
        public ShellUpdates(HttpMessageHandler handler) {
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            client=handler==null?new HttpClient():new HttpClient(handler);client.Timeout=TimeSpan.FromMinutes(3);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Obhod/"+Current);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }
        public static Release Parse(string json) {
            var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
            if(Convert.ToBoolean(data["draft"]) || Convert.ToBoolean(data["prerelease"]))return null;
            string tag=Convert.ToString(data["tag_name"]);
            if(!Regex.IsMatch(tag,@"^v\d+\.\d+\.\d+$"))throw new InvalidDataException("Некорректная версия релиза оболочки.");
            var version=new Version(tag.Substring(1));
            if(version<=new Version(Current))return null;
            string name="Obhod-"+tag.Substring(1)+"-win-x64.zip";
            foreach(Dictionary<string,object> asset in (ArrayList)data["assets"]) {
                if(Convert.ToString(asset["name"])!=name)continue;
                var release=new Release{Version=tag.Substring(1),Url=Convert.ToString(asset["browser_download_url"]),Size=Convert.ToInt64(asset["size"]),Digest=asset.ContainsKey("digest")?Convert.ToString(asset["digest"]):""};
                if(release.Url!="https://github.com/"+Repository+"/releases/download/"+tag+"/"+name || !Regex.IsMatch(release.Digest??"",@"^sha256:[a-fA-F0-9]{64}$") || release.Size<=0 || release.Size>64*1024*1024)throw new InvalidDataException("Релиз оболочки не прошёл проверку источника, размера или SHA-256.");
                return release;
            }
            throw new InvalidDataException("В релизе нет готовой Windows-сборки.");
        }
        public async Task<Release> Check() {
            // Published next to the ZIP, with its exact URL, size and SHA-256.
            // Release downloads do not consume the public REST API quota.
            var manifest=await GitHubMetadata.Get(client,"https://github.com/"+Repository+"/releases/latest/download/update.json",true);
            if(manifest!=null)return Parse(manifest);
            var json=await GitHubMetadata.Get(client,"https://api.github.com/repos/"+Repository+"/releases/latest",true);
            return json==null?null:Parse(json);
        }
        public static void ValidateBinary(string path,string version) {
            var name=AssemblyName.GetAssemblyName(path);
            if(name.Name!="Obhod" && !name.Name.StartsWith("Obhod-",StringComparison.Ordinal))throw new InvalidDataException("Архив содержит другое приложение.");
            if(name.Version.ToString(3)!=version)throw new InvalidDataException("Версия EXE отличается от версии релиза.");
        }
        public async Task<string> Stage(Release release,string root) {
            string stage=Path.Combine(root,"data","app-updates",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
            try {
                string zip=Path.Combine(stage,"update.zip");
                using(var response=await client.GetAsync(release.Url,HttpCompletionOption.ResponseHeadersRead)) {
                    response.EnsureSuccessStatusCode();
                    using(var input=await response.Content.ReadAsStreamAsync())using(var output=File.Create(zip)) {
                        var buffer=new byte[81920];int read;long total=0;
                        while((read=await input.ReadAsync(buffer,0,buffer.Length))>0){total+=read;if(total>release.Size)throw new InvalidDataException("Архив превышает объявленный размер.");await output.WriteAsync(buffer,0,read);}
                        if(total!=release.Size)throw new InvalidDataException("Загрузка обновления неполная.");
                    }
                }
                if(!String.Equals("sha256:"+Updater.Hash(zip),release.Digest,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("SHA-256 оболочки не совпадает.");
                string unpacked=Path.Combine(stage,"unpacked");await Task.Run(()=>Updater.Extract(zip,unpacked));
                string executable=Path.Combine(unpacked,"Obhod","Obhod.exe");ValidateBinary(executable,release.Version);
                // Only the shell is replaced. All settings and engine versions remain in place.
                string prepared=Path.Combine(stage,"Obhod.exe");File.Copy(executable,prepared);
                File.Copy(executable+".config",prepared+".config");
                File.WriteAllText(Path.Combine(stage,"verified.sha256"),Updater.Hash(prepared));
                File.WriteAllText(Path.Combine(stage,"version.txt"),release.Version);
                File.WriteAllText(Path.Combine(stage,"config.sha256"),Updater.Hash(prepared+".config"));
                Directory.Delete(unpacked,true);File.Delete(zip);
                return prepared;
            }catch{try{Directory.Delete(stage,true);}catch{}throw;}
        }
        public static void Launch(string prepared,string target,bool reconnect) {
            Process.Start(new ProcessStartInfo(prepared,"--apply-update "+Process.GetCurrentProcess().Id+" "+Strategy.Quote(target)+(reconnect?" --connect":"")){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(target)});
        }
        public static void Apply(string[] args) {
            string target=null, backup=null;bool replaced=false,configReplaced=false;string configBackup=null;
            try {
                string stage=AppDomain.CurrentDomain.BaseDirectory;
                string root=Path.GetFullPath(Path.Combine(stage,"..","..",".."));
                target=Path.GetFullPath(args[2]);
                if(!String.Equals(Path.GetDirectoryName(target),root,StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("Obhod",StringComparison.OrdinalIgnoreCase) || Path.GetExtension(target)!=".exe")throw new InvalidDataException("Неправильный путь установки оболочки.");
                string source=Application.ExecutablePath;
                if(Updater.Hash(source)!=File.ReadAllText(Path.Combine(stage,"verified.sha256")).Trim())throw new InvalidDataException("Файлы обновления изменились после проверки.");
                ValidateBinary(source,File.ReadAllText(Path.Combine(stage,"version.txt")).Trim());
                if(Updater.Hash(source+".config")!=File.ReadAllText(Path.Combine(stage,"config.sha256")).Trim())throw new InvalidDataException("Конфигурация обновления изменилась после проверки.");
                int pid=Int32.Parse(args[1]);
                try {using(var old=Process.GetProcessById(pid)){if(!old.WaitForExit(30000))throw new IOException("Приложение не завершилось для обновления.");}}catch(ArgumentException){}
                backup=target+".previous";configBackup=target+".config.previous";
                var temp=target+".new";File.Copy(source,temp,true);
                for(int attempt=0;;attempt++) {try{File.Replace(temp,target,backup,true);replaced=true;break;}catch(IOException){if(attempt>=30)throw;Thread.Sleep(200);}}
                File.Copy(source+".config",target+".config.new",true);
                if(File.Exists(target+".config")){File.Replace(target+".config.new",target+".config",configBackup,true);configReplaced=true;}
                else File.Move(target+".config.new",target+".config");
                Process.Start(new ProcessStartInfo(target,args.Contains("--connect")?"--connect":""){UseShellExecute=true,WorkingDirectory=root});
            }catch(Exception ex) {
                try {if(configReplaced)File.Copy(configBackup,target+".config",true);if(replaced)File.Copy(backup,target,true);}catch{}
                MessageBox.Show("Обновление оболочки не установлено.\n"+ex.Message,"Обход",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                if(target!=null && File.Exists(target))try{Process.Start(new ProcessStartInfo(target){UseShellExecute=true});}catch{}
            }
        }
        public void Dispose(){client.Dispose();}
    }
}
