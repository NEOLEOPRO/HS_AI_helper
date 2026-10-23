using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hearthstone_Deck_Tracker.Utility.Extensions;
using Core = Hearthstone_Deck_Tracker.API.Core;

namespace HSaiHelper.Hdt
{
	/// <summary>
	/// Отдельное окно HSAI на оверлее HDT: заголовок (за него перетаскивается), ▾/▸ — свернуть, содержимое.
	/// Положение и свёрнутость — свои у каждого окна (Settings.Panels[key]). Масштаб — по ширине оверлея (база 1920).
	/// Кликабельны только заголовок и то, что окно само пометит (OverlayExtensions.SetIsOverlayHitTestVisible) — остальное не мешает игре.
	/// Положение по умолчанию задаётся долями ширины/высоты оверлея, чтобы окна не налезали друг на друга на любом разрешении.
	/// </summary>
	internal class OverlayPanel
	{
		public static readonly Brush Background = new SolidColorBrush(Color.FromArgb(215, 18, 18, 26));
		public static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(167, 139, 250));
		public static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(150, 146, 168));
		public static readonly FontFamily Font = new FontFamily("Segoe UI");

		public readonly StackPanel Content = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
		/// <summary>
		/// Прокрутка, если окно выше места на экране (раскладка задаёт MaxHeight). Мышь она ловит, только когда есть что прокручивать, —
		/// иначе окно, как и раньше, не мешает кликам в игре.
		/// </summary>
		private readonly ScrollViewer _scroll;
		private readonly Border _root;
		private readonly TextBlock _title;
		private readonly TextBlock _toggle;
		private readonly ScaleTransform _scale = new ScaleTransform(1, 1);
		private readonly string _key;
		private readonly Func<string> _titleText;
		private readonly double _defaultX, _defaultY;
		private bool _attached, _dragging, _positioned;
		private Point _dragStart;

		/// <param name="defaultX">Левый край по умолчанию: доля ширины оверлея (0..1) или пиксели, если больше 1.</param>
		/// <param name="defaultY">Верхний край по умолчанию: доля высоты оверлея (0..1) или пиксели, если больше 1.</param>
		public OverlayPanel(string key, Func<string> title, double defaultX, double defaultY, double maxWidth, Brush border = null)
		{
			_key = key;
			_titleText = title;
			_defaultX = defaultX;
			_defaultY = defaultY;
			_title = new TextBlock
			{
				Foreground = Accent, FontFamily = Font, FontSize = 13, FontWeight = FontWeights.SemiBold, Cursor = Cursors.SizeAll, Margin = new Thickness(0, 0, 12, 0),
			};
			_toggle = new TextBlock { Foreground = Dim, FontFamily = Font, FontSize = 13, Cursor = Cursors.Hand };
			var header = new DockPanel { LastChildFill = false };
			DockPanel.SetDock(_toggle, Dock.Right);
			header.Children.Add(_toggle);
			header.Children.Add(_title);
			_scroll = new ScrollViewer { Content = Content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
			_scroll.ScrollChanged += (s, e) => OverlayExtensions.SetIsOverlayHitTestVisible(_scroll, _scroll.ScrollableHeight > 0);
			var stack = new DockPanel { LastChildFill = true };
			DockPanel.SetDock(header, Dock.Top);
			stack.Children.Add(header);
			stack.Children.Add(_scroll);
			_root = new Border
			{
				Child = stack, Background = Background, BorderBrush = border ?? Accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
				Padding = new Thickness(10, 6, 10, 8), MinWidth = 200, MaxWidth = maxWidth, Visibility = Visibility.Collapsed, RenderTransform = _scale,
			};
			OverlayExtensions.SetIsOverlayHitTestVisible(_title, true);
			OverlayExtensions.SetIsOverlayHitTestVisible(_toggle, true);
			_root.SizeChanged += (s, e) => Resized?.Invoke();

			_toggle.MouseLeftButtonUp += (s, e) =>
			{
				State.Collapsed = !State.Collapsed;
				Settings.Instance.Save();
				Refresh();
			};
			_title.MouseLeftButtonDown += (s, e) => { _dragging = true; _dragStart = e.GetPosition(_root); _title.CaptureMouse(); };
			_title.MouseMove += (s, e) =>
			{
				if(!_dragging)
					return;
				var p = e.GetPosition(Core.OverlayCanvas);
				Canvas.SetLeft(_root, Math.Max(0, p.X - _dragStart.X * _scale.ScaleX));
				Canvas.SetTop(_root, Math.Max(0, p.Y - _dragStart.Y * _scale.ScaleY));
			};
			_title.MouseLeftButtonUp += (s, e) =>
			{
				if(!_dragging)
					return;
				_dragging = false;
				_title.ReleaseMouseCapture();
				State.Left = Canvas.GetLeft(_root);
				State.Top = Canvas.GetTop(_root);
				Settings.Instance.Save();
				Resized?.Invoke(); // окно вышло из столбика — соседи сдвигаются
			};
			Refresh();
		}

		private PanelState State => Settings.Instance.Panel(_key);

		/// <summary>Окно не перетаскивали — его место выбирает раскладка (HsaiOverlay.Layout); перетащенное помнит своё.</summary>
		public bool Docked => State.Left == null || State.Top == null;

		public bool IsShown => _root.Visibility == Visibility.Visible;

		/// <summary>Размер на экране (с учётом масштаба).</summary>
		public Size ScreenSize => new Size(_root.ActualWidth * _scale.ScaleX, _root.ActualHeight * _scale.ScaleY);

		/// <summary>Размер окна изменился (содержимое выросло/свернулось) — раскладке пора подвинуть соседей.</summary>
		public event Action Resized;

		/// <summary>Место от раскладки (для незакреплённых окон).</summary>
		public void PlaceDocked(double left, double top)
		{
			Canvas.SetLeft(_root, Math.Max(0, left));
			Canvas.SetTop(_root, Math.Max(0, top));
			_positioned = true;
		}

		/// <summary>Заголовок (язык мог смениться) и свёрнутость.</summary>
		public void Refresh()
		{
			_title.Text = _titleText();
			_toggle.Text = State.Collapsed ? "▸" : "▾";
			_scroll.Visibility = State.Collapsed ? Visibility.Collapsed : Visibility.Visible;
		}

		public bool Collapsed => State.Collapsed;

		/// <summary>Свернуть/развернуть (горячая клавиша «все окна»); сохраняется.</summary>
		public void SetCollapsed(bool collapsed)
		{
			State.Collapsed = collapsed;
			Refresh();
		}

		/// <summary>Предел высоты на экране (пиксели оверлея): ниже края экрана окно не уходит — содержимое прокручивается.</summary>
		public void SetMaxScreenHeight(double pixels)
		{
			// Окно ещё не размещено (Canvas.Top = NaN) или размер оверлея неизвестен — предела нет (1.2.0 падал здесь: «не число»).
			if(double.IsNaN(pixels) || double.IsInfinity(pixels))
			{
				_root.MaxHeight = double.PositiveInfinity;
				return;
			}
			var scale = Math.Max(0.1, _scale.ScaleY);
			_root.MaxHeight = Math.Max(80, pixels / scale);
		}

		public double Top => Canvas.GetTop(_root);

		public void SetVisible(bool visible)
		{
			if(visible && !_attached)
			{
				Core.OverlayCanvas.Children.Add(_root);
				_attached = true;
			}
			if(visible)
			{
				ApplyScale();
				if(!_positioned)
					ApplyPosition();
			}
			_root.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
		}

		/// <summary>Масштаб: автоподгонка по ширине оверлея (база 1920) × ползунок «Размер окон HSAI» из настроек (50–150 %).</summary>
		public void ApplyScale()
		{
			var auto = Math.Max(0.75, Math.Min(1.6, (Core.OverlayWindow?.Width ?? 1920) / 1920.0));
			var user = Math.Max(50, Math.Min(150, Settings.Instance.PanelScale)) / 100.0;
			_scale.ScaleX = _scale.ScaleY = auto * user;
		}

		/// <summary>Сохранённое положение или положение по умолчанию (доли размера оверлея).</summary>
		public void ApplyPosition()
		{
			var s = State;
			double width = Core.OverlayWindow?.Width ?? 1920, height = Core.OverlayWindow?.Height ?? 1080;
			Canvas.SetLeft(_root, s.Left ?? (_defaultX <= 1 ? _defaultX * width : _defaultX));
			Canvas.SetTop(_root, s.Top ?? (_defaultY <= 1 ? _defaultY * height : _defaultY));
			_positioned = true;
		}

		public void Detach()
		{
			if(_attached)
				Core.OverlayCanvas.Children.Remove(_root);
			_attached = false;
		}
	}

	/// <summary>Положение и свёрнутость одного окна (null — по умолчанию).</summary>
	internal class PanelState
	{
		public double? Left { get; set; }
		public double? Top { get; set; }
		public bool Collapsed { get; set; }
	}
}
