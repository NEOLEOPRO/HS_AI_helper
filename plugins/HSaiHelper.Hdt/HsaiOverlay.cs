using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hearthstone_Deck_Tracker.Utility.Extensions;
using Core = Hearthstone_Deck_Tracker.API.Core;

namespace HSaiHelper.Hdt
{
	/// <summary>
	/// Окна HSAI на оверлее HDT (каждое перетаскивается за заголовок, сворачивается, помнит своё место — OverlayPanel):
	///   «HSAI · Лобби» (слева) — игроки в порядке мест; клик по строке раскрывает динамику состава и сборки; занятость пула;
	///   «Следующий соперник» (справа сверху) — шансы до боя, прошлые бои, сборка; видно только когда соперник известен;
	///   «Оценки» (справа) — шанс места, риск вылета, сила стола, пул, разбор партии;
	///   «ИИ» (справа снизу) — лента «что делает ИИ» и строка вопроса к локальной модели (поле ввода — маленькое окно: оверлей не принимает клавиатуру).
	/// Кликабельны только заголовки, строки игроков и «спросить ИИ» — остальной оверлей прозрачен для мыши.
	/// Создаётся и меняется только в UI-потоке; события из фоновых потоков приходят через Dispatcher.
	/// </summary>
	internal class HsaiOverlay
	{
		private static HsaiOverlay _instance;

		public static HsaiOverlay Instance => _instance ??= new HsaiOverlay();

		private static readonly Brush TextMain = new SolidColorBrush(Color.FromRgb(232, 230, 240));
		private static readonly Brush TextDim = OverlayPanel.Dim;
		private static readonly Brush Accent = OverlayPanel.Accent;
		private static readonly Brush Warning = new SolidColorBrush(Color.FromRgb(251, 191, 36));
		private static readonly Brush Error = new SolidColorBrush(Color.FromRgb(248, 113, 113));
		private static readonly Brush CommentBrush = new SolidColorBrush(Color.FromRgb(125, 211, 252));
		private static readonly Brush Gold = new SolidColorBrush(Color.FromRgb(250, 204, 21));
		private static readonly Brush MeBackground = new SolidColorBrush(Color.FromArgb(40, 250, 204, 21));
		private static readonly Brush NextOpponent = new SolidColorBrush(Color.FromRgb(251, 113, 133));
		private static readonly Brush NextBackground = new SolidColorBrush(Color.FromArgb(48, 244, 63, 94));
		private static readonly Brush OddsBackground = new SolidColorBrush(Color.FromArgb(40, 167, 139, 250));
		private static readonly FontFamily Font = OverlayPanel.Font;
		private const int FeedLines = 6;
		private const int ChatLines = 4;

		private readonly OverlayPanel _lobby;
		private readonly OverlayPanel _next;
		private readonly OverlayPanel _estimates;
		private readonly OverlayPanel _ai;
		private readonly Section _activity;
		private readonly Section _chat;
		private readonly Section _pool;
		private readonly Section _review;
		private readonly Section _help;
		private readonly StackPanel _estimatesMain = new StackPanel();
		private readonly TextBlock _aiToggle;
		private readonly TextBlock _thinkToggle;
		private readonly StackPanel _feed = new StackPanel();
		private readonly StackPanel _chatList = new StackPanel();
		private readonly TextBlock _ask;
		private readonly HashSet<int> _expanded = new HashSet<int>();
		private LobbyView _lastView;
		private string _lobbySignature, _nextSignature, _estimatesSignature;
		private bool _visible, _hasNext;
		private AskWindow _askWindow;

		private HsaiOverlay()
		{
			var settings = Settings.Instance;
			// «Лобби» — правее таблицы лидеров самой игры (поверх неё мешало наводить на портреты).
			// Ширины — чтобы строки помещались целиком (2026-10-23: в 520 не влезали столбцы лобби); длиннее — перенос, выше экрана — прокрутка.
			_lobby = new OverlayPanel("lobby", () => $"HSAI {Paths.VersionText} · " + L.T("Лобби", "Lobby"), 0.17, 0.105, 600);
			_next = new OverlayPanel("next", () => L.T("⚔ Следующий соперник", "⚔ Next opponent"), 0.775, 0.09, 440, NextOpponent);
			_estimates = new OverlayPanel("estimates", () => L.T("Оценки", "Estimates"), 0.775, 0.36, 440);
			_ai = new OverlayPanel("ai", () => L.T("ИИ", "AI"), 0.775, 0.60, 440);

			_activity = new Section(() => L.T("Что делает ИИ", "What the AI is doing"), () => settings.ActivityCollapsed, v => { settings.ActivityCollapsed = v; settings.Save(); });
			_chat = new Section(() => L.T("Спросить ИИ", "Ask the AI"), () => settings.CommentsCollapsed, v => { settings.CommentsCollapsed = v; settings.Save(); });
			_activity.Content.Children.Add(_feed);
			_pool = new Section(() => L.T("Сборка и пул ≈", "Build & pool ≈"), () => settings.PoolCollapsed, v => { settings.PoolCollapsed = v; settings.Save(); });
			_review = new Section(() => L.T("Разбор партии", "Game review"), () => settings.ReviewCollapsed, v => { settings.ReviewCollapsed = v; settings.Save(); });
			_estimates.Content.Children.Add(_estimatesMain);
			_estimates.Content.Children.Add(_pool.Root);
			_estimates.Content.Children.Add(_review.Root);
			// «Как читать» — по строке на каждую цифру: что значит, как пользоваться, насколько верить (по нашим проверкам).
			_help = new Section(() => L.T("Как читать", "How to read"), () => settings.HelpCollapsed, v => { settings.HelpCollapsed = v; settings.Save(); });
			_estimates.Content.Children.Add(_help.Root);
			OverlayExtensions.SetIsOverlayHitTestVisible(_pool.Header, true);
			OverlayExtensions.SetIsOverlayHitTestVisible(_review.Header, true);
			OverlayExtensions.SetIsOverlayHitTestVisible(_help.Header, true);
			FillHelp();
			_ask = new TextBlock { Foreground = Accent, FontFamily = Font, FontSize = 12, Cursor = Cursors.IBeam, Margin = new Thickness(0, 2, 0, 0) };
			_ask.MouseLeftButtonUp += (s, e) => OpenAskWindow();
			_chat.Content.Children.Add(_chatList);
			_chat.Content.Children.Add(_ask);
			// Тумблер ИИ: включить/выключить локальную модель (выключение освобождает видеокарту), заодно — её состояние.
			_aiToggle = new TextBlock { FontFamily = Font, FontSize = 12, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 3), TextWrapping = TextWrapping.Wrap };
			_aiToggle.MouseLeftButtonUp += (s, e) =>
			{
				bool off = !settings.AiAutoStart || AiServer.State == AiServerState.Failed || AiServer.State == AiServerState.NotInstalled;
				AiServer.SetEnabled(off);
			};
			OverlayExtensions.SetIsOverlayHitTestVisible(_aiToggle, true);
			AiServer.Changed += () => _aiToggle.Dispatcher.BeginInvoke(new Action(RefreshToggle));
			RefreshToggle();
			_ai.Content.Children.Add(_aiToggle);
			// Тумблер «ход мыслей»: модель рассуждает перед ответом, рассуждение видно под ответом (дольше).
			_thinkToggle = new TextBlock { FontFamily = Font, FontSize = 11, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 3), Foreground = TextDim };
			_thinkToggle.MouseLeftButtonUp += (s, e) =>
			{
				settings.AiShowReasoning = !settings.AiShowReasoning;
				settings.Save();
				RefreshThinkToggle();
			};
			OverlayExtensions.SetIsOverlayHitTestVisible(_thinkToggle, true);
			RefreshThinkToggle();
			_ai.Content.Children.Add(_thinkToggle);
			_ai.Content.Children.Add(_chat.Root);
			_ai.Content.Children.Add(_activity.Root);
			OverlayExtensions.SetIsOverlayHitTestVisible(_activity.Header, true);
			OverlayExtensions.SetIsOverlayHitTestVisible(_chat.Header, true);
			OverlayExtensions.SetIsOverlayHitTestVisible(_ask, true);

			foreach(var panel in All)
				panel.Resized += () => _ask.Dispatcher.BeginInvoke(new Action(Layout));
			ActivityFeed.Changed += () => _ask.Dispatcher.BeginInvoke(new Action(RefreshFeed));
			AiChat.Changed += () => _ask.Dispatcher.BeginInvoke(new Action(RefreshChat));
			RefreshFeed();
			RefreshChat();
		}

		private IEnumerable<OverlayPanel> All => new[] { _lobby, _next, _estimates, _ai };

		/// <summary>Показать/скрыть все окна (по состоянию игры). «Следующий соперник» — только когда он известен.</summary>
		public void SetVisible(bool visible)
		{
			_visible = visible;
			_lobby.SetVisible(visible);
			_estimates.SetVisible(visible);
			_ai.SetVisible(visible);
			_next.SetVisible(visible && _hasNext);
			Layout();
		}

		/// <summary>
		/// Раскладка окон, которые не перетаскивали: «Лобби» — правее таблицы лидеров игры; «Следующий соперник», «Оценки», «ИИ» —
		/// столбиком у правого края, друг под другом без наложений (сдвигаются, когда соседнее окно выросло или свернулось).
		/// Перетащенное окно помнит своё место и в столбике не участвует.
		/// </summary>
		/// <summary>Раскладка вызывается часто (смена размера окон) — ошибка в ней не должна всплывать в HDT красным окном.</summary>
		private void Layout()
		{
			try
			{
				LayoutCore();
			}
			catch(Exception ex)
			{
				Paths.Log("Overlay.Layout: " + ex.Message);
			}
		}

		private void LayoutCore()
		{
			double width = Core.OverlayWindow?.Width ?? 1920, height = Core.OverlayWindow?.Height ?? 1080;
			if(_lobby.Docked)
				_lobby.PlaceDocked(0.17 * width, 0.105 * height);
			double top = 0.09 * height, gap = 0.006 * height, bottom = 0.985 * height;
			_lobby.SetMaxScreenHeight(bottom - _lobby.Top);
			var column = new[] { _next, _estimates, _ai }.Where(p => p.Docked && p.IsShown).ToList();
			for(int i = 0; i < column.Count; i++)
			{
				var panel = column[i];
				// Не ниже края экрана: окну — место до низа минус заголовки оставшихся окон (≈ 30 пикселей каждое).
				panel.SetMaxScreenHeight(bottom - top - (column.Count - i - 1) * 0.03 * height);
				var size = panel.ScreenSize;
				panel.PlaceDocked(width - size.Width - 0.008 * width, top);
				top += size.Height + gap;
			}
			foreach(var panel in All.Where(p => !p.Docked && p != _lobby))
				panel.SetMaxScreenHeight(bottom - panel.Top);
		}

		private Dictionary<OverlayPanel, bool> _beforeCollapseAll;

		/// <summary>
		/// Горячая клавиша (Settings.CollapseHotkey): свернуть все окна до заголовков; повторно — вернуть, как было до этого.
		/// </summary>
		public void ToggleCollapseAll()
		{
			if(_beforeCollapseAll == null || All.Any(p => !p.Collapsed))
			{
				_beforeCollapseAll = All.ToDictionary(p => p, p => p.Collapsed);
				foreach(var p in All)
					p.SetCollapsed(true);
			}
			else
			{
				foreach(var p in All)
					p.SetCollapsed(_beforeCollapseAll.TryGetValue(p, out var c) && c);
				if(All.All(p => p.Collapsed))
					foreach(var p in All)
						p.SetCollapsed(false);
				_beforeCollapseAll = null;
			}
			Settings.Instance.Save();
			Layout();
		}

		/// <summary>Ползунок «Размер окон HSAI» в настройках — сразу, без перезапуска.</summary>
		public void RefreshScale()
		{
			foreach(var p in All)
				p.ApplyScale();
			Layout();
		}

		/// <summary>«Вернуть окна на места» (настройки).</summary>
		public void ResetPositions()
		{
			Settings.Instance.ResetPanels();
			foreach(var p in All)
			{
				p.ApplyPosition();
				p.Refresh();
			}
			Layout();
		}

		public void ApplyPosition()
		{
			foreach(var p in All)
				p.ApplyPosition();
		}

		/// <summary>
		/// «Лобби»: шапка (MMR, типы), таблица игроков в порядке мест (как в игре), занятость пула.
		/// «Следующий соперник»: его строка, шансы до боя, прошлые бои, сборка. Вызывается раз в секунду — перерисовка только при изменениях.
		/// </summary>
		public void SetLobby(LobbyView view)
		{
			_lastView = view;
			var signature = string.Join("\n", view.Info) + "\n" + string.Join("\n", view.Rows.Select(r =>
				$"{r.PlayerId}|{r.Place}|{r.Hero}|{r.Tier}|{r.Hp}|{r.Comp}|{r.Streak}|{r.Top4}|{r.Mmr}|{r.Style}|{(_expanded.Contains(r.PlayerId) ? string.Join("/", r.Details) : "")}"))
				+ "\n" + view.Pool + "\n" + L.En;
			if(signature != _lobbySignature)
			{
				_lobbySignature = signature;
				foreach(var p in All)
					p.Refresh();
				_activity.Refresh();
				_chat.Refresh();
				var content = _lobby.Content.Children;
				content.Clear();
				foreach(var line in view.Info)
					content.Add(Text(line, TextDim));
				if(view.Rows.Count > 0)
				{
					// Каждая строка — своя сетка (её можно кликнуть), ширины колонок общие (SharedSizeGroup) — таблица ровная.
					var table = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
					Grid.SetIsSharedSizeScope(table, true);
					foreach(var row in view.Rows)
						table.Children.Add(BuildRow(row));
					content.Add(table);
				}
				if(view.Pool != null)
				{
					var pool = Text(view.Pool, TextDim);
					pool.TextWrapping = TextWrapping.Wrap;
					content.Add(pool);
				}
			}
			SetNext(view);
		}

		private void SetNext(LobbyView view)
		{
			var signature = view.Odds + view.OddsNote + "\n" + string.Join("\n", view.Next);
			var hadNext = _hasNext;
			_hasNext = view.Next.Count > 0;
			_next.SetVisible(_visible && _hasNext);
			if(hadNext != _hasNext)
				Layout();
			if(signature == _nextSignature)
				return;
			_nextSignature = signature;
			var content = _next.Content.Children;
			content.Clear();
			for(int i = 0; i < view.Next.Count; i++)
			{
				// Первая строка «⚔ Следующий: …» дублирует заголовок окна — оставляем только имя, тир и состав.
				var line = i == 0 ? view.Next[0].Substring(view.Next[0].IndexOf(':') + 1).Trim() : view.Next[i].TrimStart();
				var t = Text(line, i == 0 ? NextOpponent : view.Next[i].StartsWith("    ") ? TextDim : TextMain);
				t.TextWrapping = TextWrapping.Wrap;
				if(i == 0)
				{
					t.FontWeight = FontWeights.SemiBold;
					t.FontSize = 13;
				}
				else if(view.Next[i].StartsWith("    "))
					t.Margin = new Thickness(10, 0, 0, 0);
				content.Add(t);
				// Шансы до боя — сразу под именем соперника, в рамке.
				if(i == 0 && view.Odds != null)
				{
					var odds = new StackPanel();
					odds.Children.Add(new TextBlock { Text = view.Odds, Foreground = TextMain, FontFamily = Font, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
					if(view.OddsNote != null)
						odds.Children.Add(new TextBlock { Text = view.OddsNote.Trim(), Foreground = TextDim, FontFamily = Font, FontSize = 11, TextWrapping = TextWrapping.Wrap });
					content.Add(new Border { Child = odds, Background = OddsBackground, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 3, 6, 4), Margin = new Thickness(0, 3, 0, 3) });
				}
			}
		}

		private static readonly string[] Columns = { "place", "hero", "tier", "hp", "comp", "streak", "top4", "mmr" };

		private FrameworkElement BuildRow(LobbyRow row)
		{
			var grid = new Grid();
			foreach(var group in Columns)
				grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = group });
			grid.ColumnDefinitions[1].MaxWidth = 160;

			var brush = row.Style == RowStyle.Me ? Gold : row.Style == RowStyle.Next ? NextOpponent : row.Style == RowStyle.Dead ? TextDim : TextMain;
			var cells = new[] { row.Place, row.Hero, row.Tier, row.Hp, row.Comp, row.Streak, row.Top4, row.Mmr };
			for(int c = 0; c < cells.Length; c++)
			{
				if(string.IsNullOrEmpty(cells[c]))
					continue;
				var cell = Text(cells[c], (c == 2 || c == 5 || c == 6 || c == 7) && row.Style == RowStyle.Normal ? TextDim : brush);
				cell.Margin = new Thickness(c == 0 ? 3 : 0, 1, 9, 1);
				if(row.Style == RowStyle.Me || row.Style == RowStyle.Next)
					cell.FontWeight = FontWeights.SemiBold;
				Grid.SetColumn(cell, c);
				grid.Children.Add(cell);
			}

			bool expanded = _expanded.Contains(row.PlayerId);
			var line = new DockPanel();
			if(row.PlayerId > 0)
			{
				// Раскрыть/свернуть подробности (динамика, сборки) — только по значку: остальная строка не перехватывает мышь у игры.
				var expander = new TextBlock
				{
					Text = expanded ? "▾" : "▸", Foreground = Accent, FontFamily = Font, FontSize = 12, Cursor = Cursors.Hand,
					Margin = new Thickness(2, 1, 4, 1), Width = 12,
				};
				expander.MouseLeftButtonUp += (s, e) =>
				{
					if(!_expanded.Remove(row.PlayerId))
						_expanded.Add(row.PlayerId);
					_lobbySignature = null;
					if(_lastView != null)
						SetLobby(_lastView);
				};
				OverlayExtensions.SetIsOverlayHitTestVisible(expander, true);
				line.Children.Add(expander);
			}
			else
				line.Children.Add(new TextBlock { Width = 18 });
			line.Children.Add(grid);
			var container = new StackPanel();
			container.Children.Add(line);
			if(expanded)
				foreach(var detail in row.Details)
					container.Children.Add(new TextBlock
					{
						Text = detail, Foreground = TextDim, FontFamily = Font, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(34, 0, 6, 1),
					});

			var normal = row.Style == RowStyle.Next ? NextBackground : row.Style == RowStyle.Me ? MeBackground : Brushes.Transparent;
			return new Border
			{
				Child = container, Background = normal, CornerRadius = new CornerRadius(3),
				Opacity = row.Style == RowStyle.Dead ? 0.6 : 1, Margin = new Thickness(0, 0, 0, expanded ? 3 : 0),
			};
		}

		/// <summary>«Оценки»: полоса «шанс топ-4» + строки (риск вылета, сила стола, пул, разбор партии).</summary>
		public void SetEstimates(EstimatesView view)
		{
			var signature = $"{view.Top4:F2}|" + string.Join("\n", view.Lines) + "|" + string.Join("\n", view.Pool) + "|" + string.Join("\n", view.Review);
			if(signature == _estimatesSignature)
				return;
			_estimatesSignature = signature;
			_pool.Refresh();
			_review.Refresh();
			_help.Refresh();
			FillHelp();
			FillLines(_pool.Content.Children, view.Pool);
			FillLines(_review.Content.Children, view.Review);
			_pool.Root.Visibility = view.Pool.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
			_review.Root.Visibility = view.Review.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
			var content = _estimatesMain.Children;
			content.Clear();
			if(view.Top4.HasValue)
			{
				// Полоса оценки, как в шахматах: доля топ-4, цвет от красного к зелёному.
				double v = Math.Max(0, Math.Min(1, view.Top4.Value));
				var color = Color.FromRgb((byte)(244 - 170 * v), (byte)(63 + 134 * v), 94);
				var bar = new Grid { Height = 8, Margin = new Thickness(0, 2, 6, 4) };
				bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, v), GridUnitType.Star) });
				bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, 1 - v), GridUnitType.Star) });
				var fill = new Border { Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(3, 0, 0, 3) };
				var rest = new Border { Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), CornerRadius = new CornerRadius(0, 3, 3, 0) };
				Grid.SetColumn(rest, 1);
				bar.Children.Add(fill);
				bar.Children.Add(rest);
				content.Add(bar);
			}
			if(view.Lines.Count == 0 && view.Pool.Count == 0 && view.Review.Count == 0)
				content.Add(Text(L.T("появятся после первых боёв", "will appear after the first fights"), TextDim));
			for(int i = 0; i < view.Lines.Count; i++)
			{
				var line = view.Lines[i];
				var t = Text(line, line.StartsWith("  ") ? TextDim : TextMain);
				t.TextWrapping = TextWrapping.Wrap;
				if(i == 0 && view.Top4.HasValue)
				{
					t.FontWeight = FontWeights.SemiBold;
					t.FontSize = 13;
				}
				content.Add(t);
			}
		}

		/// <summary>Справка к «Оценкам» и окну соперника — на языке интерфейса.</summary>
		private void FillHelp()
		{
			FillLines(_help.Content.Children, new List<string>
			{
				L.T("Шанс места — вероятность закончить в топ-4 / первым (полоса — топ-4). Проверено на 7 партиях: точнее, чем «всем поровну».",
					"Placement — chance to finish top 4 / first (the bar is top 4). Checked on 7 games: better than \"everyone equal\"."),
				L.T("Риск вылета — шанс вылететь в ближайшем бою / за 2 / за 3 боя. Высокий — пора усиливать стол; низкий — можно жадничать (повышение, экономика). Проверено: при риске ≥ 30 % вылетали в 35–46 % случаев.",
					"Elimination risk — chance to die in the next fight / within 2 / within 3. High — strengthen the board; low — you can be greedy (level, economy). Checked: at ≥ 30% players died in 35–46% of cases."),
				L.T("Твой стол против столов хода — BobsBuddy прогоняет его против 12 записанных столов этого хода (от слабых до сильных): перерождения, щиты, хрипы учтены.",
					"Your board vs this turn's boards — BobsBuddy runs it against 12 recorded boards of this turn (weak to strong): reborn, shields, deathrattles included."),
				L.T("Подозрительность (у соперника, после боя с ним) — во сколько раз сильнее записанных столов этого хода по боям BobsBuddy: 1,0 — порог «необычно сильный» (≈ 1 из 100 при честной игре). Не обвинение.",
					"Suspicion (opponent, after the fight with them) — how much stronger than recorded boards of this turn, by BobsBuddy fights: 1.0 — the \"unusually strong\" threshold (≈ 1 in 100 for fair play). Not an accusation."),
				L.T("Сборка и пул ≈ — какие сборки из гайдов HDT проще всего добрать (≈ золота: покупки, роллы по пулу, повышения); ★ — нужная карта сейчас в таверне; "
					+ "⚠ — когда стоит сменить сборку и хватит ли золота сделать это за ход; «до триплета» — для пар.",
					"Build & pool ≈ — which HDT guide builds are easiest to finish (≈ gold: buys, rolls by pool, upgrades); ★ — a needed card is in the tavern now; "
					+ "⚠ — when to switch builds and whether there is enough gold for it in one turn; \"for a triplet\" — for pairs."),
				L.T($"Свернуть/развернуть все окна — {(string.IsNullOrEmpty(Settings.Instance.CollapseHotkey) ? "горячая клавиша выключена" : Settings.Instance.CollapseHotkey)} (меняется в «Настройках»). Длинное окно прокручивается колесом мыши.",
					$"Collapse/expand all windows — {(string.IsNullOrEmpty(Settings.Instance.CollapseHotkey) ? "hotkey off" : Settings.Instance.CollapseHotkey)} (change it in Settings). A long window scrolls with the mouse wheel."),
				L.T("Тёмный дар — тир раскопки сейчас (растёт по ходам); брать лучше на 3–5 ходу.",
					"Dark Gift — the discover tier now (grows over turns); best taken on turns 3–5."),
				L.T("Ход 1–3 — подсказки по симуляции BobsBuddy, а не по чужим решениям.", "Turns 1–3 — tips from BobsBuddy simulation, not from other players' choices."),
				L.T("Окно соперника: шансы до боя — твой стол против 8 похожих записанных столов того же тира (его стол ещё не виден) — грубо; точный прогноз даёт HDT уже в бою. «Через ход» — по правилам подбора пар (проверено 502/502).",
					"Opponent window: pre-combat odds — your board vs 8 similar recorded boards of the same tier (theirs is hidden yet) — rough; HDT gives the exact odds in combat. \"Turn after\" — by the pairing rules (checked 502/502)."),
				L.T("Разбор после партии — где сильнее всего упал шанс топ-4 и самый неудачный/удачный бой.", "Post-game review — where the top-4 chance dropped most and the unluckiest/luckiest fight."),
			});
		}

		private static void FillLines(UIElementCollection target, List<string> lines)
		{
			target.Clear();
			foreach(var line in lines)
			{
				// «★» — карта сборки прямо сейчас в таверне (подсветка), «⚠» — стоит подумать о смене сборки.
				var trimmed = line.TrimStart();
				var t = Text(trimmed, trimmed.StartsWith("★") ? Gold : trimmed.StartsWith("⚠") ? Warning : line.StartsWith("  ") ? TextDim : TextMain);
				t.TextWrapping = TextWrapping.Wrap;
				if(line.StartsWith("  "))
					t.Margin = new Thickness(8, 0, 0, 0);
				target.Add(t);
			}
		}

		private static TextBlock Text(string text, Brush brush) => new TextBlock
		{
			Text = text, Foreground = brush, FontFamily = Font, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
		};

		public void Detach()
		{
			foreach(var p in All)
				p.Detach();
			_askWindow?.Close();
		}

		private void RefreshFeed()
		{
			_feed.Children.Clear();
			var items = ActivityFeed.Latest(FeedLines);
			if(items.Count == 0)
			{
				_feed.Children.Add(new TextBlock { Text = L.T("ждёт начала партии…", "waiting for a game…"), Foreground = TextDim, FontFamily = Font, FontSize = 12 });
				return;
			}
			for(int i = 0; i < items.Count; i++)
			{
				var it = items[i];
				var brush = it.Level == ActivityLevel.Error ? Error : it.Level == ActivityLevel.Warning ? Warning : it.Level == ActivityLevel.Comment ? CommentBrush : TextMain;
				_feed.Children.Add(new TextBlock
				{
					Text = $"{it.Time:HH:mm:ss}  {it.Text}{(it.Repeats > 1 ? $"  ×{it.Repeats}" : "")}",
					Foreground = brush, FontFamily = Font, FontSize = 12, TextWrapping = TextWrapping.Wrap,
					Opacity = Math.Max(0.45, 1.0 - i * 0.12), // старые события бледнее — видно, что свежее
				});
			}
		}

		private void RefreshThinkToggle()
		{
			_thinkToggle.Text = Settings.Instance.AiShowReasoning
				? L.T("☑ показывать ход мыслей (ответ дольше: +10–30 с)", "☑ show reasoning (slower: +10–30 s)")
				: L.T("☐ показывать ход мыслей", "☐ show reasoning");
		}

		private void RefreshToggle()
		{
			_aiToggle.Text = AiServer.StatusText();
			_aiToggle.Foreground = !Settings.Instance.AiAutoStart ? TextDim
				: AiServer.State == AiServerState.Ready ? new SolidColorBrush(Color.FromRgb(74, 222, 128))
				: AiServer.State == AiServerState.Failed || AiServer.State == AiServerState.NotInstalled ? Warning : Accent;
		}

		private void RefreshChat()
		{
			_chatList.Children.Clear();
			foreach(var c in AiChat.Latest(ChatLines))
			{
				var who = c.Role == ChatRole.Me ? L.T("Ты: ", "You: ") : c.Role == ChatRole.Ai ? L.T("ИИ: ", "AI: ") : c.Role == ChatRole.Note ? "✎ " : "";
				_chatList.Children.Add(new TextBlock
				{
					Text = $"{c.Time:HH:mm}  {who}{c.Text}",
					Foreground = c.Role == ChatRole.Ai ? TextMain : c.Role == ChatRole.System ? Warning : CommentBrush,
					FontFamily = Font, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2),
				});
				if(!string.IsNullOrEmpty(c.Reasoning))
				{
					// Ход мыслей — бледнее и короче (полный — в записи партии, ai_exchange).
					var r = c.Reasoning.Replace("\n", " ");
					_chatList.Children.Add(new TextBlock
					{
						Text = L.T("ход мыслей: ", "reasoning: ") + (r.Length > 600 ? r.Substring(0, 600) + "…" : r),
						Foreground = TextDim, FontFamily = Font, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 0, 3),
					});
				}
			}
			_ask.Text = AiChat.Busy ? L.T("… ИИ думает", "… the AI is thinking") : L.T("› спросить ИИ…", "› ask the AI…");
		}

		/// <summary>Поле ввода — маленькое окно рядом с «спросить ИИ»: оверлей HDT не получает фокус клавиатуры.</summary>
		private void OpenAskWindow()
		{
			if(_askWindow != null)
			{
				_askWindow.Activate();
				return;
			}
			var at = _ask.PointToScreen(new Point(0, _ask.ActualHeight + 4));
			_askWindow = new AskWindow(at);
			_askWindow.Closed += (s, e) => _askWindow = null;
			_askWindow.Show();
		}

		/// <summary>Сворачиваемый раздел внутри окна: заголовок «▾ Название» + содержимое.</summary>
		private class Section
		{
			public readonly StackPanel Root = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
			public readonly TextBlock Header;
			public readonly StackPanel Content = new StackPanel { Margin = new Thickness(10, 2, 0, 0) };
			private readonly Func<string> _name;
			private readonly Func<bool> _get;

			public Section(Func<string> name, Func<bool> get, Action<bool> set)
			{
				_name = name;
				_get = get;
				Header = new TextBlock { Foreground = Accent, FontFamily = Font, FontSize = 12, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand };
				Header.MouseLeftButtonUp += (s, e) => { set(!_get()); Refresh(); };
				Root.Children.Add(Header);
				Root.Children.Add(Content);
				Refresh();
			}

			public void Refresh()
			{
				var collapsed = _get();
				Header.Text = $"{(collapsed ? "▸" : "▾")} {_name()}";
				Content.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
			}
		}
	}
}
