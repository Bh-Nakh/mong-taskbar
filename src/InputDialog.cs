using System;
using System.Drawing;
using System.Windows.Forms;

namespace TaskbarGroup
{
    public class InputDialog : Form
    {
        private TextBox _tb;

        public string Value
        {
            get { return _tb.Text; }
        }

        private InputDialog(string title, string prompt, string initial)
        {
            this.Text = title;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MinimizeBox = false;
            this.MaximizeBox = false;
            this.ShowInTaskbar = false;
            this.ClientSize = new Size(380, 132);
            this.Font = new Font("Segoe UI", 9f);

            Label lbl = new Label();
            lbl.Text = prompt;
            lbl.SetBounds(14, 16, 352, 20);
            this.Controls.Add(lbl);

            _tb = new TextBox();
            _tb.Text = initial == null ? "" : initial;
            _tb.SetBounds(14, 42, 352, 25);
            this.Controls.Add(_tb);

            Button ok = new Button();
            ok.Text = "확인";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(202, 84, 78, 30);
            this.Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "취소";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(288, 84, 78, 30);
            this.Controls.Add(cancel);

            this.AcceptButton = ok;
            this.CancelButton = cancel;
            _tb.SelectAll();
        }

        public static string Prompt(IWin32Window owner, string title, string prompt, string initial)
        {
            InputDialog d = new InputDialog(title, prompt, initial);
            DialogResult r = owner == null ? d.ShowDialog() : d.ShowDialog(owner);
            if (r == DialogResult.OK) return d.Value;
            return null;
        }
    }
}
