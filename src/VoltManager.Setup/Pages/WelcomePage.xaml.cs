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

            Feat1Title.Text = I18n.T("feat1_t"); Feat1Desc.Text = I18n.T("feat1_d");
            Feat2Title.Text = I18n.T("feat2_t"); Feat2Desc.Text = I18n.T("feat2_d");
            Feat3Title.Text = I18n.T("feat3_t"); Feat3Desc.Text = I18n.T("feat3_d");
        }
    }
}
