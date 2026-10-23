using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace HSaiHelper.Hdt
{
	/// <summary>
	/// «Командная строка» к ИИ: маленькое окно поверх игры рядом с панелью HSAI.
	/// Enter — отправить (окно остаётся для следующего вопроса), Esc или потеря фокуса — закрыть.
	/// Отдельное окно нужно потому, что оверлей HDT не получает фокус клавиатуры.
	/// </summary>
	internal class AskWindow : Window
	{
		private readonly TextBox _box;

		public AskWindow(Point screenPoint)
		{
			Title = L.T("HSAI — спросить ИИ", "HSAI — ask the AI");
			WindowStyle = WindowStyle.None;
			ResizeMode = ResizeMode.NoResize;
			SizeToContent = SizeToContent.WidthAndHeight;
			ShowInTaskbar = false;
			Topmost = true;
			Background = new SolidColorBrush(Color.FromRgb(24, 24, 34));
			BorderBrush = new SolidColorBrush(Color.FromRgb(167, 139, 250));
			BorderThickness = new Thickness(1);
			WindowStartupLocation = WindowStartupLocation.Manual;

			// Координаты панели — в пикселях экрана; окну нужны независимые от DPI единицы.
			var source = PresentationSource.FromVisual(Application.Current?.MainWindow);
			var dip = source?.CompositionTarget?.TransformFromDevice.Transform(screenPoint) ?? screenPoint;
			Left = dip.X;
			Top = dip.Y;

			_box = new TextBox
			{
				Width = 380, FontFamily = new FontFamily("Segoe UI"), FontSize = 13, Padding = new Thickness(4, 3, 4, 3),
				Background = new SolidColorBrush(Color.FromRgb(36, 36, 50)), Foreground = Brushes.White, CaretBrush = Brushes.White,
				BorderBrush = new SolidColorBrush(Color.FromRgb(90, 80, 130)),
			};
			var hint = new TextBlock
			{
				Text = L.T("Enter — спросить ИИ · /заметка текст — просто заметка · Esc — закрыть",
					"Enter — ask the AI · /note text — just a note · Esc — close"),
				Foreground = new SolidColorBrush(Color.FromRgb(150, 146, 168)), FontFamily = new FontFamily("Segoe UI"), FontSize = 11, Margin = new Thickness(0, 4, 0, 0),
			};
			var stack = new StackPanel { Margin = new Thickness(8) };
			stack.Children.Add(_box);
			stack.Children.Add(hint);
			Content = stack;

			_box.KeyDown += (s, e) =>
			{
				if(e.Key == Key.Enter)
				{
					AiChat.Submit(_box.Text);
					_box.Clear();
					e.Handled = true;
				}
				else if(e.Key == Key.Escape)
				{
					SafeClose();
					e.Handled = true;
				}
			};
			Loaded += (s, e) =>
			{
				Activate();
				_box.Focus();
				Keyboard.Focus(_box);
			};
			Deactivated += (s, e) => SafeClose();
			Closing += (s, e) => _closing = true;
		}

		private bool _closing;

		/// <summary>Esc закрывает окно → оно теряет фокус → Deactivated; второй Close во время закрытия WPF не разрешает.</summary>
		private void SafeClose()
		{
			if(!_closing)
				Close();
		}
	}
}
