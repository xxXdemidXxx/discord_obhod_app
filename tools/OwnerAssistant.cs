using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ByPassOwner {
    static class OwnerProgram {
        [STAThread] static void Main(string[] args) {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            try {
                string root=args.Length==1?Path.GetFullPath(args[0]):Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,".."));
                string publicXml=File.ReadAllText(Path.Combine(root,"security","update-public.xml"));
                if(OwnerKeyStore.PublicFingerprint(publicXml)!=OwnerKeyStore.Fingerprint)throw new InvalidOperationException("Открытый ключ проекта отличается от официального. Откройте помощник из своего официального репозитория.");
                Application.Run(new OwnerForm(root,new OwnerKeyStore(publicXml,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ByPass","Signing","release.key"))));
            }catch(Exception ex){MessageBox.Show(ex.Message,"Помощник владельца ByPass",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        }
    }
    public sealed class PasswordDialog : Form {
        readonly TextBox password=new TextBox {UseSystemPasswordChar=true,Dock=DockStyle.Fill};
        readonly TextBox repeat=new TextBox {UseSystemPasswordChar=true,Dock=DockStyle.Fill};
        public string Password {get{return password.Text;}}
        public PasswordDialog(bool exporting) {
            Text=exporting?"Пароль резервной копии":"Открыть резервную копию";StartPosition=FormStartPosition.CenterParent;
            FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;MinimizeBox=false;ClientSize=new Size(470,exporting?260:210);Font=new Font("Segoe UI",10);
            var panel=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(16),ColumnCount=1};Controls.Add(panel);
            panel.Controls.Add(new Label {AutoSize=true,MaximumSize=new Size(430,0),Text=exporting?"Придумайте пароль от 16 символов. Сохраните его в менеджере паролей отдельно от файла.":"Введите пароль, который задали при создании этой резервной копии."});
            panel.Controls.Add(password);
            if(exporting){panel.Controls.Add(new Label {Text="Повторите пароль",AutoSize=true});panel.Controls.Add(repeat);}
            var actions=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};
            var ok=new Button {Text="Продолжить",AutoSize=true};var cancel=new Button {Text="Отмена",AutoSize=true,DialogResult=DialogResult.Cancel};actions.Controls.Add(ok);actions.Controls.Add(cancel);panel.Controls.Add(actions);CancelButton=cancel;AcceptButton=ok;
            ok.Click+=delegate {
                if(password.Text.Length<16){MessageBox.Show(this,"Нужно не менее 16 символов.",Text);return;}
                if(exporting && password.Text!=repeat.Text){MessageBox.Show(this,"Пароли не совпали. Повторите ввод.",Text);return;}
                DialogResult=DialogResult.OK;
            };
        }
        protected override void Dispose(bool disposing) {if(disposing){password.Clear();repeat.Clear();}base.Dispose(disposing);}
    }
    public sealed class OwnerForm : Form {
        readonly string root;readonly OwnerKeyStore store;
        readonly Label keyStatus=new Label {AutoSize=true,MaximumSize=new Size(820,0)};
        readonly Label operation=new Label {Text="Готово к работе",AutoSize=true};
        readonly TextBox log=new TextBox {Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill};
        readonly TextBox thumbprint=new TextBox {Width=380};
        readonly TabControl tabs=new TabControl {Dock=DockStyle.Fill};
        readonly ProgressBar progress=new ProgressBar {Dock=DockStyle.Bottom,Height=12,Visible=false,Style=ProgressBarStyle.Marquee};
        bool busy;
        public OwnerForm(string root, OwnerKeyStore store) {
            this.root=root;this.store=store;Text="ByPass — помощник владельца";Font=new Font("Segoe UI",10);
            ClientSize=new Size(920,690);MinimumSize=new Size(800,640);StartPosition=FormStartPosition.CenterScreen;
            using(var icon=new Icon(Path.Combine(root,"assets","ByPass.ico"),32,32))Icon=(Icon)icon.Clone();
            var keyPage=new TabPage("Ключ и перенос");var releasePage=new TabPage("Выпуск обновления");tabs.TabPages.Add(keyPage);tabs.TabPages.Add(releasePage);
            Controls.Add(tabs);Controls.Add(progress);
            var keyPanel=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(18)};keyPage.Controls.Add(keyPanel);
            AddText(keyPanel,"Один ключ на двух компьютерах",true);
            AddText(keyPanel,"Учётная запись Windows: "+WindowsIdentity.GetCurrent().Name+"\nКлюч: "+store.KeyPath);
            keyPanel.Controls.Add(keyStatus);
            AddButton(keyPanel,"1. Проверить ключ на этом компьютере",async delegate {await Work("Проверка ключа",delegate{store.Check();},"Ключ подходит для официальной подписи ByPass. Тестовая подпись успешно проверена.");RefreshKey();});
            AddText(keyPanel,"Первый компьютер: создайте копию. Пароль задаёте сами, помощник его не сохраняет.");
            AddButton(keyPanel,"2. Создать резервную копию ключа…",async delegate {
                using(var save=new SaveFileDialog {Title="Сохранить зашифрованную копию вне репозитория",Filter="Копия ключа ByPass|*.key-backup.json",FileName="ByPass.key-backup.json",InitialDirectory=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),OverwritePrompt=true}) {
                    if(save.ShowDialog(this)!=DialogResult.OK)return;
                    using(var password=new PasswordDialog(true)) {if(password.ShowDialog(this)!=DialogResult.OK)return;string secret=password.Password;
                        try{await Work("Создание резервной копии",delegate{store.Export(save.FileName,secret,root);},"Копия создана и проверена. Сохраните пароль отдельно. Теперь можно проверить файл кнопкой ниже.");}finally{secret=null;}
                    }
                }
            });
            AddButton(keyPanel,"3. Проверить резервную копию (без установки)…",async delegate{await OpenBackup(false);});
            AddText(keyPanel,"Сегодня эта проверка подтвердит файл, пароль и возможность подписывать вашим ключом. Текущий ключ не меняется.\nВечером на втором компьютере перенесите файл, откройте этот же помощник и импортируйте копию.");
            AddButton(keyPanel,"4. Импортировать ключ на этом компьютере…",async delegate{await OpenBackup(true);RefreshKey();});
            AddText(keyPanel,"Другой ключ не создаётся. Уже существующий ключ не заменяется. Разные учётки Windows подходят.\nФайл резервной копии зашифрован. Не добавляйте его в GitHub и не передавайте вместе с паролем.");
            AddButton(keyPanel,"Открыть пошаговую инструкцию",delegate{OpenGuide();});
            var releasePanel=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(16),ColumnCount=1,RowCount=6};releasePage.Controls.Add(releasePanel);
            releasePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));releasePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));releasePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));releasePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));releasePanel.RowStyles.Add(new RowStyle(SizeType.Percent,100));releasePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            releasePanel.Controls.Add(new Label {AutoSize=true,MaximumSize=new Size(850,0),Text="Сначала измените версию и описание, проверьте код, сделайте commit/push и дождитесь зелёного GitHub Actions. Затем используйте кнопки по порядку. Готовые архивы появятся в папке releases.\nPowerShell 7 и Git нужны только для выпуска; для ключа и переноса вводить команды не требуется."});
            var certPanel=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill};certPanel.Controls.Add(new Label {AutoSize=true,Text="Сертификат Windows — отпечаток (необязательно):"});certPanel.Controls.Add(thumbprint);releasePanel.Controls.Add(certPanel);
            var actionsPanel=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill};
            AddButton(actionsPanel,"Проверить готовность",async delegate{await RunScript("tools\\owner-check.ps1",false,false);});
            AddButton(actionsPanel,"1. Собрать и подписать",async delegate{await RunScript("package-friends.ps1",true,false);});
            AddButton(actionsPanel,"2. Проверить Defender",async delegate{await RunScript("tools\\owner-scan.ps1",false,true);});
            AddButton(actionsPanel,"3. Подписать обновление",async delegate{await RunScript("tools\\sign-release.ps1",false,false);});
            AddButton(actionsPanel,"4. Опубликовать",async delegate{
                if(MessageBox.Show(this,"Опубликовать подготовленный релиз на GitHub? Он станет доступен всем и будет неизменяемым.","Публикация ByPass",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)==DialogResult.Yes)await RunScript("tools\\publish-release.ps1",false,false);
            });
            releasePanel.Controls.Add(actionsPanel);
            releasePanel.Controls.Add(operation);releasePanel.Controls.Add(log);
            var links=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill};
            AddButton(links,"Папка с готовыми архивами",delegate{Directory.CreateDirectory(Path.Combine(root,"releases"));Process.Start(new ProcessStartInfo(Path.Combine(root,"releases")){UseShellExecute=true});});
            AddButton(links,"GitHub Actions",delegate{Process.Start(new ProcessStartInfo("https://github.com/xxXdemidXxx/discord_obhod_app/actions"){UseShellExecute=true});});
            AddButton(links,"Инструкция",delegate{OpenGuide();});releasePanel.Controls.Add(links);
            FormClosing+=delegate(object sender,FormClosingEventArgs e){if(busy){e.Cancel=true;MessageBox.Show(this,"Дождитесь завершения текущего шага.",Text);}};
            Shown+=delegate{RefreshKey();};
        }
        void OpenGuide() {Process.Start(new ProcessStartInfo("https://github.com/xxXdemidXxx/discord_obhod_app/blob/main/OWNER-GUIDE.md"){UseShellExecute=true});}
        static void AddText(Control parent,string text,bool heading=false) {parent.Controls.Add(new Label {Text=text,AutoSize=true,MaximumSize=new Size(820,0),Margin=new Padding(0,8,0,8),Font=new Font("Segoe UI",heading?14:10,heading?FontStyle.Bold:FontStyle.Regular)});}
        static void AddButton(Control parent,string text,EventHandler action) {var button=new Button {Text=text,AutoSize=true,MinimumSize=new Size(160,36),Margin=new Padding(0,6,12,6)};button.Click+=action;parent.Controls.Add(button);}
        void RefreshKey() {
            try{store.Check();keyStatus.Text="✓ Ключ найден, подпись проверена. Этот компьютер готов подписывать ByPass.";keyStatus.ForeColor=Color.DarkGreen;}
            catch(Exception ex){keyStatus.Text=ex.Message;keyStatus.ForeColor=Color.DarkRed;}
        }
        async Task OpenBackup(bool import) {
            if(import && File.Exists(store.KeyPath)){MessageBox.Show(this,"Ключ уже установлен. На первом компьютере используйте «Проверить резервную копию». Импорт понадобится на втором компьютере.",Text);return;}
            using(var open=new OpenFileDialog {Title="Выберите зашифрованную копию ключа",Filter="Копия ключа ByPass|*.key-backup.json|JSON|*.json",CheckFileExists=true}) {
                if(open.ShowDialog(this)!=DialogResult.OK)return;
                using(var password=new PasswordDialog(false)) {if(password.ShowDialog(this)!=DialogResult.OK)return;string secret=password.Password;
                    try{await Work(import?"Импорт ключа":"Проверка копии",delegate{if(import)store.Import(open.FileName,secret);else store.VerifyBackup(open.FileName,secret);},import?"Ключ импортирован для этой учётки Windows. Подпись проверена, компьютер готов к выпуску.":"Копия исправна, пароль подходит, ключ совпадает с официальным ByPass. Подпись успешно проверена. Вечером этот файл можно импортировать на втором компьютере.");}finally{secret=null;}
                }
            }
        }
        async Task Work(string name,Action action,string success) {
            busy=true;tabs.Enabled=false;progress.Visible=true;operation.Text=name+"…";
            try{await Task.Run(action);operation.Text="Готово";MessageBox.Show(this,success,name,MessageBoxButtons.OK,MessageBoxIcon.Information);}
            catch(Exception ex){operation.Text="Шаг не завершён";MessageBox.Show(this,ex is InvalidOperationException?ex.Message:"Шаг не удалось выполнить. Проверьте доступ к файлам. Ничего не отправляйте с паролем или содержимым ключа.",name,MessageBoxButtons.OK,MessageBoxIcon.Error);}
            finally{busy=false;tabs.Enabled=true;progress.Visible=false;}
        }
        string FindPwsh() {
            string candidate=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"PowerShell","7","pwsh.exe");
            if(File.Exists(candidate))return candidate;
            foreach(string part in (Environment.GetEnvironmentVariable("PATH")??"").Split(';')) {try{candidate=Path.Combine(part.Trim('"'),"pwsh.exe");if(File.Exists(candidate))return candidate;}catch{}}
            candidate=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".cache","codex-runtimes","codex-primary-runtime","dependencies","native","powershell","pwsh.exe");
            if(File.Exists(candidate))return candidate;
            throw new InvalidOperationException("Для выпуска нужен PowerShell 7 (pwsh.exe). Ключ и резервная копия работают без него. Установите PowerShell 7, затем снова откройте помощник.");
        }
        void Append(string text) {if(IsDisposed)return;if(InvokeRequired){BeginInvoke(new Action<string>(Append),text);return;}log.AppendText(text+Environment.NewLine);}
        async Task RunScript(string script,bool ownerSignature,bool elevate) {
            string pwsh;try{pwsh=FindPwsh();}catch(Exception ex){MessageBox.Show(this,ex.Message,Text);return;}
            if(thumbprint.Text.Trim().Length>0 && !System.Text.RegularExpressions.Regex.IsMatch(thumbprint.Text.Trim(),"^[a-fA-F0-9]{40}$")){MessageBox.Show(this,"Отпечаток сертификата должен содержать 40 символов без пробелов. Если сертификата нет, оставьте поле пустым.",Text);return;}
            busy=true;tabs.Enabled=false;progress.Visible=true;operation.Text="Выполняется: "+script;log.Clear();
            try {
                var start=new ProcessStartInfo(pwsh,"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File \""+Path.Combine(root,script)+"\""+(ownerSignature?" -OwnerSignature":"")) {WorkingDirectory=root,UseShellExecute=elevate,CreateNoWindow=!elevate,WindowStyle=ProcessWindowStyle.Hidden};
                if(elevate){start.Verb="runas";Append("Windows запросит разрешение только для проверки Defender. Ключ подписи в этом шаге не используется.");}
                else {start.RedirectStandardOutput=true;start.RedirectStandardError=true;start.StandardOutputEncoding=Encoding.UTF8;start.StandardErrorEncoding=Encoding.UTF8;start.EnvironmentVariables["BYPASS_SIGNING_THUMBPRINT"]=thumbprint.Text.Trim();}
                int exitCode=await Task.Run(delegate {
                    using(var process=new Process {StartInfo=start}) {
                        if(!elevate){process.OutputDataReceived+=delegate(object s,DataReceivedEventArgs e){if(e.Data!=null)Append(e.Data);};process.ErrorDataReceived+=delegate(object s,DataReceivedEventArgs e){if(e.Data!=null)Append(e.Data);};}
                        process.Start();if(!elevate){process.BeginOutputReadLine();process.BeginErrorReadLine();}process.WaitForExit();return process.ExitCode;
                    }
                });
                if(elevate){string scanLog=Path.Combine(root,"releases","owner-scan.log");if(File.Exists(scanLog))Append(File.ReadAllText(scanLog));}
                operation.Text=exitCode==0?"Шаг завершён успешно":"Ошибка: следующий шаг не выполняйте";
                if(exitCode==0)Append("Готово. Следующий шаг выбираете сами.");
            }catch(System.ComponentModel.Win32Exception ex){operation.Text="Шаг не запущен";Append(ex.NativeErrorCode==1223?"Запрос Windows отклонён. Проверка не выполнена.":"Не удалось запустить шаг: "+ex.Message);}
            catch(Exception ex){operation.Text="Шаг не завершён";Append(ex.Message);}
            finally{busy=false;tabs.Enabled=true;progress.Visible=false;}
        }
    }
}
