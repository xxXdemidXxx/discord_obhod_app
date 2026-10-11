using System;
using System.Drawing;
using System.Windows.Forms;
namespace ZapretDesktop {
    public sealed class DomainEditor : ResponsiveControl {
        readonly Label title,description,status,hint;readonly DarkSelect list;readonly DarkEditor text;readonly Button save,discard;string engine,baseline="";int loaded;bool loading;bool allowed=true;
        public event Action<int,string> SaveRequested;public bool Dirty{get{return text.Editor.Text!=baseline;}}
        public DomainEditor(){BackColor=Theme.Background;title=Theme.Label("Ваши сайты",18,true);description=Theme.Label("Добавьте сайты, для которых нужен обход, или исключения.",10);description.ForeColor=Theme.Muted;list=new DarkSelect();list.Items.AddRange(new object[]{"Применять обход","Не применять обход","Исключить IP-адреса"});list.SelectedIndex=0;list.SelectedIndexChanged+=(s,e)=>ChangeList();text=new DarkEditor();text.Editor.TextChanged+=(s,e)=>{if(!loading){status.Text=Dirty?"Есть несохранённые изменения":"";RefreshSave();}};save=Theme.Button("Сохранить",true);save.Click+=(s,e)=>{if(SaveRequested!=null)SaveRequested(loaded,text.Editor.Text);};discard=Theme.Button("Отменить изменения");discard.Click+=(s,e)=>Discard();status=Theme.Label("",9);status.ForeColor=Theme.Muted;hint=Theme.Label("Один домен на строку: example.com. Без https:// и путей.",9);hint.ForeColor=Theme.Muted;Controls.AddRange(new Control[]{title,description,list,text,hint,save,discard,status});}
        public void LoadEngine(string path){engine=path;loading=true;loaded=Math.Max(0,list.SelectedIndex);text.Editor.Text=DomainLists.Read(path,loaded);baseline=text.Editor.Text;loading=false;status.Text="";RefreshSave();}
        void ChangeList(){if(engine==null || loading)return;int next=list.SelectedIndex;if(Dirty){loading=true;list.SelectedIndex=loaded;loading=false;status.Text="Сначала сохраните изменения или нажмите «Отменить изменения».";return;}loaded=next;LoadEngine(engine);hint.Text=loaded==2?"Один IP-адрес или сеть на строку: 192.168.1.0/24.":"Один домен на строку: example.com. Без https:// и путей.";}
        public void Saved(){LoadEngine(engine);status.Text="Список сохранён";status.ForeColor=Theme.Success;}
        public void SetAllowed(bool canSave,bool connected){allowed=canSave;text.Editor.ReadOnly=!canSave;list.Enabled=canSave;save.Text=connected?"Сохранить и применить":"Сохранить";RefreshSave();}
        void RefreshSave(){save.Enabled=allowed && Dirty;discard.Enabled=Dirty;}
        public void SetError(string message){status.Text=message;status.ForeColor=Theme.Danger;}
        public void Discard(){LoadEngine(engine);}
        public TextBox TextEditor{get{return text.Editor;}}
        protected override void OnLayout(LayoutEventArgs e){base.OnLayout(e);if(title==null)return;title.SetBounds(0,0,Width,S(38));description.SetBounds(0,S(43),Width,S(26));list.SetBounds(0,S(85),Math.Min(Width,S(420)),S(40));hint.SetBounds(0,S(139),Width,S(24));text.SetBounds(0,S(178),Width,Math.Max(S(80),Height-S(286)));save.SetBounds(0,Height-S(56),S(230),S(40));discard.SetBounds(S(244),Height-S(56),S(220),S(40));status.SetBounds(0,Height-S(92),Width,S(30));}
    }
}
