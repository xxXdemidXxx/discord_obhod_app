using System;
using System.IO;
using System.Text;

namespace ZapretDesktop {
    public static class DiagnosticLog {
        public static void Save(Store store,bool running,string strategy,string text,string file) {
            var report=new StringBuilder();report.AppendLine("ByPass — журнал для поддержки");
            report.AppendLine("Создан: "+DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            report.AppendLine("Оболочка: "+ShellUpdates.Current);report.AppendLine("Ядро: "+Updater.Version(store.Current));
            report.AppendLine("Windows: "+Environment.OSVersion+" · "+(Environment.Is64BitOperatingSystem?"64":"32")+" бит");
            report.AppendLine("Обход: "+(running?"включён":"выключен"));report.AppendLine("Стратегия: "+strategy);
            report.AppendLine("Автозапуск Windows (сохранённая настройка): "+store.Settings.StartWithWindows);report.AppendLine("Автообновление оболочки: "+store.Settings.AutoInstallShell+"; ядра: "+store.Settings.AutoInstallCore);
            report.AppendLine();report.AppendLine("Сообщения приложения:");
            string saved=Path.Combine(store.Root,"data","app.log");
            if(File.Exists(saved)) {
                using(var input=new FileStream(saved,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)) {
                    if(input.Length>2*1024*1024)input.Seek(-2*1024*1024,SeekOrigin.End);
                    using(var reader=new StreamReader(input,Encoding.UTF8))report.Append(reader.ReadToEnd());
                }
            }else report.Append(text);
            // Do not collect environment variables, account names, user lists,
            // credentials or signing material. Mask the user's profile path.
            string output=report.ToString(),profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if(!String.IsNullOrEmpty(profile))output=System.Text.RegularExpressions.Regex.Replace(output,System.Text.RegularExpressions.Regex.Escape(profile),"%USERPROFILE%",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            File.WriteAllText(file,output,new UTF8Encoding(true));
        }
    }
}
