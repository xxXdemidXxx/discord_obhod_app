using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretDesktop {
    public enum AppUpdateState { Idle, Checking, Current, Available, Downloading, Ready, Installing, Failed }
    // One check, followed by a separate, explicit install action.
    public sealed class AppUpdateSession : IDisposable {
        readonly ShellUpdates updates;
        CancellationTokenSource cancellation;
        public AppUpdateState State { get; private set; }
        public Release Release { get; private set; }
        public string Prepared { get; private set; }
        public string Message { get; private set; }
        public string Detail { get; private set; }
        public string LastError { get; private set; }
        public int Percent { get; private set; }
        public event Action Changed;
        public bool Working { get { return State==AppUpdateState.Checking || State==AppUpdateState.Downloading || State==AppUpdateState.Installing; } }
        public string ActionText { get { return State==AppUpdateState.Checking?"Проверяем…":State==AppUpdateState.Downloading?"Скачиваем…":State==AppUpdateState.Installing?"Перезапуск…":State==AppUpdateState.Failed?(Release==null?"Повторить проверку":"Повторить обновление"):Release!=null?"Обновить":"Проверить обновления"; } }
        public AppUpdateSession(ShellUpdates updates) { this.updates=updates; Message="";Detail=""; }
        void Set(AppUpdateState state,string message,string detail) { State=state;Message=message;Detail=detail;if(Changed!=null)Changed(); }
        public async Task Check() {
            if(Working)return;
            Release=null;Prepared=null;Percent=0;LastError="";
            Set(AppUpdateState.Checking,"Проверяем обновления…","");
            try {
                Release=await updates.Check();
                Set(Release==null?AppUpdateState.Current:AppUpdateState.Available,Release==null?"У вас последняя версия":"Доступна версия "+Release.Version,Release==null?"":"Приложение перезапустится. Настройки сохранятся.");
            }catch(Exception ex){LastError=ex.Message;Fail(ex is UpdateCheckException?ex.Message:"Не удалось проверить обновления. Попробуйте ещё раз.");}
        }
        public async Task<bool> Prepare(string root) {
            if(Working || Release==null)return false;
            if(Prepared!=null){Set(AppUpdateState.Ready,"Обновление готово","");return true;}
            cancellation=new CancellationTokenSource();Percent=0;LastError="";
            Set(AppUpdateState.Downloading,"Скачиваем обновление…","0%");
            var progress=new Progress<int>(value=>{if(State!=AppUpdateState.Downloading)return;Percent=value;Message=value==100?"Подготавливаем обновление…":"Скачиваем обновление…";Detail=value==100?"Почти готово":value+"%";if(Changed!=null)Changed();});
            try {
                Prepared=await updates.Stage(Release,root,progress,cancellation.Token);
                Set(AppUpdateState.Ready,"Обновление готово","");return true;
            }catch(OperationCanceledException){Set(AppUpdateState.Available,"Обновление отменено","Можно установить позже.");return false;}
            catch(Exception ex){LastError=ex.Message;Fail("Не удалось скачать обновление. Текущая версия сохранена.");return false;}
            finally{cancellation.Dispose();cancellation=null;}
        }
        public void Installing() { Set(AppUpdateState.Installing,"Перезапускаем приложение…","Подключение восстановится автоматически."); }
        public void Fail(string message) {Prepared=null;Set(AppUpdateState.Failed,message,"");}
        public void Complete() {Set(AppUpdateState.Current,"Обновлено до версии "+ShellUpdates.Current,"");}
        public void Cancel() {if(cancellation!=null)cancellation.Cancel();}
        public void Dispose(){Cancel();updates.Dispose();}
    }
}
