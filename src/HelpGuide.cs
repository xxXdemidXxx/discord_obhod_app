using System;
using System.Drawing;
using System.Windows.Forms;
namespace ZapretDesktop {
    internal sealed class HelpGuide : ResponsiveControl {
        readonly Button start,browser,windows,networks;readonly Label title,body,note;readonly SurfacePanel card;string topic="start";
        internal string Topic{get{return topic;}}
        internal HelpGuide(Action<string> open){
            BackColor=Theme.Background;start=Theme.Button("Начало работы",true);browser=Theme.Button("DNS в браузере");windows=Theme.Button("DNS в Windows");
            start.Name="HelpStart";browser.Name="HelpBrowser";windows.Name="HelpWindows";start.Click+=(s,e)=>ShowTopic("start");browser.Click+=(s,e)=>ShowTopic("browser");windows.Click+=(s,e)=>ShowTopic("windows");
            card=new SurfacePanel();title=Theme.Label("",18,true);body=Theme.Label("",11);body.AutoEllipsis=false;note=Theme.Label("",9);note.AutoEllipsis=false;note.ForeColor=Theme.Muted;
            networks=Theme.Button("Открыть подключения Windows");networks.Name="HelpNetworks";networks.Click+=(s,e)=>open("ncpa.cpl");card.Controls.AddRange(new Control[]{title,body,note,networks});Controls.AddRange(new Control[]{start,browser,windows,card});ShowTopic("start");
        }
        internal void ShowTopic(string value){
            topic=value=="browser" || value=="windows"?value:"start";
            start.BackColor=topic=="start"?Theme.Accent:Theme.ButtonSurface;browser.BackColor=topic=="browser"?Theme.Accent:Theme.ButtonSurface;windows.BackColor=topic=="windows"?Theme.Accent:Theme.ButtonSurface;
            if(topic=="browser"){
                title.Text="YouTube в обычном браузере";
                body.Text="1. Оставьте ByPass и выбранную стратегию включёнными.\n2. В Chrome или Edge откройте «Настройки».\n3. В поиске настроек введите DNS.\n4. Включите безопасный DNS и выберите Google Public DNS.\n5. Перезапустите браузер и проверьте ролик YouTube.";
                note.Text="Выберите Google явно, вместо «Текущий поставщик». Это меняет DNS только браузера.\nПроверки ByPass используют DNS Windows и могут оставаться красными.\nНе помогло? Используйте отдельное окно YouTube в диагностике DNS.";
            }else if(topic=="windows"){
                title.Text="DNS для Windows и проверок ByPass";
                body.Text="1. Нажмите «Открыть подключения Windows» ниже.\n2. Откройте свойства подключения, через которое работает интернет.\n3. Выберите «IP версии 4 (TCP/IPv4)» → «Свойства».\n4. Сохраните прежние настройки DNS, например сделайте снимок.\n5. Выберите «Использовать следующие адреса DNS-серверов».\n6. Введите 8.8.8.8 и 8.8.4.4. Нажмите ОК.\n7. Повторите диагностику DNS в ByPass.";
                note.Text="Меняйте только DNS; IP-адрес и шлюз оставьте как были. Для возврата восстановите\nпрежние значения DNS. Если не помогло, попробуйте DNS в браузере или отдельное окно.";
            }else{
                title.Text="Подключиться за три шага";
                body.Text="1. На вкладке «Подключение» нажмите «Подобрать автоматически».\n2. Дождитесь результата и откройте YouTube или Discord.\n3. Проверьте ролик или звонок. Работает — нажмите «Работает».";
                note.Text="Если появилась «Ошибка DNS», выберите инструкцию для браузера или Windows выше.\nБыстрый вариант: «Диагностика DNS» → «Открыть YouTube в отдельном окне».\nОставьте ByPass включённым; после его перезапуска откройте это окно заново.";
            }
            networks.Visible=topic=="windows";PerformLayout();Invalidate(true);
        }
        protected override void OnLayout(LayoutEventArgs e){
            base.OnLayout(e);if(card==null)return;int gap=S(10),width=(Width-gap*2)/3;
            start.SetBounds(0,0,width,S(40));browser.SetBounds(width+gap,0,width,S(40));windows.SetBounds((width+gap)*2,0,width,S(40));
            int p=S(22),w=Math.Max(S(200),Width-2*p),bodyHeight=TextRenderer.MeasureText(body.Text,body.Font,new Size(w,0),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl).Height+S(12),noteHeight=TextRenderer.MeasureText(note.Text,note.Font,new Size(w,0),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl).Height+S(12);
            title.SetBounds(p,p,w,S(38));body.SetBounds(p,p+S(58),w,bodyHeight);note.SetBounds(p,body.Bottom+S(22),w,noteHeight);networks.SetBounds(p,note.Bottom+S(18),S(300),S(40));
            card.SetBounds(0,S(62),Width,(networks.Visible?networks.Bottom:note.Bottom)+p);
            Height=card.Bottom+S(12);
        }
    }
    public sealed partial class MainForm {
        HelpGuide helpGuide;
        void BuildHelp(Panel page){page.AutoScroll=true;helpGuide=new HelpGuide(target=>Guard(()=>Open(target))){Dock=DockStyle.Top};page.Controls.Add(helpGuide);}
        void ShowHelp(string topic){tabs.SelectedIndex=5;helpGuide.ShowTopic(topic);helpGuide.Focus();}
    }
}
