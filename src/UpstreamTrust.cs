using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ZapretDesktop {
    // New Flowseal releases are authenticated directly against GitHub over TLS.
    // Only the elevated host can persist approvals for subsequent offline starts.
    internal static class UpstreamTrust {
        internal const string ReceiptName=".upstream-release.json",ArchiveName=".upstream-archive.zip";
        internal const string Repository="Flowseal/zapret-discord-youtube";
        static readonly object gate=new object();
        static readonly Dictionary<string,Dictionary<string,object>> releases=new Dictionary<string,Dictionary<string,object>>(),approved=new Dictionary<string,Dictionary<string,object>>();
#if TESTING
        internal static Func<string,string> TestMetadata=null;
        internal static string TestApprovalRoot=null;
        internal static Func<string,bool> TestProtected=null;
        internal static void Clear(){lock(gate){releases.Clear();approved.Clear();}}
#endif
        internal static string ArchiveUrl(string version){return "https://github.com/"+Repository+"/releases/download/"+version+"/zapret-discord-youtube-"+version+".zip";}
        internal static string Serialize(object value){return new JavaScriptSerializer().Serialize(value);}
        internal static Dictionary<string,object> ParseRelease(string json,string expected=null) {
            var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
            string version=Convert.ToString(data["tag_name"]);
            if(!Regex.IsMatch(version,@"^\d+\.\d+\.\d+$") || (expected!=null && version!=expected) || (data.ContainsKey("draft") && Convert.ToBoolean(data["draft"])) || (data.ContainsKey("prerelease") && Convert.ToBoolean(data["prerelease"])))throw new InvalidDataException("Неверная версия официального ядра.");
            var assets=((ArrayList)data["assets"]).Cast<Dictionary<string,object>>().Where(x=>Convert.ToString(x["name"])=="zapret-discord-youtube-"+version+".zip").ToArray();
            if(assets.Length!=1)throw new InvalidDataException("В официальном релизе не найден единственный ZIP-архив ядра.");
            var asset=assets[0];string digest=asset.ContainsKey("digest")?Convert.ToString(asset["digest"]):"";long size=Convert.ToInt64(asset["size"]);
            if(Convert.ToString(asset["browser_download_url"])!=ArchiveUrl(version) || !Regex.IsMatch(digest,@"^sha256:[a-fA-F0-9]{64}$") || size<1 || size>64*1024*1024)throw new InvalidDataException("Официальный релиз не содержит корректных адреса, размера и SHA-256.");
            var result=new Dictionary<string,object>{{"version",version},{"archive_sha256",digest.Substring(7).ToLowerInvariant()},{"archive_size",size},{"schema",1},{"origin",Repository}};
            lock(gate)releases[version]=result;return result;
        }
        static Dictionary<string,object> Release(string version) {
            lock(gate){Dictionary<string,object> known;if(releases.TryGetValue(version,out known))return known;}
            string url="https://api.github.com/repos/"+Repository+"/releases/tags/"+version,json;
#if TESTING
            if(TestMetadata!=null)return ParseRelease(TestMetadata(version),version);
#endif
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            var request=(HttpWebRequest)WebRequest.Create(url);request.UserAgent="ByPass-Core";request.Accept="application/vnd.github+json";request.Timeout=15000;request.ReadWriteTimeout=15000;request.AllowAutoRedirect=false;
            using(var response=(HttpWebResponse)request.GetResponse()) {
                if(response.StatusCode!=HttpStatusCode.OK || response.ContentLength>1024*1024)throw new InvalidDataException("Не удалось подтвердить официальный релиз ядра.");
                using(var input=response.GetResponseStream())using(var output=new MemoryStream()) {
                    var buffer=new byte[8192];int count;while((count=input.Read(buffer,0,buffer.Length))>0){if(output.Length+count>1024*1024)throw new InvalidDataException("Описание ядра слишком большое.");output.Write(buffer,0,count);}json=Encoding.UTF8.GetString(output.ToArray());
                }
            }
            return ParseRelease(json,version);
        }
        internal static string ApprovalRoot {get{
#if TESTING
            if(TestApprovalRoot!=null)return TestApprovalRoot;
#endif
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"ByPass");
        }}
        internal static string ApprovalPath(string hash){if(!Regex.IsMatch(hash??"",@"^[a-f0-9]{64}$"))throw new InvalidDataException("Неверный SHA-256 ядра.");return Path.Combine(ApprovalRoot,"approved-core-"+hash+".json");}
        static bool Protected(string path,bool directory) {
#if TESTING
            if(TestProtected!=null)return TestProtected(path);
#endif
            Trust.NoLinks(path);
            FileSystemSecurity acl=directory?(FileSystemSecurity)Directory.GetAccessControl(path):File.GetAccessControl(path);
            if(!acl.AreAccessRulesProtected || new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(),0).DiscretionaryAcl==null)return false;
            var admins=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
            var owner=acl.GetOwner(typeof(SecurityIdentifier));if(!owner.Equals(admins) && !owner.Equals(system))return false;
            const FileSystemRights writes=FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.DeleteSubdirectoriesAndFiles|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership;
            foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
                if(rule.AccessControlType==AccessControlType.Allow && !rule.IdentityReference.Equals(admins) && !rule.IdentityReference.Equals(system) && (rule.FileSystemRights&writes)!=0)return false;
            return true;
        }
        static Dictionary<string,object> Saved(string hash) {
            string path=ApprovalPath(hash);
            if(!File.Exists(path) || !Protected(ApprovalRoot,true) || !Protected(path,false))return null;
            if(new FileInfo(path).Length>1024*1024)throw new InvalidDataException("Каталог ядра слишком большой.");
            var result=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
            if((string)result["archive_sha256"]!=hash || (string)result["origin"]!=Repository || Convert.ToInt32(result["schema"])!=1)throw new InvalidDataException("Повреждён защищённый каталог ядра.");return result;
        }
        static Dictionary<string,object> Catalogue(string root) {
            string receipt=Path.Combine(root,ReceiptName);Trust.NoLinks(receipt);
            if(!File.Exists(receipt) || new FileInfo(receipt).Length>1024*1024)throw new InvalidDataException("Нет подтверждения официального происхождения ядра. Повторите установку из ByPass.");
            var requested=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(receipt));
            string version=(string)requested["version"],hash=(string)requested["archive_sha256"];
            if(!Regex.IsMatch(version??"",@"^\d+\.\d+\.\d+$"))throw new InvalidDataException("Неверная версия ядра.");
            var saved=Saved(hash);if(saved!=null && (string)saved["version"]==version)return saved;
            lock(gate){Dictionary<string,object> known;if(approved.TryGetValue(hash,out known) && (string)known["version"]==version)return known;}
            var release=Release(version);
            if((string)release["archive_sha256"]!=hash || Convert.ToInt64(requested["archive_size"])!=Convert.ToInt64(release["archive_size"]))throw new InvalidDataException("Ядро не соответствует официальному релизу Flowseal.");
            string zip=Path.Combine(root,ArchiveName);Trust.NoLinks(zip);
            if(!File.Exists(zip) || new FileInfo(zip).Length!=Convert.ToInt64(release["archive_size"]) || Trust.Hash(zip)!=hash)throw new InvalidDataException("SHA-256 официального архива ядра не совпадает.");
            var files=new ArrayList();long expanded=0;
            using(var stream=File.OpenRead(zip))using(var archive=new ZipArchive(stream,ZipArchiveMode.Read)) {
                if(archive.Entries.Count>2000)throw new InvalidDataException("Слишком много файлов в архиве ядра.");
                string prefix="";var binaries=archive.Entries.Select(x=>x.FullName.Replace('\\','/')).Where(x=>x=="bin/winws.exe" || x.EndsWith("/bin/winws.exe",StringComparison.Ordinal)).ToArray();
                if(binaries.Length!=1)throw new InvalidDataException("Неоднозначная структура ядра.");prefix=binaries[0].Substring(0,binaries[0].Length-"bin/winws.exe".Length);
                foreach(var item in archive.Entries) {
                    expanded+=item.Length;if(expanded>200*1024*1024)throw new InvalidDataException("Слишком большой архив ядра.");
                    if(item.Name.Length==0)continue;
                    string fullName=item.FullName.Replace('\\','/');
                    if(!fullName.StartsWith(prefix,StringComparison.Ordinal))throw new InvalidDataException("Посторонние файлы за пределами ядра.");
                    string relative=fullName.Substring(prefix.Length);
                    if(relative.Contains("..") || relative.Contains(":") || Path.IsPathRooted(relative) || ((item.ExternalAttributes>>16)&0xF000)==0xA000)throw new InvalidDataException("Недопустимый путь в архиве ядра.");
                    if(Trust.Mutable(relative) || relative=="lists/ipset-all.txt.backup")continue;
                    using(var input=item.Open())using(var sha=System.Security.Cryptography.SHA256.Create())files.Add(new Dictionary<string,object>{{"path",relative},{"size",item.Length},{"sha256",Trust.Hex(sha.ComputeHash(input))}});
                }
            }
            var catalogue=new Dictionary<string,object>(release);catalogue["files"]=files;
            lock(gate)approved[hash]=catalogue;return catalogue;
        }
        internal static Dictionary<string,object> VerifyEngine(string root) {
            var catalogue=Catalogue(root);var allowed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(Dictionary<string,object> item in (ArrayList)catalogue["files"]) {
                string relative=(string)item["path"];if(relative.Contains("..") || relative.Contains(":") || Path.IsPathRooted(relative) || !allowed.Add(relative))throw new InvalidDataException("Неверный путь каталога ядра.");
                string path=Path.Combine(root,relative);Trust.NoLinks(path);
                if(!File.Exists(path) || new FileInfo(path).Length!=Convert.ToInt64(item["size"]) || Trust.Hash(path)!=(string)item["sha256"])throw new InvalidDataException("Файлы ядра отличаются от официального архива Flowseal: "+relative);
            }
            foreach(string path in Trust.Files(root)) {
                string relative=path.Substring(Path.GetFullPath(root).TrimEnd('\\').Length+1).Replace('\\','/');
                if(!allowed.Contains(relative) && !Trust.Mutable(relative) && relative!=ReceiptName && relative!=ArchiveName && relative!=".upstream-ipset.sha256" && relative!="lists/ipset-all.txt.backup")throw new InvalidDataException("В ядре обнаружен посторонний файл: "+relative);
            }
            return catalogue;
        }
        internal static string AuthenticatedApproval(byte[] bytes) {
            var receipt=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(bytes));string hash=(string)receipt["archive_sha256"];
            Dictionary<string,object> known;lock(gate)approved.TryGetValue(hash,out known);
            if(known==null)known=Saved(hash);
            if(known==null || Serialize(known)!=Encoding.UTF8.GetString(bytes))throw new InvalidDataException("Происхождение каталога ядра не подтверждено.");
            return ApprovalPath(hash);
        }
    }
}
