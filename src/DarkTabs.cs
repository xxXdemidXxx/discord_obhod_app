using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
namespace ZapretDesktop {
    public sealed class DarkTabs : ResponsiveControl {
        readonly Panel navigation,content;readonly List<Button> buttons=new List<Button>();
        public readonly List<Panel> TabPages=new List<Panel>();int selected=-1;
        public int SelectedIndex {get{return selected;}set{if(value<0 || value>=TabPages.Count || buttons[value].Tag!=null)return;selected=value;for(int i=0;i<TabPages.Count;i++){TabPages[i].Visible=i==value;buttons[i].BackColor=i==value?Theme.Accent:Theme.Surface;buttons[i].ForeColor=i==value?Theme.Text:Theme.Muted;}TabPages[value].BringToFront();}}
        public DarkTabs(){DoubleBuffered=true;BackColor=Theme.Background;navigation=new Panel{Dock=DockStyle.Top,Height=S(58),BackColor=Theme.Surface,Padding=new Padding(24,9,24,9)};content=new Panel{Dock=DockStyle.Fill,BackColor=BackColor};Controls.Add(content);Controls.Add(navigation);navigation.Resize+=(s,e)=>LayoutButtons();}
        void LayoutButtons(){var shown=buttons.FindAll(item=>item.Tag==null);int x=S(24),available=Math.Max(1,navigation.Width-S(48)-Math.Max(0,shown.Count-1)*S(8));int total=0;foreach(var button in shown)total+=Math.Max(S(86),TextRenderer.MeasureText(button.Text,button.Font).Width+S(28));foreach(var button in shown){int natural=Math.Max(S(86),TextRenderer.MeasureText(button.Text,button.Font).Width+S(28)),width=total>available?(int)((long)natural*available/total):natural;button.SetBounds(x,S(9),width,S(38));x+=button.Width+S(8);}}
        public void SetPageAvailable(int index,bool available){if(index<0 || index>=buttons.Count)return;bool was=buttons[index].Tag==null;if(was==available)return;buttons[index].Tag=available?null:(object)"hidden";buttons[index].Visible=available;if(!available && selected==index)SelectedIndex=0;LayoutButtons();}
        public void SetNotice(int index,bool visible){string text=TabPages[index].Text+(visible?" •":"");if(buttons[index].Text==text)return;buttons[index].Text=text;LayoutButtons();}
        public void AddPages(Panel[] pages){foreach(var page in pages){int index=TabPages.Count;TabPages.Add(page);page.Dock=DockStyle.Fill;page.BackColor=BackColor;content.Controls.Add(page);var button=Theme.Button(page.Text);button.Click+=(s,e)=>SelectedIndex=index;buttons.Add(button);navigation.Controls.Add(button);}LayoutButtons();SelectedIndex=0;}
    }
}
