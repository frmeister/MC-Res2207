// ConnectTogether.App/Views/JoinView.xaml.cs

using System.Windows;
using System.Windows.Controls;
using ConnectTogether.App.Services;

namespace ConnectTogether.App.Views
{
    public partial class JoinView : UserControl
    {
        public JoinView()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                AddressBox.Focus();
                AddressBox.CaretIndex = AddressBox.Text.Length;
            };
            DataObject.AddPastingHandler(AddressBox, OnPaste);
        }

        /// <summary>Вставили сообщение от друга целиком — оставляем в поле только адрес из него.</summary>
        private void OnPaste(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text) return;

            string address = RoomAddress.Extract(text);
            if (address == text) return;

            e.CancelCommand();
            AddressBox.Text = address;
            AddressBox.CaretIndex = address.Length;
        }
    }
}
