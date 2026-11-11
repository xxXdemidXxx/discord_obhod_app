using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using Microsoft.Win32;

namespace ZapretDesktop {
    internal sealed class NetworkFootprint {
        internal readonly List<string> Items=new List<string>();
        internal bool Verified=true;
        internal int OwnComponents,ExternalComponents;
        internal bool HasExternalZapret;
        internal bool Clean {get{return Verified && Items.Count==0;}}
        internal string Summary {get{return Clean?"Процессы обхода, службы и WinDivert не обнаружены":!Verified?"Проверка процессов и служб неполная":OwnComponents+ExternalComponents>0?"Компоненты ByPass: "+OwnComponents+" · другие: "+ExternalComponents:"Остались сетевые компоненты: "+Items.Count;}}
        internal string Display {get{return Summary+(Items.Count==0?"":"\n"+String.Join("\n",Items.Take(3).Select(x=>x.Length>160?x.Substring(0,157)+"…":x)))+(Items.Count>3?"\nОстальные компоненты — в журнале":"");}}
        internal static string Executable(string command) {
            command=Environment.ExpandEnvironmentVariables(command??"").Trim();
            if(command.StartsWith("\"")){int end=command.IndexOf('"',1);return end>1?command.Substring(1,end-1):"";}
            int exe=command.IndexOf(".exe",StringComparison.OrdinalIgnoreCase);
            return exe>=0?command.Substring(0,exe+4):EngineHost.NormalizeDriverPath(command);
        }
        internal static bool OwnImage(string image) {
            if(String.IsNullOrEmpty(image))return false;
            try{image=System.IO.Path.GetFullPath(Executable(image));return image.StartsWith(System.IO.Path.GetFullPath(HostWire.ProtectedRoot).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);}
            catch(ArgumentException){return false;}catch(NotSupportedException){return false;}
        }
#if TESTING
        internal static Func<NetworkFootprint> TestRead;
#endif
        internal static NetworkFootprint Read() {
#if TESTING
            if(TestRead!=null)return TestRead();
#endif
            var result=new NetworkFootprint();
            try {
                foreach(string name in new[]{"winws","winws2","goodbyedpi","ciadpi","ByPass.EngineHost"})foreach(var process in Process.GetProcessesByName(name))using(process){
                    string image="";try{image=HostWire.Image(process.Id);}catch(InvalidOperationException){continue;}catch(System.ComponentModel.Win32Exception){}
                    bool own=OwnImage(image);if(own)result.OwnComponents++;else result.ExternalComponents++;
                    if(!own && name=="winws")result.HasExternalZapret=true;
                    result.Items.Add((own?"ByPass: ":"Другой обход: ")+"процесс "+process.ProcessName+" · PID "+process.Id);
                }
                using(var services=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services",false)) {
                    if(services==null)throw new InvalidOperationException("Службы Windows недоступны.");
                    foreach(string name in services.GetSubKeyNames())using(var service=services.OpenSubKey(name,false)) {
                        if(service==null)continue;string image=Convert.ToString(service.GetValue("ImagePath"));
                        bool related=name.StartsWith("WinDivert",StringComparison.OrdinalIgnoreCase) || name==StartupService.Name ||
                            System.Text.RegularExpressions.Regex.IsMatch(image,@"(?i)(?:^|[\\""\s])(winws2?|goodbyedpi|ciadpi)\.exe(?:""|\s|$)");
                        if(!related)continue;
                        string state;try{using(var controller=new ServiceController(name))state=controller.Status.ToString();}catch(Exception ex){state="не удалось прочитать: "+ex.Message;result.Verified=false;}
                        bool own=name==StartupService.Name || OwnImage(image);if(own)result.OwnComponents++;else result.ExternalComponents++;
                        if(!own && (ExternalBypass.IsZapretCommand(image) || name.Equals("WinDivert",StringComparison.OrdinalIgnoreCase)))result.HasExternalZapret=true;
                        result.Items.Add((own?"ByPass: ":"Другой обход: ")+"служба "+name+" · "+state+" · "+Executable(image));
                    }
                }
            }catch(Exception ex){result.Verified=false;result.Items.Add("Ошибка проверки: "+ex.Message);}
            return result;
        }
    }
}
