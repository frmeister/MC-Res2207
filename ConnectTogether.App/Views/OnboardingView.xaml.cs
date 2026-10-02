// ConnectTogether.App/Views/OnboardingView.xaml.cs

using System.Windows.Controls;

namespace ConnectTogether.App.Views
{
    public partial class OnboardingView : UserControl
    {
        public OnboardingView()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                NameBox.Focus();
                NameBox.CaretIndex = NameBox.Text.Length;
            };
        }
    }
}
