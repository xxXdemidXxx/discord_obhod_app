using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

namespace ByPassOwner {
    public sealed class OwnerKeyStore {
        public const string Fingerprint = "813c22d95ae9b8e82d7776a5c8c87acb47c05bf3d0cfa6137ceeeb915b4e125f";
        readonly string publicXml;
        public readonly string KeyPath;
        public OwnerKeyStore(string publicXml, string keyPath) {
            this.publicXml=publicXml.Trim(); KeyPath=Path.GetFullPath(keyPath);
        }
        static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength=65536 }; }
        public static string PublicFingerprint(string xml) {
            using(var rsa=new RSACryptoServiceProvider()) {
                rsa.PersistKeyInCsp=false; rsa.FromXmlString(xml);
                using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(rsa.ToXmlString(false)))).Replace("-","").ToLowerInvariant();
            }
        }
        RSACryptoServiceProvider PrivateKey(byte[] plain) {
            var rsa=new RSACryptoServiceProvider(); rsa.PersistKeyInCsp=false;
            try {
                rsa.FromXmlString(Encoding.UTF8.GetString(plain));
                if(rsa.PublicOnly || rsa.KeySize<3072 || rsa.ToXmlString(false)!=publicXml) throw new InvalidOperationException("Это ключ другого проекта. Нужен существующий ключ ByPass.");
                // Exercise the private key rather than checking only its public fields.
                var challenge=new byte[32]; using(var random=RandomNumberGenerator.Create())random.GetBytes(challenge);
                var signature=rsa.SignData(challenge,CryptoConfig.MapNameToOID("SHA256"));
                using(var verify=new RSACryptoServiceProvider()) {
                    verify.PersistKeyInCsp=false; verify.FromXmlString(publicXml);
                    if(!verify.VerifyData(challenge,CryptoConfig.MapNameToOID("SHA256"),signature))throw new InvalidOperationException("Проверка подписи не пройдена.");
                }
                return rsa;
            } catch {rsa.Dispose();throw;}
        }
        byte[] LocalPlain() {
            if(!File.Exists(KeyPath))throw new InvalidOperationException("На этом компьютере ключ не найден. Импортируйте резервную копию с первого компьютера. Новый ключ создавать не нужно.");
            if(new FileInfo(KeyPath).Length>65536)throw new InvalidOperationException("Локальный файл ключа повреждён.");
            try{return ProtectedData.Unprotect(File.ReadAllBytes(KeyPath),null,DataProtectionScope.CurrentUser);}
            catch(CryptographicException){throw new InvalidOperationException("Windows не смогла открыть ключ. Запустите помощник под той учётной записью, которая создавала или импортировала его.");}
        }
        public void Check() {
            var plain=LocalPlain();try{using(var rsa=PrivateKey(plain)) {}}finally{Array.Clear(plain,0,plain.Length);}
        }
        static byte[] Material(string password, byte[] salt) {
            if(password==null || password.Length<16)throw new InvalidOperationException("Пароль должен содержать не менее 16 символов.");
            var bytes=Encoding.UTF8.GetBytes(password);
            try {using(var kdf=new Rfc2898DeriveBytes(bytes,salt,310000,HashAlgorithmName.SHA256))return kdf.GetBytes(64);}
            finally{Array.Clear(bytes,0,bytes.Length);}
        }
        static byte[] Part(byte[] material, int start) {var part=new byte[32];Buffer.BlockCopy(material,start,part,0,32);return part;}
        static byte[] Mac(Dictionary<string,object> data, byte[] material) {
            var key=Part(material,32);
            try{using(var mac=new HMACSHA256(key))return mac.ComputeHash(Encoding.UTF8.GetBytes("ByPass-key-v1\n310000\n"+data["salt"]+"\n"+data["iv"]+"\n"+data["ciphertext"]));}
            finally{Array.Clear(key,0,key.Length);}
        }
        public static string Protect(byte[] plain, string password) {
            var salt=new byte[16];using(var random=RandomNumberGenerator.Create())random.GetBytes(salt);
            var material=Material(password,salt);var key=Part(material,0);
            try {
                using(var aes=Aes.Create()) {
                    aes.Key=key;aes.GenerateIV();
                    byte[] ciphertext;using(var encryptor=aes.CreateEncryptor())ciphertext=encryptor.TransformFinalBlock(plain,0,plain.Length);
                    var data=new Dictionary<string,object>{{"schema",1},{"kdf","PBKDF2-SHA256"},{"iterations",310000},{"salt",Convert.ToBase64String(salt)},{"iv",Convert.ToBase64String(aes.IV)},{"ciphertext",Convert.ToBase64String(ciphertext)}};
                    data["mac"]=Convert.ToBase64String(Mac(data,material));return Json().Serialize(data);
                }
            } finally {Array.Clear(key,0,key.Length);Array.Clear(material,0,material.Length);}
        }
        public static byte[] Unprotect(string json, string password) {
            if(json==null || json.Length>65536)throw new InvalidOperationException("Файл резервной копии слишком большой или пустой.");
            Dictionary<string,object> data;byte[] salt,iv,cipher,expected;
            try {
                data=Json().Deserialize<Dictionary<string,object>>(json);
                if(Convert.ToInt32(data["schema"])!=1 || (string)data["kdf"]!="PBKDF2-SHA256" || Convert.ToInt32(data["iterations"])!=310000)throw new FormatException();
                salt=Convert.FromBase64String((string)data["salt"]);iv=Convert.FromBase64String((string)data["iv"]);cipher=Convert.FromBase64String((string)data["ciphertext"]);expected=Convert.FromBase64String((string)data["mac"]);
                if(salt.Length!=16 || iv.Length!=16 || expected.Length!=32 || cipher.Length<16 || cipher.Length%16!=0)throw new FormatException();
            }catch{throw new InvalidOperationException("Это не поддерживаемая резервная копия ключа ByPass или файл повреждён.");}
            var material=Material(password,salt);var key=Part(material,0);
            try {
                var actual=Mac(data,material);int difference=0;
                for(int i=0;i<32;i++)difference|=actual[i]^expected[i];
                if(difference!=0)throw new InvalidOperationException("Пароль не подходит или файл резервной копии изменён.");
                using(var aes=Aes.Create()) {aes.Key=key;aes.IV=iv;using(var decryptor=aes.CreateDecryptor())return decryptor.TransformFinalBlock(cipher,0,cipher.Length);}
            }finally{Array.Clear(key,0,key.Length);Array.Clear(material,0,material.Length);}
        }
        byte[] ReadBackup(string path, string password) {
            if(new FileInfo(path).Length>65536)throw new InvalidOperationException("Файл резервной копии слишком большой.");
            return Unprotect(File.ReadAllText(path),password);
        }
        public void VerifyBackup(string path, string password) {
            var plain=ReadBackup(path,password);try{using(var rsa=PrivateKey(plain)) {}}finally{Array.Clear(plain,0,plain.Length);}
        }
        public void Export(string path, string password, string repository) {
            path=Path.GetFullPath(path);
            if(path.StartsWith(Path.GetFullPath(repository).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Сохраните резервную копию вне папки репозитория: например, в Документы или на флешку.");
            if(File.Exists(path))throw new InvalidOperationException("Такой файл уже существует. Выберите другое имя: резервные копии не перезаписываются.");
            var plain=LocalPlain();
            try {
                using(var rsa=PrivateKey(plain)) {}
                string json=Protect(plain,password);
                // Confirm the exported representation before committing a portable copy.
                var decoded=Unprotect(json,password);try{using(var rsa=PrivateKey(decoded)) {}}finally{Array.Clear(decoded,0,decoded.Length);}
                var bytes=new UTF8Encoding(false).GetBytes(json);
                using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))output.Write(bytes,0,bytes.Length);
            }finally{Array.Clear(plain,0,plain.Length);}
        }
        public void Import(string path, string password) {
            if(File.Exists(KeyPath))throw new InvalidOperationException("На этом компьютере уже есть ключ. Его замена запрещена. Нажмите «Проверить ключ».");
            var plain=ReadBackup(path,password);
            try {
                using(var rsa=PrivateKey(plain)) {}
                var protectedBytes=ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser);
                var directory=Path.GetDirectoryName(KeyPath);Directory.CreateDirectory(directory);
                var sid=WindowsIdentity.GetCurrent().User;
                // Keep the directory's existing owner: changing ownership can
                // require an administrator token, while restricting its DACL
                // does not require elevating the owner's signing workflow.
                var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
                foreach(var identity in new[]{sid,new SecurityIdentifier("S-1-5-18")})acl.AddAccessRule(new FileSystemAccessRule(identity,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
                Directory.SetAccessControl(directory,acl);
                using(var output=new FileStream(KeyPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))output.Write(protectedBytes,0,protectedBytes.Length);
            }finally{Array.Clear(plain,0,plain.Length);}
            Check();
        }
    }
}
