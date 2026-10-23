using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HSaiHelper.Hdt
{
	/// <summary>Настройки HSAI (кнопка «Настройки» в списке плагинов HDT): язык, адрес ИИ-сервера, положение панели.</summary>
	internal class SettingsWindow : Window
	{
		private static readonly Brush Back = new SolidColorBrush(Color.FromRgb(24, 24, 34));
		private static readonly Brush Fore = new SolidColorBrush(Color.FromRgb(232, 230, 240));
		private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(150, 146, 168));

		public SettingsWindow()
		{
			var s = Settings.Instance;
			Title = $"HSAI {Paths.VersionText} — " + L.T("настройки", "settings");
			SizeToContent = SizeToContent.WidthAndHeight;
			ResizeMode = ResizeMode.NoResize;
			WindowStartupLocation = WindowStartupLocation.CenterScreen;
			Background = Back;
			Topmost = true;

			var language = new ComboBox { Width = 220, Margin = new Thickness(0, 2, 0, 10) };
			language.Items.Add(new ComboBoxItem { Content = L.T("как язык карт в HDT", "same as HDT card language"), Tag = "auto" });
			language.Items.Add(new ComboBoxItem { Content = "Русский", Tag = "ru" });
			language.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
			foreach(ComboBoxItem item in language.Items)
				if((string)item.Tag == (s.Language ?? "auto"))
					language.SelectedItem = item;

			var aiUrl = new TextBox { Text = s.AiUrl, Width = 220, Margin = new Thickness(0, 2, 0, 2) };

			var answer = new ComboBox { Width = 220, Margin = new Thickness(0, 2, 0, 2) };
			answer.Items.Add(new ComboBoxItem { Content = L.T("английский (быстрее)", "English (faster)"), Tag = "en" });
			answer.Items.Add(new ComboBoxItem { Content = L.T("русский (~на треть дольше)", "Russian (~a third slower)"), Tag = "ru" });
			foreach(ComboBoxItem item in answer.Items)
				if((string)item.Tag == (s.AiAnswerLanguage ?? "ru"))
					answer.SelectedItem = item;

			// Модель ИИ (tools/bench_llm.py, 2026-10-23): 26B-A4B — быстрее (18–23 ток/с), 12B — лучше рассуждает без готовых чисел
			// («что купить» по текстам карт: 0,56 против 0,33), с готовыми шансами обе ≈ 0,95.
			var model = new ComboBox { Width = 220, Margin = new Thickness(0, 2, 0, 2) };
			model.Items.Add(new ComboBoxItem { Content = L.T("Gemma-4-26B-A4B — быстрее (по умолчанию)", "Gemma-4-26B-A4B — faster (default)"), Tag = "" });
			model.Items.Add(new ComboBoxItem { Content = L.T("Gemma-4-12B — лучше рассуждает сама", "Gemma-4-12B — better own reasoning"), Tag = "gemma-4-12b-it-Q4_K_M.gguf" });
			model.Items.Add(new ComboBoxItem { Content = L.T("Gemma-4-E4B — для слабых ПК", "Gemma-4-E4B — for weak PCs"), Tag = "gemma-4-E4B-it-Q4_K_M.gguf" });
			foreach(ComboBoxItem item in model.Items)
				if((string)item.Tag == (s.AiModelFile ?? ""))
					model.SelectedItem = item;
			model.SelectedItem ??= model.Items[0];

			var reset = new Button { Content = L.T("Вернуть окна на места (лобби слева, остальное справа)", "Move the windows back (lobby left, the rest right)"), Margin = new Thickness(0, 4, 0, 10), Padding = new Thickness(8, 3, 8, 3) };
			reset.Click += (o, e) => HsaiOverlay.Instance.ResetPositions();

			// Размер окон — как масштаб оверлея в HDT; применяется сразу, пока тянешь.
			var scaleLabel = Label(ScaleText(s.PanelScale));
			var scale = new Slider { Minimum = 50, Maximum = 150, TickFrequency = 5, IsSnapToTickEnabled = true, Value = s.PanelScale, Width = 220, Margin = new Thickness(0, 2, 0, 6) };
			scale.ValueChanged += (o, e) =>
			{
				s.PanelScale = (int)scale.Value;
				scaleLabel.Text = ScaleText(s.PanelScale);
				HsaiOverlay.Instance.RefreshScale();
				s.Save();
			};

			// Горячая клавиша: щёлкнуть в поле и нажать сочетание (Delete/Backspace — выключить).
			var hotkey = new TextBox { Text = s.CollapseHotkey ?? "", Width = 160, IsReadOnly = true, Margin = new Thickness(0, 2, 6, 2), Cursor = System.Windows.Input.Cursors.Hand };
			hotkey.PreviewKeyDown += (o, e) =>
			{
				e.Handled = true;
				var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
				if(key == System.Windows.Input.Key.Delete || key == System.Windows.Input.Key.Back)
				{
					hotkey.Text = "";
					return;
				}
				if(key == System.Windows.Input.Key.LeftCtrl || key == System.Windows.Input.Key.RightCtrl || key == System.Windows.Input.Key.LeftShift
				   || key == System.Windows.Input.Key.RightShift || key == System.Windows.Input.Key.LeftAlt || key == System.Windows.Input.Key.RightAlt
				   || key == System.Windows.Input.Key.LWin || key == System.Windows.Input.Key.RWin)
					return;
				var text = Hotkey.Describe(System.Windows.Input.Keyboard.Modifiers, key);
				if(Hotkey.TryParse(text, out _, out _))
					hotkey.Text = text;
			};
			var hotkeyRow = new StackPanel { Orientation = Orientation.Horizontal };
			hotkeyRow.Children.Add(hotkey);
			var hotkeyNote = new TextBlock { Foreground = Dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
				Text = L.T("щёлкни и нажми сочетание; Delete — выключить", "click and press a combination; Delete — off") };
			hotkeyRow.Children.Add(hotkeyNote);

			var save = new Button { Content = L.T("Сохранить", "Save"), IsDefault = true, Padding = new Thickness(14, 3, 14, 3), HorizontalAlignment = HorizontalAlignment.Right };
			save.Click += (o, e) =>
			{
				s.Language = (string)((ComboBoxItem)language.SelectedItem)?.Tag ?? "auto";
				s.AiUrl = string.IsNullOrWhiteSpace(aiUrl.Text) ? "http://127.0.0.1:8091" : aiUrl.Text.Trim();
				s.AiAnswerLanguage = (string)((ComboBoxItem)answer.SelectedItem)?.Tag ?? "ru";
				s.CollapseHotkey = hotkey.Text;
				var hotkeyError = Hotkey.Apply(s.CollapseHotkey);
				if(hotkeyError != null)
					ActivityFeed.Post(hotkeyError, ActivityLevel.Warning);
				var newModel = (string)((ComboBoxItem)model.SelectedItem)?.Tag ?? "";
				bool modelChanged = newModel != (s.AiModelFile ?? "");
				s.AiModelFile = newModel;
				s.Save();
				if(modelChanged && s.AiAutoStart)
				{
					// Перезапустить наш сервер с новой моделью (вручную запущенный не трогаем).
					AiServer.Stop();
					AiServer.SetEnabled(true);
				}
				Close();
			};

			var stack = new StackPanel { Margin = new Thickness(16) };
			stack.Children.Add(Label(L.T("Язык интерфейса", "Interface language")));
			stack.Children.Add(language);
			stack.Children.Add(Label(L.T("Адрес ИИ-сервера (llama.cpp на этом компьютере)", "AI server address (llama.cpp on this PC)")));
			stack.Children.Add(aiUrl);
			stack.Children.Add(Label(L.T("Модель ИИ", "AI model")));
			stack.Children.Add(model);
			stack.Children.Add(Label(L.T("Язык ответов ИИ (запрос к модели всегда на английском)", "AI answer language (the prompt is always English)")));
			stack.Children.Add(answer);
			stack.Children.Add(new TextBlock { Text = L.T("ИИ запускается сам при входе в Battlegrounds (тумблер — в окне «ИИ»)", "The AI starts automatically in Battlegrounds (toggle in the \"AI\" window)"), Foreground = Dim, FontSize = 11, Margin = new Thickness(0, 0, 0, 10) });
			stack.Children.Add(Label(L.T("Окна HSAI", "HSAI windows")));
			stack.Children.Add(scaleLabel);
			stack.Children.Add(scale);
			stack.Children.Add(reset);
			stack.Children.Add(Label(L.T("Свернуть/развернуть все окна HSAI — горячая клавиша", "Collapse/expand all HSAI windows — hotkey")));
			stack.Children.Add(hotkeyRow);

			// Данные для проекта: обезличенный архив записей партий; отправляет человек сам (ничего не уходит в сеть автоматически).
			stack.Children.Add(Label(L.T("Данные для проекта", "Data for the project")));
			stack.Children.Add(new TextBlock
			{
				Text = L.T("Архив записей партий без ников, точного времени и точного MMR (MMR — диапазоном). Ничего не отправляется само: архив можно приложить к сообщению проекту, при желании.",
					"An archive of game records without names, exact times or exact MMR (MMR as a range). Nothing is sent automatically: the archive can be attached to a message to the project."),
				Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Width = 320, Margin = new Thickness(0, 2, 0, 4),
			});
			var chat = new CheckBox { Content = L.T("включить твои вопросы ИИ и заметки", "include your AI questions and notes"), Foreground = Fore, Margin = new Thickness(0, 0, 0, 4) };
			var exportResult = new TextBlock { Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Width = 320 };
			var export = new Button { Content = L.T("Подготовить данные для проекта", "Prepare data for the project"), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
			export.Click += (o, e) =>
			{
				try
				{
					var r = DataExport.Export(chat.IsChecked == true);
					exportResult.Text = L.T($"Готово: {System.IO.Path.GetFileName(r.File)} — {r.Counts.Values.Sum()} записей ({r.Dates}). Папка открыта.",
						$"Done: {System.IO.Path.GetFileName(r.File)} — {r.Counts.Values.Sum()} records ({r.Dates}). Folder opened.");
					System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{r.File}\"");
				}
				catch(System.Exception ex)
				{
					exportResult.Text = L.T("Не получилось: ", "Failed: ") + ex.Message;
				}
			};
			stack.Children.Add(chat);
			stack.Children.Add(export);
			stack.Children.Add(exportResult);
			stack.Children.Add(save);
			Content = stack;
		}

		private static TextBlock Label(string text) => new TextBlock { Text = text, Foreground = Fore, FontSize = 12 };

		private static string ScaleText(int percent) => L.T($"Размер окон: {percent}%", $"Window size: {percent}%");
	}
}
