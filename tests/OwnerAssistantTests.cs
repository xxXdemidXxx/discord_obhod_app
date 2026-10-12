using System;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using ByPassOwner;

static class OwnerAssistantTests {
    const string Password="Only-A-Test-Password-Not-A-Real-Secret";
    static int checks;
    static void Assert(bool okay,string name){if(!okay)throw new Exception(name);checks++;}
    static void Reject(Action action,string name){try{action();}catch{checks++;return;}throw new Exception("Accepted: "+name);}
    [STAThread] static int Main(string[] args) {
        try {
            string root=Path.GetFullPath(args[0]),fixture=Path.GetFullPath(args[1]);
            if(args.Length==3) {
                var imported=new OwnerKeyStore(File.ReadAllText(Path.Combine(fixture,"public.xml")),Path.Combine(fixture,"ps-import","release.key"));
                imported.Import(Path.Combine(fixture,"powershell-backup.key-backup.json"),Password);imported.Check();
                Console.WriteLine("PASS: PowerShell backup imported and signature verified by graphical assistant");return 0;
            }
            Directory.CreateDirectory(fixture);
            using(var rsa=new RSACryptoServiceProvider(3072)) {
                rsa.PersistKeyInCsp=false;string publicXml=rsa.ToXmlString(false);
                File.WriteAllText(Path.Combine(fixture,"public.xml"),publicXml);
                byte[] plain=Encoding.UTF8.GetBytes(rsa.ToXmlString(true));
                string source=Path.Combine(fixture,"source.key");File.WriteAllBytes(source,ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser));
                Array.Clear(plain,0,plain.Length);
                var store=new OwnerKeyStore(publicXml,source);store.Check();checks++;
                byte[] original=File.ReadAllBytes(source);
                string backup=Path.Combine(fixture,"csharp-backup.key-backup.json");
                // This temporary test repository is distinct from the backup folder.
                string fakeRepository=Path.Combine(fixture,"repo");Directory.CreateDirectory(fakeRepository);
                store.Export(backup,Password,fakeRepository);store.VerifyBackup(backup,Password);checks++;
                Assert(!File.ReadAllText(backup).Contains("RSAKeyValue"),"No plain private XML in backup");
                Reject(delegate{store.VerifyBackup(backup,"Wrong-Password-For-Testing");},"wrong password");
                Reject(delegate{store.Export(backup,Password,fakeRepository);},"overwrite backup");
                Reject(delegate{store.Export(Path.Combine(fakeRepository,"forbidden.json"),Password,fakeRepository);},"backup in repository");
                Reject(delegate{store.Export(Path.Combine(fixture,"short.json"),"short",fakeRepository);},"short password");
                var second=new OwnerKeyStore(publicXml,Path.Combine(fixture,"destination","release.key"));
                second.Import(backup,Password);second.Check();checks++;
                var acl=Directory.GetAccessControl(Path.GetDirectoryName(second.KeyPath));
                Assert(acl.AreAccessRulesProtected,"import disables inherited access");
                foreach(System.Security.AccessControl.FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(System.Security.Principal.SecurityIdentifier)))
                    Assert(rule.IdentityReference.Value==System.Security.Principal.WindowsIdentity.GetCurrent().User.Value || rule.IdentityReference.Value=="S-1-5-18","only current user and SYSTEM have explicit key access");
                Assert(OwnerKeyStore.PublicFingerprint(publicXml)==OwnerKeyStore.PublicFingerprint(rsa.ToXmlString(false)),"same public identity");
                Reject(delegate{store.Import(backup,Password);},"overwrite source key");
                Reject(delegate{second.Import(backup,Password);},"overwrite imported key");
                Assert(Convert.ToBase64String(original)==Convert.ToBase64String(File.ReadAllBytes(source)),"source key unchanged");
                var json=new JavaScriptSerializer();var data=json.Deserialize<System.Collections.Generic.Dictionary<string,object>>(File.ReadAllText(backup));
                var cipher=Convert.FromBase64String((string)data["ciphertext"]);cipher[0]^=1;data["ciphertext"]=Convert.ToBase64String(cipher);
                string modified=Path.Combine(fixture,"modified.json");File.WriteAllText(modified,json.Serialize(data));
                var invalid=new OwnerKeyStore(publicXml,Path.Combine(fixture,"invalid","release.key"));
                Reject(delegate{invalid.Import(modified,Password);},"tampered backup");Assert(!File.Exists(invalid.KeyPath),"failed import writes no key");
                data=json.Deserialize<System.Collections.Generic.Dictionary<string,object>>(File.ReadAllText(backup));data["iterations"]=2147483647;File.WriteAllText(modified,json.Serialize(data));
                Reject(delegate{invalid.Import(modified,Password);},"unbounded KDF");
                using(var other=new RSACryptoServiceProvider(3072)) {
                    other.PersistKeyInCsp=false;var wrongProject=new OwnerKeyStore(other.ToXmlString(false),Path.Combine(fixture,"other","release.key"));
                    Reject(delegate{wrongProject.Import(backup,Password);},"different project's key");Assert(!File.Exists(wrongProject.KeyPath),"different project writes no key");
                }
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                using(var form=new OwnerForm(root,store)) {
                    form.Show();Application.DoEvents();
                    using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(root,"tests","owner-window.png"));}
                    ((TabControl)form.Controls[0]).SelectedIndex=1;Application.DoEvents();
                    using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(root,"tests","owner-release-window.png"));}
                    form.Close();
                }
                using(var dialog=new PasswordDialog(true)) {
                    dialog.Show();Application.DoEvents();
                    using(var bitmap=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(root,"tests","owner-password-window.png"));}
                    dialog.Close();
                }
            }
            Console.WriteLine("PASS: "+checks+" owner assistant checks");return 0;
        }catch(Exception ex){Console.WriteLine(ex);return 1;}
    }
}
