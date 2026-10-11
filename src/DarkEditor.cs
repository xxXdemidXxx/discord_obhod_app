using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
namespace ZapretDesktop {
    public sealed class DarkEditor : UserControl {
        [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
        readonly SlimScroll scroll=new SlimScroll();public readonly TextBox Editor;bool updating;
        sealed class Inner : TextBox {public event Action<int> Wheel;protected override void OnMouseWheel(MouseEventArgs e){if(Wheel!=null)Wheel(-e.Delta/120*3);}}
        public DarkEditor(){BackColor=Theme.Input;Padding=new Padding(14,12,6,12);Editor=new Inner{Dock=DockStyle.Fill,Multiline=true,BorderStyle=BorderStyle.None,ScrollBars=ScrollBars.None,WordWrap=true,AcceptsTab=false,BackColor=Theme.Input,ForeColor=Theme.Text,Font=new Font("Consolas",10)};scroll.Dock=DockStyle.Right;scroll.BackColor=BackColor;Controls.Add(Editor);Controls.Add(scroll);scroll.Changed+=v=>{if(updating || !Editor.IsHandleCreated)return;int first=(int)SendMessage(Editor.Handle,0xCE,IntPtr.Zero,IntPtr.Zero);SendMessage(Editor.Handle,0xB6,IntPtr.Zero,new IntPtr(v-first));};((Inner)Editor).Wheel+=delta=>{RefreshScroll();scroll.Value+=delta;};Editor.TextChanged+=(s,e)=>RefreshScroll();Editor.KeyUp+=(s,e)=>RefreshScroll();Editor.MouseUp+=(s,e)=>RefreshScroll();Editor.Resize+=(s,e)=>RefreshScroll();Editor.HandleCreated+=(s,e)=>RefreshScroll();}
        public void RefreshScroll(){if(!Editor.IsHandleCreated || updating)return;updating=true;int lines=(int)SendMessage(Editor.Handle,0xBA,IntPtr.Zero,IntPtr.Zero),first=(int)SendMessage(Editor.Handle,0xCE,IntPtr.Zero,IntPtr.Zero),page=Math.Max(1,Editor.ClientSize.Height/Editor.Font.Height);scroll.SetRange(lines-page,page,first);updating=false;}
    }
}
