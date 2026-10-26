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
        internal bool Clean {get{return Verified && Items.Count==0;}}
        internal string Summary {get{return Clean?"Процессы обхода, службы и WinDivert не обнаружены":!Verified?"Проверка остановки неполная":"Остались сетевые компоненты: "+Items.Count;}}
#if TESTING
        internal static Func<NetworkFootprint> TestRead;
#endif
        internal static NetworkFootprint Read() {
#if TESTING
            if(TestRead!=null)return TestRead();
#endif
            var result=new NetworkFootprint();
            try {
                foreach(string name in new[]{"winws","winws2","goodbyedpi","ciadpi","ByPass.EngineHost"})foreach(var process in Process.GetProcessesByName(name))using(process)result.Items.Add("Процесс "+process.ProcessName+" · PID "+process.Id);
                using(var services=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services",false)) {
                    if(services==null)throw new InvalidOperationException("Службы Windows недоступны.");
                    foreach(string name in services.GetSubKeyNames())using(var service=services.OpenSubKey(name,false)) {
                        if(service==null)continue;string image=Convert.ToString(service.GetValue("ImagePath"));
                        bool related=name.StartsWith("WinDivert",StringComparison.OrdinalIgnoreCase) || name==StartupService.Name ||
                            System.Text.RegularExpressions.Regex.IsMatch(image,@"(?i)(?:^|[\\""\s])(winws2?|goodbyedpi|ciadpi)\.exe(?:""|\s|$)");
                        if(!related)continue;
                        string state;try{using(var controller=new ServiceController(name))state=controller.Status.ToString();}catch(Exception ex){state="не удалось прочитать: "+ex.Message;result.Verified=false;}
                        result.Items.Add("Служба "+name+" · "+state+" · "+image);
                    }
                }
            }catch(Exception ex){result.Verified=false;result.Items.Add("Ошибка проверки: "+ex.Message);}
            return result;
        }
    }
}
