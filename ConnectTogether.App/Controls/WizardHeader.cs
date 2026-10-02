// ConnectTogether.App/Controls/WizardHeader.cs

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace ConnectTogether.App.Controls
{
    /// <summary>
    /// Шапка мастера создания комнаты: «Назад» слева и три шага (Игра → Сервер → Приглашение) по центру.
    /// Шаги до текущего — готово (зелёная галочка), текущий — акцентный или ошибка, следующие — ждут.
    /// </summary>
    public sealed class WizardHeader : Grid
    {
        private static readonly string[] StepNames = { "Игра", "Сервер", "Приглашение" };

        public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
            nameof(Step), typeof(int), typeof(WizardHeader), new PropertyMetadata(1, (d, _) => ((WizardHeader)d).Rebuild()));

        public static readonly DependencyProperty IsErrorProperty = DependencyProperty.Register(
            nameof(IsError), typeof(bool), typeof(WizardHeader), new PropertyMetadata(false, (d, _) => ((WizardHeader)d).Rebuild()));

        public static readonly DependencyProperty BackCommandProperty = DependencyProperty.Register(
            nameof(BackCommand), typeof(ICommand), typeof(WizardHeader), new PropertyMetadata(null, (d, _) => ((WizardHeader)d).Rebuild()));

        public WizardHeader()
        {
            Margin = new Thickness(32, 18, 32, 18);
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            Rebuild();
        }

        /// <summary>Текущий шаг, 1–3.</summary>
        public int Step { get => (int)GetValue(StepProperty); set => SetValue(StepProperty, value); }

        /// <summary>Текущий шаг завершился ошибкой (сервер не найден).</summary>
        public bool IsError { get => (bool)GetValue(IsErrorProperty); set => SetValue(IsErrorProperty, value); }

        /// <summary>Команда «Назад»; если null, кнопки нет.</summary>
        public ICommand? BackCommand { get => (ICommand?)GetValue(BackCommandProperty); set => SetValue(BackCommandProperty, value); }

        private void Rebuild()
        {
            Children.Clear();

            if (BackCommand != null)
            {
                var back = new Button { Content = "Назад", Command = BackCommand, HorizontalAlignment = HorizontalAlignment.Left };
                back.SetResourceReference(StyleProperty, "Btn.Back");
                Children.Add(back);
            }

            var steps = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            SetColumn(steps, 1);
            for (int i = 1; i <= StepNames.Length; i++)
            {
                if (i > 1)
                {
                    var line = new Border { Width = 40, Height = 1.5, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                    line.SetResourceReference(Border.BackgroundProperty, i - 1 < Step ? "Ok" : "Bd2");
                    steps.Children.Add(line);
                }
                steps.Children.Add(CreateStep(i));
            }
            Children.Add(steps);
        }

        private UIElement CreateStep(int index)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = StepNames[index - 1], FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };

            var badge = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12) };
            var number = new TextBlock
            {
                Text = index.ToString(), FontSize = 12, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };

            if (index < Step)
            {
                badge.SetResourceReference(Border.BackgroundProperty, "OkSoft");
                var check = new Icon { Kind = "check", StrokeWidth = 2.6, Width = 13, Height = 13 };
                check.SetResourceReference(Icon.ForegroundProperty, "Ok");
                badge.Child = check;
                label.SetResourceReference(TextBlock.ForegroundProperty, "Tx2");
            }
            else if (index == Step && IsError)
            {
                badge.SetResourceReference(Border.BackgroundProperty, "ErSoft");
                number.Text = "!";
                number.FontSize = 13;
                number.SetResourceReference(TextBlock.ForegroundProperty, "Er");
                badge.Child = number;
                label.FontWeight = FontWeights.SemiBold;
            }
            else if (index == Step)
            {
                badge.SetResourceReference(Border.BackgroundProperty, "Ac");
                number.SetResourceReference(TextBlock.ForegroundProperty, "AcTx");
                badge.Child = number;
                label.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                badge.Width = badge.Height = 22;
                badge.Margin = new Thickness(1);
                badge.BorderThickness = new Thickness(1.5);
                badge.SetResourceReference(Border.BorderBrushProperty, "Bd2");
                number.FontWeight = FontWeights.SemiBold;
                badge.Child = number;
                item.SetResourceReference(TextElement.ForegroundProperty, "Tx3");
            }

            item.Children.Add(badge);
            item.Children.Add(label);
            return item;
        }
    }
}
