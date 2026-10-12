using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ZapretDesktop {
    public static class OwnerIdentity {
        public const string Owner="xxXdemidXxx";
        public const string ProofName="build-proof.json";
        public static string Fingerprint {
            get {using(var rsa=new RSACryptoServiceProvider())using(var sha=SHA256.Create()){rsa.PersistKeyInCsp=false;rsa.FromXmlString(Trust.Resource("UpdatePublicKey"));return Trust.Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(rsa.ToXmlString(false))));}}
        }
        public static void Verify(string proof,string exe,string config,string version) {
            var data=Trust.Verify(proof);
            if(Convert.ToInt32(data["schema"])!=1 || Convert.ToString(data["kind"])!="ByPass-build" || Convert.ToString(data["owner"])!=Owner || Convert.ToString(data["version"])!=version)
                throw new InvalidDataException("Сборка не подтверждена ключом автора ByPass.");
            Trust.VerifyShell(proof,exe,config);
        }
        public static bool Current(string exe) {
            try {string path=Path.Combine(Path.GetDirectoryName(exe),ProofName);Trust.NoLinks(path);if(new FileInfo(path).Length>1024*1024)return false;Verify(File.ReadAllText(path),exe,exe+".config",ShellUpdates.Current);return true;}catch{return false;}
        }
        public static async Task<bool> RestoreProof(string exe) {
            if(Current(exe))return true;
            // Older updaters copy only EXE and config. Recover the independently
            // signed build receipt for this exact version, never the latest one.
            try {
                using(var http=new HttpClient{Timeout=TimeSpan.FromSeconds(10)}) {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("ByPass/"+ShellUpdates.Current);
                    string proof=await GitHubMetadata.Get(http,"https://github.com/"+ShellUpdates.Repository+"/releases/download/v"+ShellUpdates.Current+"/"+ProofName,true);
                    if(proof==null)return false;Verify(proof,exe,exe+".config",ShellUpdates.Current);
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(exe),ProofName),proof,new UTF8Encoding(false));return true;
                }
            }catch{return false;}
        }
    }
}
