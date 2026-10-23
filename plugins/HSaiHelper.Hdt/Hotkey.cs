using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace HSaiHelper.Hdt
{
	/// <summary>
	/// Глобальная горячая клавиша «свернуть/развернуть все окна HSAI» (Settings.CollapseHotkey, по умолчанию Ctrl+Shift+H).
	/// RegisterHotKey Windows на своём невидимом окне сообщений: в игру ничего не посылаем и её не трогаем — клавишу просто получает Windows
	/// и отдаёт нам (игра это сочетание не получит). Занята другой программой — пишем в ленту, плагин работает дальше.
	/// </summary>
	internal static class Hotkey
	{
		private const int Id = 0x4853; // «HS»
		private const int WmHotkey = 0x0312;
		private const uint ModAlt = 1, ModCtrl = 2, ModShift = 4, ModWin = 8, ModNoRepeat = 0x4000;

		[DllImport("user32.dll", SetLastError = true)]
		private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

		[DllImport("user32.dll", SetLastError = true)]
		private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

		private static HwndSource _window;
		private static bool _registered;

		public static Action Pressed;

		/// <summary>Зарегистрировать сочетание из настроек (пусто — выключено). Повторный вызов — перерегистрация. Только из UI-потока.</summary>
		public static string Apply(string text)
		{
			Unregister();
			if(string.IsNullOrWhiteSpace(text))
				return null;
			if(!TryParse(text, out var modifiers, out var key))
				return L.T($"Горячая клавиша «{text}» не распознана", $"Hotkey \"{text}\" not recognized");
			if(_window == null)
			{
				// Окно только для сообщений (HWND_MESSAGE = −3): не видно, фокус не берёт.
				_window = new HwndSource(new HwndSourceParameters("HSaiHelper.Hotkey") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
				_window.AddHook(WndProc);
			}
			_registered = RegisterHotKey(_window.Handle, Id, modifiers | ModNoRepeat, (uint)KeyInterop.VirtualKeyFromKey(key));
			return _registered ? null : L.T($"Горячая клавиша {text} занята другой программой — выберите другую в настройках",
				$"Hotkey {text} is taken by another program — choose another one in the settings");
		}

		public static void Unregister()
		{
			if(_registered && _window != null)
				UnregisterHotKey(_window.Handle, Id);
			_registered = false;
		}

		private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
		{
			if(msg == WmHotkey && wParam.ToInt32() == Id)
			{
				handled = true;
				try
				{
					Pressed?.Invoke();
				}
				catch(Exception ex)
				{
					Paths.Log("Hotkey: " + ex.Message);
				}
			}
			return IntPtr.Zero;
		}

		/// <summary>«Ctrl+Shift+H» → модификаторы и клавиша. Нужен хотя бы один модификатор, кроме клавиш F1–F24.</summary>
		public static bool TryParse(string text, out uint modifiers, out Key key)
		{
			modifiers = 0;
			key = Key.None;
			foreach(var part in text.Split('+'))
			{
				var p = part.Trim();
				switch(p.ToLowerInvariant())
				{
					case "ctrl":
					case "control":
						modifiers |= ModCtrl;
						break;
					case "shift":
						modifiers |= ModShift;
						break;
					case "alt":
						modifiers |= ModAlt;
						break;
					case "win":
						modifiers |= ModWin;
						break;
					default:
						if(p.Length == 1 && char.IsDigit(p[0]))
							p = "D" + p;
						if(!Enum.TryParse(p, true, out key))
							return false;
						break;
				}
			}
			bool function = key >= Key.F1 && key <= Key.F24;
			return key != Key.None && (modifiers != 0 || function);
		}

		/// <summary>Текст сочетания из нажатия (поле в настройках): «Ctrl+Shift+H».</summary>
		public static string Describe(ModifierKeys modifiers, Key key)
		{
			var text = "";
			if(modifiers.HasFlag(ModifierKeys.Control))
				text += "Ctrl+";
			if(modifiers.HasFlag(ModifierKeys.Shift))
				text += "Shift+";
			if(modifiers.HasFlag(ModifierKeys.Alt))
				text += "Alt+";
			if(modifiers.HasFlag(ModifierKeys.Windows))
				text += "Win+";
			var name = key >= Key.D0 && key <= Key.D9 ? key.ToString().Substring(1) : key.ToString();
			return text + name;
		}
	}
}
