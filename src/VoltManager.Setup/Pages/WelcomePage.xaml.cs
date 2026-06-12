using System.Windows.Controls;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Pages
{
    public partial class WelcomePage : UserControl
    {
        public WelcomePage()
        {
            InitializeComponent();
            TitleText.Text    = I18n.T("welcome_title");
            SubtitleText.Text = I18n.T("welcome_subtitle");
            InfoText.Text     = I18n.T("welcome_info");
        }
    }
}
