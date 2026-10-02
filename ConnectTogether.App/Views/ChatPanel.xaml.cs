// ConnectTogether.App/Views/ChatPanel.xaml.cs

using System.Collections.Specialized;
using System.Windows.Controls;

namespace ConnectTogether.App.Views
{
    public partial class ChatPanel : UserControl
    {
        public ChatPanel()
        {
            InitializeComponent();
            // Новое сообщение — прокручиваем к нему, как в любом мессенджере
            ((INotifyCollectionChanged)Messages.Items).CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add) Dispatcher.BeginInvoke(Scroller.ScrollToEnd);
            };
            Loaded += (_, _) => Scroller.ScrollToEnd();
        }
    }
}
