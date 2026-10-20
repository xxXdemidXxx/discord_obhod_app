using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace ZapretDesktop {
    // The private release key is never included in the application or CI.
    public static class Trust {
        public static string Resource(string name) {
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("ZapretDesktop."+name)) {
                if(stream==null)throw new InvalidDataException("Отсутствуют данные доверия.");
                using(var reader=new StreamReader(stream))return reader.ReadToEnd();
            }
        }
        public static Dictionary<string,object> Verify(string envelope) {
            try {
                if(envelope==null || envelope.Length>1024*1024)throw new InvalidDataException();
                var serializer=new JavaScriptSerializer {MaxJsonLength=1024*1024};
                var outer=serializer.Deserialize<Dictionary<string,object>>(envelope);
                var payload=Convert.FromBase64String((string)outer["signed_payload"]);
                var signature=Convert.FromBase64String((string)outer["signature"]);
                using(var rsa=new RSACryptoServiceProvider()) {
                    rsa.PersistKeyInCsp=false;rsa.FromXmlString(Resource("UpdatePublicKey"));
                    if(!rsa.VerifyData(payload,CryptoConfig.MapNameToOID("SHA256"),signature))throw new InvalidDataException();
                }
                return serializer.Deserialize<Dictionary<string,object>>(System.Text.Encoding.UTF8.GetString(payload));
            }catch(Exception ex){throw new InvalidDataException("Подпись данных не подтверждена. Обновление отменено.",ex);}
        }
        public static string Hash(string path) {
            using(var input=File.OpenRead(path))using(var sha=SHA256.Create())return Hex(sha.ComputeHash(input));
        }
        public static string Hex(byte[] bytes){return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}
        public static void NoLinks(string path) {
            for(var current=Path.GetFullPath(path);!String.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))
                if((File.Exists(current)||Directory.Exists(current)) && (File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidDataException("Ссылки и перенаправления в папке приложения не поддерживаются.");
        }
        public static IEnumerable<string> Files(string root) {
            NoLinks(root);
            foreach(var path in Directory.GetFileSystemEntries(root)) {
                NoLinks(path);
                if(Directory.Exists(path)){foreach(var nested in Files(path))yield return nested;}
                else yield return path;
            }
        }
        public static Dictionary<string,object> Engine(string version) {
            var catalogue=Verify(Resource("EngineTrust"));
            if(Convert.ToInt32(catalogue["schema"])!=1)throw new InvalidDataException("Неизвестный формат каталога ядра.");
            foreach(Dictionary<string,object> entry in (ArrayList)catalogue["engines"])
                if((string)entry["version"]==version)return entry;
            throw new InvalidDataException("Эта версия ядра ещё не проверена. Обновите оболочку ByPass, чтобы получить новый каталог доверия.");
        }
        public static void Archive(string version,string digest) {
            if(!String.Equals("sha256:"+(string)Engine(version)["archive_sha256"],digest,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Архив ядра не соответствует проверенному выпуску.");
        }
        public static bool Mutable(string path) {
            return (path.StartsWith("lists/",StringComparison.OrdinalIgnoreCase) && path.EndsWith(".txt",StringComparison.OrdinalIgnoreCase) && path.IndexOf('/',6)<0)
                || path=="utils/game_filter.enabled" || path=="bin/ACTIVE_GAME_UDP.bin" || path=="bin/ACTIVE_DISCORD_UDP.bin";
        }
        public static Dictionary<string,object> VerifyEngine(string root) {
            NoLinks(root);
            // Try only reviewed catalogues; never trust a hash supplied by a local file.
            var catalogue=Verify(Resource("EngineTrust"));
            foreach(Dictionary<string,object> entry in (ArrayList)catalogue["engines"]) {
                bool matches=true;
                var allowed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(Dictionary<string,object> item in (ArrayList)entry["files"]) {
                    string relative=(string)item["path"];
                    if(relative.Contains("..") || relative.Contains(":") || Path.IsPathRooted(relative))throw new InvalidDataException("Неправильный путь каталога доверия.");
                    allowed.Add(relative);string path=Path.Combine(root,relative);NoLinks(path);
                    if(!File.Exists(path) || new FileInfo(path).Length!=Convert.ToInt64(item["size"]) || Hash(path)!=(string)item["sha256"]){matches=false;break;}
                }
                if(!matches)continue;
                foreach(var path in Files(root)) {
                    string relative=path.Substring(Path.GetFullPath(root).TrimEnd('\\').Length+1).Replace('\\','/');
                    if(!allowed.Contains(relative) && !Mutable(relative) && !relative.StartsWith(".upstream-",StringComparison.Ordinal) && relative!="lists/ipset-all.txt.backup")
                        throw new InvalidDataException("В ядре обнаружен посторонний файл: "+relative);
                }
                return entry;
            }
            return UpstreamTrust.VerifyEngine(root);
        }
        public static void VerifyShell(string proof,string exe,string config) {
            var payload=Verify(proof);
            if(Convert.ToInt32(payload["schema"])!=1)throw new InvalidDataException("Неизвестный формат обновления.");
            var files=(Dictionary<string,object>)payload["shell_files"];
            NoLinks(exe);NoLinks(config);
            if(Hash(exe)!=(string)files["exe"] || Hash(config)!=(string)files["config"])
                throw new InvalidDataException("Файлы оболочки отличаются от подписанного выпуска.");
        }
    }
}
