using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace TaskbarGroup
{
    // 그룹 목록: 색 칩 + 이름 + 항목 수를 그리고, 끌어서 순서를 바꿀 수 있는 ListBox.
    // Items 에는 Group 개체를 넣는다.
    public class GroupListBox : ListBox
    {
        private const int WM_PAINT = 0x000F;
        private const string DragFormat = "TaskbarGroupIndex";
        private static readonly Color Accent = Color.FromArgb(0, 103, 192);

        private int _downIndex = -1;
        private Point _downPt;
        private int _dropLine = -1;

        /// <summary>(원래 위치, 옮겨 간 위치) — 이미 빼고 넣은 뒤의 인덱스.</summary>
        public event Action<int, int> GroupMoved;

        public GroupListBox()
        {
            this.DrawMode = DrawMode.OwnerDrawFixed;
            this.ItemHeight = 40;
            this.IntegralHeight = false;
            this.AllowDrop = true;
            this.BorderStyle = BorderStyle.FixedSingle;
            this.BackColor = Color.White;
            this.DoubleBuffered = true;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            Group g = this.Items[e.Index] as Group;
            if (g == null) return;

            Graphics gr = e.Graphics;
            gr.SmoothingMode = SmoothingMode.AntiAlias;
            gr.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            Rectangle r = e.Bounds;
            bool sel = (e.State & DrawItemState.Selected) != 0;

            using (Brush bg = new SolidBrush(sel ? Color.FromArgb(222, 236, 252) : Color.White))
                gr.FillRectangle(bg, r);
            if (sel)
                using (Brush bar = new SolidBrush(Accent))
                    gr.FillRectangle(bar, r.X, r.Y + 6, 3, r.Height - 12);

            // 색 칩 + 첫 글자 (실제 그룹 아이콘과 같은 색)
            Rectangle chip = new Rectangle(r.X + 12, r.Y + (r.Height - 26) / 2, 26, 26);
            using (GraphicsPath p = Round(chip, 6))
            using (Brush b = new SolidBrush(GroupIcon.ColorAt(e.Index)))
                gr.FillPath(b, p);
            using (Font f = new Font("Segoe UI", 10f, FontStyle.Bold))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                gr.DrawString(GroupIcon.FirstLetter(g.Name), f, Brushes.White, chip, sf);
            }

            // 항목 수 (오른쪽)
            string cnt = g.Entries.Count.ToString();
            Size cs = TextRenderer.MeasureText(cnt, this.Font);
            Rectangle cr = new Rectangle(r.Right - cs.Width - 14, r.Y, cs.Width + 4, r.Height);
            TextRenderer.DrawText(gr, cnt, this.Font, cr, Color.Gray,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding);

            // 이름
            Rectangle nr = new Rectangle(chip.Right + 10, r.Y, cr.X - chip.Right - 14, r.Height);
            TextRenderer.DrawText(gr, g.Name, this.Font, nr, Color.FromArgb(32, 32, 32),
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            using (Pen sep = new Pen(Color.FromArgb(238, 238, 238)))
                gr.DrawLine(sep, r.X + 12, r.Bottom - 1, r.Right - 8, r.Bottom - 1);
        }

        private static GraphicsPath Round(Rectangle r, int rad)
        {
            int d = rad * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---------- 끌어서 순서 바꾸기 ----------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _downIndex = e.Button == MouseButtons.Left ? this.IndexFromPoint(e.Location) : -1;
            _downPt = e.Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_downIndex < 0 || (e.Button & MouseButtons.Left) == 0) return;
            Size ds = SystemInformation.DragSize;
            if (Math.Abs(e.X - _downPt.X) < ds.Width && Math.Abs(e.Y - _downPt.Y) < ds.Height) return;
            int from = _downIndex;
            _downIndex = -1;
            DoDragDrop(new DataObject(DragFormat, from), DragDropEffects.Move);
            SetDropLine(-1);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _downIndex = -1;
        }

        protected override void OnDragEnter(DragEventArgs drgevent)
        {
            drgevent.Effect = drgevent.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        }

        protected override void OnDragOver(DragEventArgs drgevent)
        {
            if (!drgevent.Data.GetDataPresent(DragFormat)) { drgevent.Effect = DragDropEffects.None; return; }
            drgevent.Effect = DragDropEffects.Move;
            Point pt = PointToClient(new Point(drgevent.X, drgevent.Y));
            if (pt.Y < 14 && this.TopIndex > 0) this.TopIndex--;
            else if (pt.Y > this.ClientSize.Height - 14 && this.TopIndex < this.Items.Count - 1) this.TopIndex++;
            SetDropLine(InsertIndex(pt));
        }

        protected override void OnDragLeave(EventArgs e)
        {
            SetDropLine(-1);
        }

        protected override void OnDragDrop(DragEventArgs drgevent)
        {
            Point pt = PointToClient(new Point(drgevent.X, drgevent.Y));
            int insert = InsertIndex(pt);
            SetDropLine(-1);
            if (!drgevent.Data.GetDataPresent(DragFormat)) return;
            int from = (int)drgevent.Data.GetData(DragFormat);
            if (from < 0 || from >= this.Items.Count) return;
            int to = insert;
            if (to > from) to--;
            if (to < 0) to = 0;
            if (to > this.Items.Count - 1) to = this.Items.Count - 1;
            if (to == from) return;
            if (GroupMoved != null) GroupMoved(from, to);
        }

        private int InsertIndex(Point pt)
        {
            int idx = this.IndexFromPoint(pt);
            if (idx < 0) return pt.Y < ItemHeight ? 0 : this.Items.Count;
            Rectangle r = this.GetItemRectangle(idx);
            return pt.Y > r.Top + r.Height / 2 ? idx + 1 : idx;
        }

        private void SetDropLine(int index)
        {
            if (_dropLine == index) return;
            _dropLine = index;
            this.Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_PAINT && _dropLine >= 0 && this.Items.Count > 0)
            {
                int y = _dropLine >= this.Items.Count
                    ? this.GetItemRectangle(this.Items.Count - 1).Bottom
                    : this.GetItemRectangle(_dropLine).Top;
                y = Math.Max(2, Math.Min(this.ClientSize.Height - 3, y));
                using (Graphics g = Graphics.FromHwnd(this.Handle))
                using (Pen p = new Pen(Accent, 2f))
                {
                    g.DrawLine(p, 4, y, this.ClientSize.Width - 5, y);
                    g.DrawLine(p, 4, y - 4, 4, y + 4);
                    g.DrawLine(p, this.ClientSize.Width - 5, y - 4, this.ClientSize.Width - 5, y + 4);
                }
            }
        }
    }
}
