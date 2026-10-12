using System;
using System.ComponentModel;
using System.IO;
using System.Windows.Forms;
using System.Drawing;

namespace ZapretDesktop {
    public static class UserProblems {
        public static string Message(Exception error) {
            Exception cause=error;
            while(cause.InnerException!=null)cause=cause.InnerException;
            var windows=cause as Win32Exception;
            if(windows!=null && windows.NativeErrorCode==1223)
                return "Запуск отменён в окне Windows.\nЧтобы включить обход, повторите запуск и подтвердите запрос Windows.";
            if(error is EngineDriverException)
                return "Windows не смогла запустить драйвер WinDivert. Подбор остановлен.\nПерезагрузите компьютер и повторите запуск. Если ошибка останется, сохраните журнал.";
            if(error is EngineSetupException && cause is InvalidDataException)
                return "Не удалось проверить файлы программы.\nСкачайте полный ZIP ByPass и распакуйте его в новую папку.";
            if(error is EngineSetupException)
                return "Не удалось включить обход. Подбор остановлен.\nЗакройте ByPass через значок у часов, откройте снова и повторите запуск.";
            if(error is EngineStartException)
                return "Выбранный вариант обхода не удалось запустить.\nПопробуйте «Подобрать автоматически». Подробности сохранены в журнале.";
            if(cause is UnauthorizedAccessException)
                return "Windows не разрешила доступ к файлам программы.\nРаспакуйте полный ZIP ByPass в новую папку и повторите действие.";
            if(error is InvalidOperationException && error.InnerException==null)
                return error.Message;
            return "Не удалось выполнить действие. Попробуйте ещё раз.\nЕсли это повторяется, откройте «Журнал» и передайте его разработчику.";
        }
    }
    public sealed class ProblemNotice : ResponsiveControl {
        public readonly Label Message=Theme.Label("",9);
        public readonly Button Details=Theme.Button("Журнал"),Dismiss=Theme.Button("×");
        public ProblemNotice() {
            Name="ProblemNotice";Dock=DockStyle.Top;BackColor=Theme.Surface;Height=S(66);Visible=false;
            Message.TextAlign=ContentAlignment.MiddleLeft;Message.ForeColor=Theme.Text;
            Dismiss.AccessibleName="Скрыть сообщение";Dismiss.Click+=(s,e)=>Clear();
            Controls.AddRange(new Control[]{Message,Details,Dismiss});
        }
        public void ShowProblem(Exception error){Message.Text=UserProblems.Message(error);Visible=true;}
        public void Clear(){Visible=false;}
        protected override void OnLayout(LayoutEventArgs e) {
            base.OnLayout(e);if(Message==null)return;
            Message.SetBounds(S(24),0,Math.Max(0,Width-S(190)),Height);
            Details.SetBounds(Width-S(152),(Height-S(32))/2,S(96),S(32));
            Dismiss.SetBounds(Width-S(48),(Height-S(32))/2,S(40),S(32));
        }
    }
}
