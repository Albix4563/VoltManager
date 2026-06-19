using System.Windows.Controls;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Pages
{
    public partial class UninstallConfirmPage : UserControl
    {
        public UninstallConfirmPage()
        {
            InitializeComponent();
            TitleText.Text = I18n.T("uninst_confirm");
            SubText.Text   = I18n.T("uninst_sub");
            WarnText.Text  = I18n.T("uninst_warn");
            Item1.Text     = I18n.T("uninst_item1");
            Item2.Text     = I18n.T("uninst_item2");
            Item3.Text     = I18n.T("uninst_item3");
        }
    }
}
