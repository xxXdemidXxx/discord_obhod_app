using System.Drawing;
using System.Windows.Forms;

namespace ZapretDesktop {
    public sealed class FlatAction : Button {
        protected override void OnPaint(PaintEventArgs e) {
            if (Enabled) {base.OnPaint(e); return;}
            using (var brush = new SolidBrush(Color.FromArgb(25, 31, 45))) e.Graphics.FillRectangle(brush, ClientRectangle);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.FromArgb(100, 113, 133), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
