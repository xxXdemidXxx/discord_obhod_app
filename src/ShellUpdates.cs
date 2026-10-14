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
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ByPass/"+Current);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }
        public static Release Parse(string json) {
            return ParseVerified(json,true);
        }
        static Release ParseVerified(string json,bool newerOnly) {
            var data=Trust.Verify(json);
            if(Convert.ToInt32(data["schema"])!=1)throw new InvalidDataException("Неизвестный формат обновления.");
            var files=(Dictionary<string,object>)data["shell_files"];
            foreach(var key in new[]{"exe","config"})if(!Regex.IsMatch(Convert.ToString(files[key]),@"^[a-f0-9]{64}$"))throw new InvalidDataException("Неправильный хеш файлов оболочки.");
            if(Convert.ToBoolean(data["draft"]) || Convert.ToBoolean(data["prerelease"]))return null;
            string tag=Convert.ToString(data["tag_name"]);
            if(!Regex.IsMatch(tag,@"^v\d+\.\d+\.\d+$"))throw new InvalidDataException("Некорректная версия релиза оболочки.");
            var version=new Version(tag.Substring(1));
            if(newerOnly && version<=new Version(Current))return null;
            string versionedName="Obhod-"+tag.Substring(1)+"-win-x64.zip";
            foreach(Dictionary<string,object> asset in (ArrayList)data["assets"]) {
                string name=Convert.ToString(asset["name"]);
                if(name!="ByPass-win-x64.zip" && name!=versionedName)continue;
                var release=new Release{Version=tag.Substring(1),Url=Convert.ToString(asset["browser_download_url"]),Size=Convert.ToInt64(asset["size"]),Digest=asset.ContainsKey("digest")?Convert.ToString(asset["digest"]):"",Proof=json};
                if(release.Url!="https://github.com/"+Repository+"/releases/download/"+tag+"/"+name || !Regex.IsMatch(release.Digest??"",@"^sha256:[a-fA-F0-9]{64}$") || release.Size<=0 || release.Size>64*1024*1024)throw new InvalidDataException("Релиз оболочки не прошёл проверку источника, размера или SHA-256.");
                return release;
            }
            throw new InvalidDataException("В релизе нет готовой Windows-сборки.");
        }
        public async Task<Release> Check() {
            // Published next to the ZIP, with its exact URL, size and SHA-256.
            // Release downloads do not consume the public REST API quota.
            var manifest=await GitHubMetadata.Get(client,"https://github.com/"+Repository+"/releases/latest/download/update.json",true);
            if(manifest==null)throw new InvalidDataException("Для последнего выпуска нет подписанных данных обновления. Попробуйте позже.");
            return Parse(manifest);
        }
        public static void ValidateBinary(string path,string version) {
            var name=AssemblyName.GetAssemblyName(path);
            if(name.Name!="Obhod" && !name.Name.StartsWith("Obhod-",StringComparison.Ordinal))throw new InvalidDataException("Архив содержит другое приложение.");
            if(name.Version.ToString(3)!=version)throw new InvalidDataException("Версия EXE отличается от версии релиза.");
        }
        public Task<string> Stage(Release release,string root) {return Stage(release,root,null,CancellationToken.None);}
        public async Task<string> Stage(Release release,string root,IProgress<int> progress,CancellationToken token) {
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)) {
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                try{return await StageCore(release,root,progress,timeout.Token);}
                catch(OperationCanceledException){token.ThrowIfCancellationRequested();throw new IOException("Загрузка обновления не ответила.");}
            }
        }
        async Task<string> StageCore(Release release,string root,IProgress<int> progress,CancellationToken token) {
            token.ThrowIfCancellationRequested();
            var trusted=ParseVerified(release.Proof,false);
            if(trusted==null || release.Version!=trusted.Version || release.Digest!=trusted.Digest || release.Url!=trusted.Url || release.Size!=trusted.Size)throw new InvalidDataException("Параметры обновления отличаются от подписанного выпуска.");
            Trust.NoLinks(root);
            string stage=Path.Combine(root,"data","app-updates",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
            try {
                string zip=Path.Combine(stage,"update.zip");
                using(var response=await client.GetAsync(release.Url,HttpCompletionOption.ResponseHeadersRead,token)) {
                    response.EnsureSuccessStatusCode();
                    using(var input=await response.Content.ReadAsStreamAsync())using(var output=File.Create(zip)) {
                        var buffer=new byte[81920];int read,lastPercent=-1;long total=0;
                        while((read=await input.ReadAsync(buffer,0,buffer.Length,token))>0){total+=read;if(total>release.Size)throw new InvalidDataException("Архив превышает объявленный размер.");await output.WriteAsync(buffer,0,read,token);int percent=(int)(total*100/release.Size);if(progress!=null && percent!=lastPercent){lastPercent=percent;progress.Report(percent);}}
                        if(total!=release.Size)throw new InvalidDataException("Загрузка обновления неполная.");
                    }
                }
                token.ThrowIfCancellationRequested();
                if(!String.Equals("sha256:"+Updater.Hash(zip),release.Digest,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("SHA-256 оболочки не совпадает.");
                string unpacked=Path.Combine(stage,"unpacked");await Task.Run(()=>Updater.Extract(zip,unpacked));
                token.ThrowIfCancellationRequested();
                string executable=Path.Combine(unpacked,"ByPass","ByPass.exe");
                if(!File.Exists(executable))executable=Path.Combine(unpacked,"Obhod","Obhod.exe");
                ValidateBinary(executable,release.Version);
                // Only the shell is replaced. All settings and engine versions remain in place.
                string prepared=Path.Combine(stage,"ByPass.exe");File.Copy(executable,prepared);
                File.Copy(executable+".config",prepared+".config");
                string ownerProof=File.ReadAllText(Path.Combine(Path.GetDirectoryName(executable),OwnerIdentity.ProofName));
                OwnerIdentity.Verify(ownerProof,prepared,prepared+".config",release.Version);
                File.WriteAllText(Path.Combine(stage,OwnerIdentity.ProofName),ownerProof);
                Trust.VerifyShell(release.Proof,prepared,prepared+".config");
                File.WriteAllText(Path.Combine(stage,"release-proof.json"),release.Proof);
                Directory.Delete(unpacked,true);File.Delete(zip);
                return prepared;
            }catch{try{Directory.Delete(stage,true);}catch{}throw;}
        }
        static string InstallationRoot(string prepared,string target) {
            string root=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(prepared),"..","..",".."));
            target=Path.GetFullPath(target);
            if(!String.Equals(Path.GetDirectoryName(target),root,StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(Path.GetFileName(target),@"^(?:ByPass|Obhod)(?:-[0-9.]+)?\.exe$",RegexOptions.IgnoreCase))throw new InvalidDataException("Неправильный путь установки приложения.");
            return root;
        }
        static void ValidatePrepared(string source) {
            string stage=Path.GetDirectoryName(source);
            string proofPath=Path.Combine(stage,"release-proof.json");Trust.NoLinks(proofPath);
            if(!File.Exists(proofPath) || new FileInfo(proofPath).Length>1024*1024)throw new InvalidDataException("Нет подписанного подтверждения обновления.");
            string proof=File.ReadAllText(proofPath);var release=ParseVerified(proof,false);
            if(release==null)throw new InvalidDataException("Неправильный выпуск.");
            Trust.VerifyShell(proof,source,source+".config");ValidateBinary(source,release.Version);
            OwnerIdentity.Verify(File.ReadAllText(Path.Combine(stage,OwnerIdentity.ProofName)),source,source+".config",release.Version);
        }
#if TESTING
        internal static Func<string,Task<string>> LegacyProofForTest = null;
#endif
        static async Task RecoverLegacyProof(string source) {
            string stage=Path.GetDirectoryName(source);
            if(File.Exists(Path.Combine(stage,"release-proof.json")))return;
            // 0.3/0.4 stage only EXE/config and plain hashes. Those hashes are
            // hints for compatibility, never a trust anchor. Fetch owner-signed
            // receipts for the helper's exact version before acknowledging it.
            foreach(string file in new[]{"verified.sha256","config.sha256","version.txt"}) {
                string path=Path.Combine(stage,file);Trust.NoLinks(path);
                if(!File.Exists(path) || new FileInfo(path).Length>128)throw new InvalidDataException("Нет подписанного подтверждения обновления.");
            }
            if(File.ReadAllText(Path.Combine(stage,"version.txt")).Trim()!=Current)throw new InvalidDataException("Неправильная версия старого обновления.");
            using(var http=new HttpClient{Timeout=TimeSpan.FromSeconds(3)}) {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ByPass/"+Current);
                string prefix="https://github.com/"+Repository+"/releases/download/v"+Current+"/";
                Func<string,Task<string>> fetch=url=>GitHubMetadata.Get(http,url);
#if TESTING
                if(LegacyProofForTest!=null)fetch=LegacyProofForTest;
#endif
                string proof=await fetch(prefix+"update.json");var release=ParseVerified(proof,false);
                if(release==null || release.Version!=Current)throw new InvalidDataException("Подписанный выпуск не соответствует обновлению.");
                Trust.VerifyShell(proof,source,source+".config");
                string identity=await fetch(prefix+OwnerIdentity.ProofName);OwnerIdentity.Verify(identity,source,source+".config",Current);
                File.WriteAllText(Path.Combine(stage,OwnerIdentity.ProofName),identity);
                File.WriteAllText(Path.Combine(stage,"release-proof.json"),proof);
            }
        }
        public static async Task Launch(string prepared,string target,bool reconnect,bool tray=false) {
            using(var executableLock=new FileStream(prepared,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var configLock=new FileStream(prepared+".config",FileMode.Open,FileAccess.Read,FileShare.Read)) {
            InstallationRoot(prepared,target);ValidatePrepared(prepared);
            string ready=Path.Combine(Path.GetDirectoryName(prepared),"helper-ready.txt");
            if(File.Exists(ready))File.Delete(ready);
            using(var helper=Process.Start(new ProcessStartInfo(prepared,"--apply-update "+Process.GetCurrentProcess().Id+" "+Strategy.Quote(target)+(reconnect?" --connect":"")+(tray?" --tray":"")+" --ready"){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(target)})) {
                var watch=Stopwatch.StartNew();
                while(!File.Exists(ready)) {
                    if(helper.HasExited)throw new IOException("Не удалось подготовить установку.");
                    if(watch.ElapsedMilliseconds>10000){try{helper.Kill();}catch{}throw new IOException("Установка не ответила.");}
                    await Task.Delay(50);
                }
            }
            }
        }
        public sealed class Outcome {
            public bool Success {get;set;}
            public string Version {get;set;}
            public string Error {get;set;}
        }
        static void WriteOutcome(string root,bool success,string error) {
            var path=Path.Combine(root,"data","app-update-result.json");Directory.CreateDirectory(Path.GetDirectoryName(path));
            var json=new JavaScriptSerializer().Serialize(new Outcome{Success=success,Version=Current,Error=error??""});
            File.WriteAllText(path+".new",json);
            if(File.Exists(path))File.Replace(path+".new",path,null);else File.Move(path+".new",path);
        }
        public static Outcome TakeOutcome(string root) {
            string path=Path.Combine(root,"data","app-update-result.json");
            try {
                if(!File.Exists(path))return null;
                if(new FileInfo(path).Length>8192){File.Delete(path);return null;}
                var result=new JavaScriptSerializer().Deserialize<Outcome>(File.ReadAllText(path));File.Delete(path);return result;
            }catch{return null;}
        }
        public static void Apply(string[] args) {
            using(var executableLock=new FileStream(Application.ExecutablePath,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var configLock=new FileStream(Application.ExecutablePath+".config",FileMode.Open,FileAccess.Read,FileShare.Read)) {
            string target=null,root=null,backup=null;bool replaced=false,configReplaced=false,configCreated=false,proofReplaced=false;string configBackup=null,oldProof=null;
            bool handshake=args.Contains("--ready"),reconnect=args.Contains("--connect"),tray=args.Contains("--tray");int pid=0;
            try {
                pid=Int32.Parse(args[1]);string source=Application.ExecutablePath;
                root=InstallationRoot(source,args[2]);target=Path.GetFullPath(args[2]);
                RecoverLegacyProof(source).GetAwaiter().GetResult();ValidatePrepared(source);
                if(handshake)File.WriteAllText(Path.Combine(Path.GetDirectoryName(source),"helper-ready.txt"),"ready");
                WaitForExit(pid);
                backup=target+".previous";configBackup=target+".config.previous";
                var temp=target+".new";File.Copy(source,temp,true);
                for(int attempt=0;;attempt++) {try{File.Replace(temp,target,backup,true);replaced=true;break;}catch(IOException){if(attempt>=30)throw;Thread.Sleep(200);}}
                File.Copy(source+".config",target+".config.new",true);
                if(File.Exists(target+".config")){File.Replace(target+".config.new",target+".config",configBackup,true);configReplaced=true;}
                else {File.Move(target+".config.new",target+".config");configCreated=true;}
                string identity=Path.Combine(root,OwnerIdentity.ProofName);oldProof=File.Exists(identity)?File.ReadAllText(identity):null;
                File.WriteAllText(identity,File.ReadAllText(Path.Combine(Path.GetDirectoryName(source),OwnerIdentity.ProofName)));proofReplaced=true;
                WriteOutcome(root,true,null);
                Process.Start(new ProcessStartInfo(target,(reconnect?"--connect":"")+(tray?" --tray":"")){UseShellExecute=true,WorkingDirectory=root});
            }catch(Exception ex) {
                try {if(proofReplaced){string identity=Path.Combine(root,OwnerIdentity.ProofName);if(oldProof==null)File.Delete(identity);else File.WriteAllText(identity,oldProof);}if(configReplaced)File.Copy(configBackup,target+".config",true);else if(configCreated)File.Delete(target+".config");if(replaced)File.Copy(backup,target,true);}catch{}
                // The existing application displays the result in its Updates tab.
                // A helper must never open an error dialog or expose internal details.
                bool parentAlive=false;try{using(var parent=Process.GetProcessById(pid))parentAlive=!parent.HasExited;}catch{}
                if(root!=null && target!=null) {
                    try{WriteOutcome(root,false,ex.Message);}catch{}
                    if(!handshake || !parentAlive) {
                        try{WaitForExit(pid);if(File.Exists(target))Process.Start(new ProcessStartInfo(target,(reconnect?"--connect":"")+(tray?" --tray":"")){UseShellExecute=true,WorkingDirectory=root});}catch{}
                    }
                }
            }
            }
        }
        static void WaitForExit(int pid) {
            try {using(var old=Process.GetProcessById(pid)){if(!old.WaitForExit(30000))throw new IOException("Приложение не завершилось для обновления.");}}catch(ArgumentException){}
        }
        public void Dispose(){client.Dispose();}
    }
}
