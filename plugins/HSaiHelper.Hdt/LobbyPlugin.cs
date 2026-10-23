using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using Hearthstone_Deck_Tracker.Enums.Hearthstone;
using Hearthstone_Deck_Tracker.Plugins;
using HearthDb.Enums;
using HearthMirror;
using Newtonsoft.Json.Linq;
using Core = Hearthstone_Deck_Tracker.API.Core;

namespace HSaiHelper.Hdt
{
	/// <summary>
	/// HSAI Lobby: раздел «Лобби» на панели HSAI — таблица игроков в порядке мест (как в игре), по клику — динамика состава
	/// и предполагаемые сборки по гайдам; занятость пула; блок о следующем сопернике с «шансами до боя».
	/// Данные: таблица лидеров игры (читает Recorder), HearthMirror (лобби, типы), сущности HDT (мой стол, следующий соперник),
	/// официальные таблицы лидеров Blizzard (MMR топ-игроков). После партии панель остаётся с итогом до начала следующей.
	/// </summary>
	public class LobbyPlugin : IPlugin
	{
		public string Name => "HSAI Lobby";
		public string Description => L.T(
			$"Версия {Paths.VersionText}. Панель HSAI в Battlegrounds: игроки в порядке мест, состав и его динамика, предполагаемые сборки, шансы до боя, лента «Что делает ИИ», комментарии. Разделы сворачиваются, панель перетаскивается за заголовок.",
			$"Version {Paths.VersionText}. HSAI panel for Battlegrounds: players by place, compositions and their history, likely builds, pre-combat odds, the \"What the AI is doing\" feed, comments. Sections collapse; drag the panel by its title.");
		public string ButtonText => L.T("Настройки", "Settings");
		public string Author => "HSaiHelper";
		public Version Version => Paths.Version;
		public MenuItem MenuItem => null;

		private readonly LeaderboardService _leaderboards = new LeaderboardService();
		private bool _enabled;
		private DateTime _lastCheck = DateTime.MinValue;
		private bool _lobbyRequested;
		private string _tribes = "";
		private List<Race> _lobbyRaces = new List<Race>();
		private List<HearthMirror.Objects.BattlegroundsLobbyPlayer> _players;
		private Dictionary<string, int> _ratings = new Dictionary<string, int>();
		private string _region;
		private bool _duos;
		private bool _ratingsLoading;

		private bool _wasInGame;
		private bool _showLastGame;
		private int _me;
		private int _turn;
		private int _nextId;
		private DateTime? _combatStart;
		private ShopCards _shop = new ShopCards();
		private string _precombatSignature;
		private DateTime _lastPrecombat = DateTime.MinValue;

		public void OnLoad()
		{
			_enabled = true;
			Paths.Log($"Lobby: загружен, версия {Paths.VersionText}; шансы до боя: {(PrecombatEstimator.Supported ? "доступны" : "НЕДОСТУПНЫ — нет SetupInputPlayer в HDT")}");
			BoardLibrary.EnsureLoaded();
			// Горячая клавиша «свернуть/развернуть все окна HSAI» (настройки).
			Hotkey.Pressed = () => HsaiOverlay.Instance.ToggleCollapseAll();
			var hotkeyError = Hotkey.Apply(Settings.Instance.CollapseHotkey);
			if(hotkeyError != null)
				ActivityFeed.Post(hotkeyError, ActivityLevel.Warning);
			// Сводка для ИИ собирается в момент вопроса и всегда по-английски (короче в токенах, модели понимают лучше).
			AiChat.ContextProvider = () => L.InEnglish(() =>
			{
				bool inGame = Core.Game.IsBattlegroundsMatch && !Core.Game.IsInMenu;
				if(!inGame && !_showLastGame)
					return null;
				var view = BuildView(gameOver: !inGame);
				var estimates = BuildEstimates(gameOver: !inGame);
				ApplyTop4(view, estimates);
				// Сводка короткая (2026-10-23: 3800 токенов → 19 с только на чтение в щадящем режиме): без пояснений, без повторов
				// (тёмный дар уже в BoardNow), разбор партии — только после неё.
				var lines = estimates.Lines.Concat(estimates.Pool).Concat(inGame ? Enumerable.Empty<string>() : estimates.Review)
					.Where(l => !l.StartsWith("  heuristic") && !l.StartsWith("Dark Gift")).Select(l => l.Trim());
				return ContextText(view, gameOver: !inGame) + string.Join("\n", lines);
			});
		}

		public void OnUnload()
		{
			_enabled = false;
			HsaiOverlay.Instance.Detach();
			AiServer.Stop();
			Hotkey.Unregister();
		}

		public void OnButtonPress() => new SettingsWindow().Show();

		public void OnUpdate()
		{
			if(!_enabled || (DateTime.Now - _lastCheck).TotalMilliseconds < 1000)
				return;
			_lastCheck = DateTime.Now;
			try
			{
				var inGame = Core.Game.IsBattlegroundsMatch && !Core.Game.IsInMenu;
				// Экран Battlegrounds в меню (очередь, выбор режима) — панель уже видна: MMR, итог прошлой партии, ИИ-помощник.
				var bgMenu = Core.Game.CurrentMode == Mode.BACON;
				if(inGame && !_wasInGame)
					NewGame();
				_wasInGame = inGame;
				Suspicion.InGame = inGame;
				if(inGame)
					_showLastGame = true;
				else if(!Core.Game.IsInMenu && !Core.Game.IsBattlegroundsMatch)
					_showLastGame = false; // началась партия в другом режиме — итог BG больше не нужен
				HsaiOverlay.Instance.SetVisible(inGame || bgMenu || _showLastGame);
				if(inGame || bgMenu)
					AiServer.EnsureRunning(); // локальный ИИ подключается сам, если установлен
				if(!inGame)
				{
					if(bgMenu || _showLastGame)
						Suspicion.WarmUp(); // эталоны подозрительности — вне партии, чтобы в партии считать только стол соперника
					if(_showLastGame || bgMenu)
					{
						var view = BuildView(gameOver: _showLastGame);
						var summary = _showLastGame ? BuildEstimates(gameOver: true) : new EstimatesView();
						HsaiOverlay.Instance.SetLobby(view);
						HsaiOverlay.Instance.SetEstimates(summary);
					}
					return;
				}

				_me = Core.Game.Player.Id;
				_turn = Core.Game.GetTurnNumber();
				// Лобби известно после выбора героев.
				if(!_lobbyRequested && _turn >= 1)
				{
					_lobbyRequested = true;
					_region = ApiRegion(Core.Game.CurrentRegion.ToString());
					_duos = Core.Game.IsBattlegroundsDuosMatch;
					Task.Run(LoadLobby);
				}
				var next = NextOpponentId(_me);
				if(next > 0)
					_nextId = next;
				TrackCombat();
				MaybeStartPrecombat();
				MaybeStartField();
				MaybeRunPlacement();
				var estimates = BuildEstimates(gameOver: false);
				var current = BuildView(gameOver: false);
				ApplyTop4(current, estimates);
				HsaiOverlay.Instance.SetLobby(current);
				HsaiOverlay.Instance.SetEstimates(estimates);
			}
			catch(Exception ex)
			{
				Paths.Log("Lobby.OnUpdate: " + ex);
			}
		}

		private void NewGame()
		{
			LobbyState.Clear();
			PrecombatEstimator.Reset();
			_players = null;
			_tribes = "";
			_lobbyRaces = new List<Race>();
			_lobbyRequested = false;
			_nextId = 0;
			_combatStart = null;
			_precombatSignature = null;
			_placement = null;
			_placementSignature = null;
			GameReview.Reset();
			OpeningAdvisor.Reset();
			Suspicion.Reset();
			_shop = new ShopCards();
		}

		/// <summary>
		/// Мои карты на конец таверны. В бою стол меняется (гибнут, призываются жетоны) — гайды, «ключевые карты» и сила стола
		/// по нему прыгали бы; поэтому в бою берём то, что было перед боем.
		/// </summary>
		private class ShopCards
		{
			public List<string> Board = new List<string>();
			public List<string> BoardAndHand = new List<string>();
			/// <summary>Предложения таверны (в таверне это сущности «соперника» HDT; в бою там стол соперника — поэтому снимок).</summary>
			public List<string> Offers = new List<string>();
			public List<string> Trinkets = new List<string>();
			public int Gold, UpgradeCost;
			/// <summary>Подпись стола для пересчёта силы против поля: карты, статы, щиты, перерождения.</summary>
			public string Signature = "";
		}

		private ShopCards MyShopCards()
		{
			if(!Core.Game.IsBattlegroundsCombatPhase)
			{
				var board = Core.Game.Player.Board.Where(e => e.IsMinion && !string.IsNullOrEmpty(e.CardId)).ToList();
				var me = Core.Game.PlayerEntity;
				_shop = new ShopCards
				{
					Board = board.Select(e => e.CardId).ToList(),
					BoardAndHand = board.Concat(Core.Game.Player.Hand.Where(e => e.IsMinion && !string.IsNullOrEmpty(e.CardId))).Select(e => e.CardId).ToList(),
					Offers = Core.Game.Opponent.Board.Where(e => e.IsMinion && !string.IsNullOrEmpty(e.CardId)).Select(e => e.CardId).ToList(),
					Trinkets = Core.Game.Player.PlayerEntities.Where(e => e.GetTag(GameTag.CARDTYPE) == (int)CardType.BATTLEGROUND_TRINKET
						&& e.GetTag(GameTag.ZONE) == (int)Zone.PLAY && !string.IsNullOrEmpty(e.CardId)).Select(e => e.CardId).ToList(),
					Gold = me == null ? 0 : me.GetTag(GameTag.RESOURCES) - me.GetTag(GameTag.RESOURCES_USED) + me.GetTag(GameTag.TEMP_RESOURCES),
					UpgradeCost = Core.Game.Player.PlayerEntities.FirstOrDefault(e => e.CardId?.StartsWith("TB_BaconShopTechUp") == true)?.GetTag(GameTag.COST) ?? 0,
					Signature = string.Join(",", board.Select(e => $"{e.CardId}:{e.Attack}/{e.Health}:{e.GetTag(GameTag.DIVINE_SHIELD)}{e.GetTag(GameTag.REBORN)}")),
				};
			}
			return _shop;
		}

		// --- Оценки: шанс места, риск вылета, пул, сила стола, разбор ---

		private PlacementModel.Result _placement;
		private string _placementSignature;
		private System.Threading.Tasks.Task _placementTask;

		/// <summary>Розыгрыш остатка партии — в фоне, когда изменились HP/тиры/история или оценка шансов до боя.</summary>
		private void MaybeRunPlacement()
		{
			var tiles = LobbyState.Players?.OfType<JObject>().ToList();
			if(tiles == null || tiles.Count == 0 || _placementTask?.IsCompleted == false)
				return;
			var history = LobbyState.History?.OfType<JObject>().ToList() ?? new List<JObject>();
			var pre = PrecombatEstimator.Current;
			var usePre = pre != null && pre.Turn == _turn && pre.OpponentId == _nextId && !pre.Running && pre.Error == null ? pre : null;
			var signature = string.Join(",", tiles.Select(t => $"{t["player_id"]}:{t["health"]}-{t["damage"]}+{t["armor"]}/{t["tier"]}"))
							+ $"|{history.Count}|{_nextId}|{usePre?.Win:F2}";
			if(signature == _placementSignature)
				return;
			_placementSignature = signature;
			int me = _me, next = _nextId, cap = CurrentDamageCap();
			_placementTask = System.Threading.Tasks.Task.Run(() =>
			{
				try
				{
					_placement = PlacementModel.Run(tiles, history, me, next, usePre, cap);
				}
				catch(Exception ex)
				{
					Paths.Log("Placement: " + ex);
				}
			});
		}

		private static int CurrentDamageCap()
		{
			var game = Core.Game.GameEntity;
			if(game == null || !Enum.TryParse("BACON_COMBAT_DAMAGE_CAP_ENABLED", out GameTag enabled) || !Enum.TryParse("BACON_COMBAT_DAMAGE_CAP", out GameTag cap))
				return 0;
			return game.GetTag(enabled) == 1 ? game.GetTag(cap) : 0;
		}

		private void ApplyTop4(LobbyView view, EstimatesView estimates)
		{
			foreach(var row in view.Rows)
				if(estimates.Top4ById.TryGetValue(row.PlayerId, out var p) && row.Style != RowStyle.Dead)
					row.Top4 = $"{p:P0}";
		}

		private EstimatesView BuildEstimates(bool gameOver)
		{
			var view = new EstimatesView();
			var p = _placement;
			if(p != null && !gameOver && p.Top4.TryGetValue(_me, out var top4))
			{
				view.Top4 = top4;
				view.Top4ById = p.Top4;
				if(!Core.Game.IsBattlegroundsCombatPhase)
					GameReview.RecordTop4(_turn, top4);
				view.Lines.Add(L.T($"Шанс места: топ-4 {top4:P0} · 1-е {p.First[_me]:P0} · в среднем {p.ExpectedPlace[_me]:F1}-е",
					$"Placement: top 4 {top4:P0} · 1st {p.First[_me]:P0} · average {p.ExpectedPlace[_me]:F1}"));
				view.Lines.Add(L.T($"Риск вылета: в ближайшем бою {p.Death[0]:P0} · за 2 боя {p.Death[1]:P0} · за 3 боя {p.Death[2]:P0}",
					$"Risk of elimination: next fight {p.Death[0]:P0} · within 2 {p.Death[1]:P0} · within 3 {p.Death[2]:P0}"));
				view.Lines.Add(L.T("  эвристика: здоровье, тир, шансы до боя (проверено на 7 партиях)", "  heuristic: health, tier, pre-combat odds (checked on 7 games)"));
			}
			if(!gameOver)
			{
				// Сила стола — боями (BobsBuddy) против записанных столов раунда, а не суммой статов: та обманывала на нежити с перерождениями.
				var field = PrecombatEstimator.Field;
				if(field != null && field.Boards > 0 && !field.Running && !Core.Game.IsBattlegroundsCombatPhase)
					GameReview.RecordField(field.Turn, field.Win + 0.5 * field.Tie);
				if(field != null && field.Boards > 0)
					view.Lines.Add(L.T($"Твой стол против столов хода {field.Turn}: побед ≈{field.Win:P0}, поражений ≈{field.Loss:P0} · сильнее {field.Beats} из {field.Boards} (BobsBuddy)",
						$"Your board vs turn {field.Turn} boards: ≈{field.Win:P0} wins, ≈{field.Loss:P0} losses · beats {field.Beats} of {field.Boards} (BobsBuddy)")
						+ (field.Running ? " …" : ""));
				else if(field?.Running == true)
					view.Lines.Add(L.T("Твой стол против столов этого хода: считаю…", "Your board vs this turn's boards: calculating…"));
				view.Lines.AddRange(OpeningAdvisor.Lines(_turn, _lobbyRaces)); // ходы 1–3: что купить, заморозка, план с тёмным даром
				var gift = BoardNow.DarkGiftText(_turn);
				if(gift != null)
					view.Lines.Add(gift);
				// Ошибка в одном разделе не должна останавливать обновление всей панели.
				try
				{
					view.Pool.AddRange(PoolLines().ToList());
				}
				catch(Exception ex)
				{
					Paths.Log("PoolLines: " + ex.Message);
				}
			}
			view.Review.AddRange(GameReview.Lines(gameOver));
			return view;
		}

		/// <summary>Пул в цифрах: ключевые карты моей сборки (лучший гайд) и кандидаты на триплет — шанс увидеть за ролл и за 5 роллов.</summary>
		private IEnumerable<string> PoolLines()
		{
			var myTile = LobbyState.Players?.OfType<JObject>().FirstOrDefault(t => (int?)t["player_id"] == _me);
			if(myTile == null)
				yield break;
			int myTier = (int?)myTile["tier"] ?? 1;
			var lobby = new HashSet<Race>(_lobbyRaces);
			var mine = MyShopCards().BoardAndHand;
			var taken = PoolOdds.Taken(mine.Concat(LobbyState.AllSeenCards()));
			var myCounts = PoolOdds.Taken(mine);

			string Odds(HearthDb.Card card)
			{
				var chance = PoolOdds.RollChance(card, myTier, lobby, taken, out var left);
				var name = card.GetLocName(L.CardLocale) ?? card.Name;
				if(chance == null)
					return L.T($"«{name}» т{card.TechLevel} — откроется на т{card.TechLevel}, в пуле ≈{left}",
						$"\"{name}\" t{card.TechLevel} — unlocks at t{card.TechLevel}, ≈{left} in pool");
				return L.T($"«{name}» т{card.TechLevel} — в пуле ≈{left}, за ролл {chance:P0}, за 5 роллов {PoolOdds.AfterRolls(chance.Value, 5):P0}",
					$"\"{name}\" t{card.TechLevel} — ≈{left} in pool, {chance:P0} per roll, {PoolOdds.AfterRolls(chance.Value, 5):P0} in 5 rolls");
			}

			var shop = MyShopCards();
			var state = new BuildState
			{
				MyCards = mine, Offers = shop.Offers, Trinkets = shop.Trinkets, Tier = myTier, Gold = shop.Gold, UpgradeCost = shop.UpgradeCost,
				BoardCount = shop.Board.Count, Turn = _turn, Lobby = lobby, Taken = taken, Field = PrecombatEstimator.Field,
				OtherTiles = LobbyState.Players?.OfType<JObject>().Where(t => (int?)t["player_id"] != _me).ToList() ?? new List<JObject>(),
			};
			foreach(var line in BuildAdvisor.Lines(state))
				yield return line;
			foreach(var kv in myCounts.Where(kv => kv.Value % 3 == 2).Take(2))
				if(HearthDb.Cards.All.TryGetValue(kv.Key, out var card))
					yield return L.T("  до триплета: ", "  for a triplet: ") + Odds(card);
		}

		/// <summary>
		/// В бою стол соперника виден — запоминаем его карты (первые секунды боя, пока все на месте): по ним считается совпадение с гайдами.
		/// Соперник боя — тот, кто был «следующим» в таверне.
		/// </summary>
		private void TrackCombat()
		{
			if(!Core.Game.IsBattlegroundsCombatPhase)
			{
				_combatStart = null;
				return;
			}
			if(_combatStart == null)
			{
				_combatStart = DateTime.Now;
				// Подозрительность (проверка «на читера/бота») — по записи этого боя, боями против поля (Suspicion).
				var others = MyShopCards().BoardAndHand.Concat(LobbyState.AllSeenCardsExcept(_nextId)).ToList();
				Suspicion.Expect(_turn, _nextId, others);
			}
			if(_nextId <= 0 || (DateTime.Now - _combatStart.Value).TotalSeconds > 4)
				return;
			var minions = Core.Game.Opponent.Board.Where(e => e.IsMinion).ToList();
			if(minions.Count > 0)
				LobbyState.AddSeen(_nextId, _turn, minions.Select(e => e.CardId));
		}

		private string _fieldSignature;
		private DateTime _fieldChanged = DateTime.MinValue;

		/// <summary>Сила моего стола против поля: в таверне, когда стол не менялся 3 с (не на каждую покупку) и шансы до боя уже посчитаны.</summary>
		private void MaybeStartField()
		{
			if(!PrecombatEstimator.Supported || Core.Game.IsBattlegroundsCombatPhase || _turn < 2 || !BoardLibrary.Loaded)
				return;
			var shop = MyShopCards();
			if(shop.Board.Count == 0)
				return;
			var signature = $"{_turn}|{shop.Signature}";
			if(signature != _fieldSignature)
			{
				_fieldSignature = signature;
				_fieldChanged = DateTime.Now;
				return;
			}
			if((DateTime.Now - _fieldChanged).TotalSeconds < 3 || PrecombatEstimator.Field?.Signature == signature || PrecombatEstimator.Busy)
				return;
			var me = LobbyState.Players?.OfType<JObject>().FirstOrDefault(t => (int?)t["player_id"] == _me);
			if(me != null)
				PrecombatEstimator.StartField(_turn, me, _lobbyRaces, signature);
		}

		/// <summary>Пересчёт шансов до боя: в таверне, когда сменились соперник, его состав/тир или мой стол (не чаще раза в 3 с).</summary>
		private void MaybeStartPrecombat()
		{
			if(!PrecombatEstimator.Supported || Core.Game.IsBattlegroundsCombatPhase || _turn < 2 || _nextId <= 0 || !BoardLibrary.Loaded)
				return;
			var tiles = LobbyState.Players?.OfType<JObject>().ToList();
			var opponent = tiles?.FirstOrDefault(t => (int?)t["player_id"] == _nextId);
			var me = tiles?.FirstOrDefault(t => (int?)t["player_id"] == _me);
			if(opponent == null || me == null || PrecombatEstimator.Busy || (DateTime.Now - _lastPrecombat).TotalSeconds < 3)
				return;
			var myBoard = string.Join(",", Core.Game.Player.Board.Where(e => e.IsMinion).Select(e => $"{e.CardId}:{e.Attack}/{e.Health}"));
			var signature = $"{_turn}|{_nextId}|{opponent["tier"]}|{opponent["races"]?.ToString(Newtonsoft.Json.Formatting.None)}|{myBoard}";
			if(signature == _precombatSignature)
				return;
			_precombatSignature = signature;
			_lastPrecombat = DateTime.Now;
			PrecombatEstimator.Start(_turn, opponent, me, LobbyState.HeroName((string)opponent["hero"]), _lobbyRaces);
		}

		/// <summary>
		/// Таблица игроков в том же порядке, что таблица лидеров в игре (по текущему месту), — легко сопоставить глазами.
		/// Вылетевшие показываются полностью (последний состав, раунд вылета), но бледнее.
		/// </summary>
		private LobbyView BuildView(bool gameOver)
		{
			var view = new LobbyView();
			view.Info.Add($"MMR {Core.Game.CurrentBattlegroundsRating?.ToString() ?? "?"}" + (_tribes.Length > 0 ? $" · {_tribes}" : "")
						  + (gameOver ? L.T(" · партия окончена", " · game over") : ""));
			if(_ratingsLoading)
				view.Info.Add(L.T("таблица лидеров: ", "leaderboard: ") + (LeaderboardService.GetProgress(_region, _duos) ?? L.T("загрузка…", "loading…")));

			var tiles = LobbyState.Players?.OfType<JObject>().ToList();
			var players = _players;
			if(tiles == null || tiles.Count == 0)
			{
				// Таблица лидеров игры ещё не прочитана — хотя бы список героев и MMR.
				if(players == null || players.Count == 0)
					view.Info.Add(L.T("соперники: жду данных лобби…", "opponents: waiting for lobby data…"));
				else
					foreach(var p in players)
						view.Rows.Add(new LobbyRow { Hero = LobbyState.HeroName(p.HeroCardId), Mmr = Mmr(p) });
				return view;
			}

			var history = LobbyState.History?.OfType<JObject>().ToList() ?? new List<JObject>();
			JObject next = null;
			foreach(var t in tiles.Select((t, i) => (t, i)).OrderBy(x => (int?)x.t["place"] ?? 9).ThenBy(x => x.i).Select(x => x.t))
			{
				int id = (int?)t["player_id"] ?? 0;
				int hp = ((int?)t["health"] ?? 0) - ((int?)t["damage"] ?? 0), armor = (int?)t["armor"] ?? 0;
				bool dead = (bool?)t["dead"] == true || hp <= 0 && (int?)t["health"] > 0;
				// Таблица лидеров игры обновляет здоровье уже в начале боя (исход известен до конца анимации): соперник, которого этот бой
				// добивает, «вылетает» сразу. В бою окно «Следующий соперник» с ним всё равно показываем (2026-10-23, бой с Финли).
				bool isNext = !gameOver && id != _me && id == _nextId && (!dead || Core.Game.IsBattlegroundsCombatPhase);
				if(isNext)
					next = t;
				int ws = (int?)t["win_streak"] ?? 0, ls = (int?)t["lose_streak"] ?? 0;
				var lobbyPlayer = players?.FirstOrDefault(p => SameHero(p.HeroCardId, (string)t["hero"]));
				var row = new LobbyRow
				{
					PlayerId = id,
					Place = $"{(int?)t["place"]}",
					Hero = (isNext ? "⚔ " : "") + LobbyState.HeroName((string)t["hero"]),
					Tier = L.T("т", "t") + (int?)t["tier"],
					Hp = dead ? "—" : armor > 0 ? $"{hp}+{armor}" : $"{hp}",
					Comp = LobbyState.Composition(t["races"] as JObject),
					Streak = dead ? DeathText(history, id) : ws > 1 ? $"+{ws}" : ls > 1 ? $"−{ls}" : "",
					Mmr = lobbyPlayer == null ? "" : Mmr(lobbyPlayer),
					Style = id == _me ? RowStyle.Me : dead ? RowStyle.Dead : isNext ? RowStyle.Next : RowStyle.Normal,
				};
				row.Details.AddRange(Details(t, id == _me));
				view.Rows.Add(row);
			}
			view.Pool = PoolLine(tiles);
			if(next != null)
				AddNextOpponent(view, next, history);
			return view;
		}

		/// <summary>
		/// Сводка партии для ИИ-помощника — то же, что видно на панели (все подробности раскрыты), плюс мой стол с названиями и статами.
		/// Числа здесь — из симулятора и статистики; модель должна брать их отсюда, а не считать сама.
		/// </summary>
		private string ContextText(LobbyView view, bool gameOver)
		{
			// Коротко (вызывается внутри L.InEnglish): по строке на игрока, самая вероятная сборка, подозрительность — без пояснений.
			var sb = new System.Text.StringBuilder();
			sb.AppendLine($"Turn {_turn}" + (gameOver ? " (game over)" : "") + $"; lobby tribes: {string.Join(", ", _lobbyRaces)}; MMR {Core.Game.CurrentBattlegroundsRating?.ToString() ?? "?"}");
			sb.AppendLine("Players (place. hero · tier · health+armor · tribes · streak; quil = quilboar, drag = dragon, elem = elemental, aber = aberration, murl = murloc, all = all types):");
			var tiles = LobbyState.Players?.OfType<JObject>().ToList() ?? new List<JObject>();
			foreach(var r in view.Rows)
			{
				var line = $"  {r.Place}. {r.Hero.Replace("⚔ ", "")} · {r.Tier} · {r.Hp} · {r.Comp}" + (string.IsNullOrEmpty(r.Streak) ? "" : $" · {r.Streak}")
						   + (r.Style == RowStyle.Me ? " ← me" : r.Style == RowStyle.Next ? " ← next opponent" : "");
				var tile = tiles.FirstOrDefault(t => (int?)t["player_id"] == r.PlayerId);
				if(tile != null && r.Style != RowStyle.Dead)
				{
					var cards = r.Style == RowStyle.Me ? MyShopCards().Board : LobbyState.Seen(r.PlayerId)?.Value;
					var m = CompGuides.Match(tile["races"] as JObject, cards, 1).FirstOrDefault();
					if(m != null && m.Percent >= 50)
						line += $" · likely build {m.Guide.Name} ({m.Percent}%)";
				}
				if(r.Style != RowStyle.Me && Suspicion.Of(r.PlayerId) is var sus && sus != null)
					line += $" · suspicion {sus.K:F1}";
				sb.AppendLine(line);
			}
			if(!gameOver)
				sb.Append(BoardNow.Text(_turn)); // стол, рука, таверна, золото; в бою — стол соперника и прогноз BobsBuddy
			else if(GameReview.TurnsForAi() is string turns)
				sb.Append(turns); // разбор партии: как рос мой стол, где бои шли против прогноза
			var heroes = BoardNow.LobbyHeroes(); // силы героев и безделушки всех игроков лобби
			if(heroes != null)
				sb.Append(heroes);
			if(view.Odds != null)
				sb.AppendLine(view.Odds + (view.OddsNote != null ? " (" + view.OddsNote.Trim() + ")" : ""));
			foreach(var line in view.NextBrief)
				sb.AppendLine(line.Trim());
			return sb.ToString();
		}

		/// <summary>Подробности игрока: динамика состава, предполагаемые сборки, увиденный стол.</summary>
		private IEnumerable<string> Details(JObject tile, bool isMe)
		{
			int id = (int?)tile["player_id"] ?? 0;
			var trajectory = LobbyState.TrajectoryText(id);
			yield return L.T("Динамика: ", "History: ") + (trajectory ?? L.T("пока нет данных (пишется по ходам)", "no data yet (recorded each turn)"));

			List<string> cards = null;
			string source;
			if(isMe)
			{
				cards = MyShopCards().Board;
				source = L.T("по твоему столу", "from your board");
			}
			else if(LobbyState.Seen(id) is KeyValuePair<int, List<string>> seen)
			{
				cards = seen.Value;
				source = L.T($"стол видели на ходу {seen.Key}: {cards.Count} карт", $"board seen on turn {seen.Key}: {cards.Count} cards");
				if(Suspicion.Pending(id))
					yield return L.T("Подозрительность: считаю (бои BobsBuddy против столов этого хода)…", "Suspicion: calculating (BobsBuddy fights vs this turn's boards)…");
				else if(Suspicion.Of(id) is var sus && sus != null)
					yield return L.T($"Подозрительность: {sus.K:F1} (ход {sus.Turn}: сила {sus.KStrength:F1} — поле нужно усилить ×{Math.Pow(2, sus.Strength):F1}; сложность сбора {sus.KDifficulty:F1}; 1,0 — порог ≈ 1 из 100; не обвинение)",
						$"Suspicion: {sus.K:F1} (turn {sus.Turn}: strength {sus.KStrength:F1} — the field needs ×{Math.Pow(2, sus.Strength):F1} stats; assembly difficulty {sus.KDifficulty:F1}; 1.0 — threshold ≈ 1 in 100; not an accusation)");
			}
			else
				source = L.T("стол не видели — только по составу", "board not seen — composition only");

			var matches = CompGuides.Match(tile["races"] as JObject, cards);
			if(CompGuides.All.Count == 0)
				yield return L.T("Сборка: нет гайдов — откройте Comp Guides в HDT один раз", "Build: no guides — open Comp Guides in HDT once");
			else if(matches.Count == 0)
				yield return L.T("Сборка: не похоже ни на один гайд", "Build: no guide matches");
			else
			{
				yield return L.T("Сборка ", "Build ") + $"({source}):";
				foreach(var m in matches)
					yield return $"  {m.Guide.Name} [{m.Guide.TierText}] — {m.Percent}% ({m.Why})";
			}
		}

		private string Mmr(HearthMirror.Objects.BattlegroundsLobbyPlayer p)
		{
			if(!_ratings.TryGetValue(LeaderboardService.NormalizeName(p.Name), out var r))
				return "";
			// В таблице Blizzard имена без #1234 — сильно выше моего рейтинга почти наверняка однофамилец.
			var mine = Core.Game.CurrentBattlegroundsRating ?? 0;
			return mine > 0 && r > mine + 3000 ? $"{r}?" : r.ToString();
		}

		/// <summary>«вылетел р9» — по истории боёв лобби (запись, где его выбили).</summary>
		private static string DeathText(List<JObject> history, int id)
		{
			var killed = history.Where(h => (int?)h["opponentId"] == id && (bool?)h["isDefeated"] == true)
				.Select(h => (int?)h["index"] ?? 0).DefaultIfEmpty(-1).Max();
			return killed >= 0 ? L.T($"вылетел на ходу {killed + 1}", $"out on turn {killed + 1}") : L.T("вылетел", "out");
		}

		/// <summary>
		/// Следующий соперник — тег NEXT_OPPONENT_PLAYER_ID на моём герое (игра ставит его в начале таверны; HDT уже держит его в памяти).
		/// Флаг m_isNextOpponent в таблице лидеров игры на практике не выставляется (партия 2026-10-22: 0 из 69 снимков).
		/// </summary>
		private static int NextOpponentId(int me)
		{
			try
			{
				return Core.Game.Entities.Values.ToList()
					.Where(e => e.IsHero && e.IsControlledBy(me) && e.HasTag(GameTag.NEXT_OPPONENT_PLAYER_ID))
					.OrderByDescending(e => e.Id)
					.Select(e => e.GetTag(GameTag.NEXT_OPPONENT_PLAYER_ID))
					.FirstOrDefault();
			}
			catch(InvalidOperationException)
			{
				return 0; // HDT как раз меняет список сущностей — возьмём на следующем обновлении
			}
		}

		/// <summary>
		/// Занятость общего пула: сколько существ каждого типа стоит на столах живых игроков и сколько игроков этот тип собирают (≥ 3 существ).
		/// Чем больше на столах — тем меньше копий осталось в таверне.
		/// </summary>
		private static string PoolLine(List<JObject> tiles)
		{
			var total = new Dictionary<string, int>();
			var collectors = new Dictionary<string, int>();
			foreach(var t in tiles)
			{
				int hp = ((int?)t["health"] ?? 0) - ((int?)t["damage"] ?? 0);
				if((bool?)t["dead"] == true || hp <= 0 && (int?)t["health"] > 0)
					continue;
				foreach(var p in (t["races"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
				{
					if(p.Name == "ALL")
						continue;
					total[p.Name] = total.TryGetValue(p.Name, out var n) ? n + (int)p.Value : (int)p.Value;
					if((int)p.Value >= 3)
						collectors[p.Name] = collectors.TryGetValue(p.Name, out var c) ? c + 1 : 1;
				}
			}
			if(total.Count == 0)
				return null;
			return L.T("На столах: ", "On boards: ") + string.Join(" · ", total.OrderByDescending(kv => kv.Value).Select(kv =>
				$"{LobbyState.Short(kv.Key)} {kv.Value}" + (collectors.TryGetValue(kv.Key, out var c) ? L.T($" ({c} собир.)", $" ({c} going)") : "")));
		}

		/// <summary>Блок «Следующий соперник»: его стол, шансы до боя, прошлые бои, динамика и сборка.</summary>
		private void AddNextOpponent(LobbyView view, JObject next, List<JObject> history)
		{
			int id = (int?)next["player_id"] ?? 0;
			view.Next.Add(L.T("⚔ Следующий: ", "⚔ Next: ") + $"{LobbyState.HeroName((string)next["hero"])} · {L.T("т", "t")}{(int?)next["tier"]} · {LobbyState.Composition(next["races"] as JObject)}");
			SetOdds(view, id);
			var ours = history.Where(h => (int?)h["owner_id"] == _me && (int?)h["opponentId"] == id).OrderBy(h => (int?)h["index"]).LastOrDefault();
			view.NextBrief.Add(ours == null ? L.T("  раньше не встречались", "  haven't met yet") : L.T("  прошлый бой с ним: ", "  last fight with them: ") + Describe(ours, _me, id));
			var his = history.Where(h => (int?)h["owner_id"] == id).OrderBy(h => (int?)h["index"]).LastOrDefault();
			if(his != null)
				view.NextBrief.Add(L.T("  его последний бой: ", "  their last fight: ") + Describe(his, id, (int?)his["opponentId"] ?? 0));
			view.Next.AddRange(view.NextBrief);
			foreach(var line in Details(next, isMe: false))
				view.Next.Add("  " + line);
			var after = AfterNext(history, id);
			if(after != null)
			{
				view.Next.Add(after);
				view.NextBrief.Add(after);
			}
		}

		/// <summary>
		/// Соперник через ход — по правилам подбора пар, выведенным из наших записей (tools/pairing_report.py, 7 партий, 558 пар):
		/// пока живы все 8 — круговая система: каждый встречает каждого раз в 7 раундов (96 из 96 повторов — ровно через 7) →
		/// после 7-го раунда соперник через ход = соперник 7 раундов назад, в первом круге — кто-то из ещё не встреченных;
		/// после вылетов — любой живой, кроме двух последних соперников (558 из 558). При нечётном числе живых возможен призрак.
		/// </summary>
		private string AfterNext(List<JObject> history, int nextId)
		{
			var tiles = LobbyState.Players?.OfType<JObject>().ToList();
			if(tiles == null)
				return null;
			var alive = tiles.Where(t => ((int?)t["health"] ?? 0) - ((int?)t["damage"] ?? 0) > 0 && (bool?)t["dead"] != true)
				.ToDictionary(t => (int?)t["player_id"] ?? 0);
			if(!alive.ContainsKey(_me) || alive.Count < 3)
				return null;
			// Мои соперники по раундам: история + ближайший (раунд _turn).
			var mine = history.Where(h => (int?)h["owner_id"] == _me).GroupBy(h => ((int?)h["index"] ?? 0) + 1)
				.ToDictionary(g => g.Key, g => (int?)g.First()["opponentId"] ?? 0);
			mine[_turn] = nextId;
			int after = _turn + 1;
			List<int> candidates;
			string rule;
			if(alive.Count == 8)
			{
				if(after > 7 && mine.TryGetValue(after - 7, out var repeat) && alive.ContainsKey(repeat))
				{
					candidates = new List<int> { repeat };
					rule = L.T("круг из 7 ходов", "7-turn cycle");
				}
				else
				{
					candidates = alive.Keys.Where(id => id != _me && !mine.Values.Contains(id)).ToList();
					rule = L.T("ещё не встречались", "not met yet");
				}
			}
			else
			{
				candidates = alive.Keys.Where(id => id != _me && id != nextId && (!mine.TryGetValue(_turn - 1, out var prev) || id != prev)).ToList();
				rule = L.T("кроме двух последних соперников", "except the last two opponents");
			}
			if(candidates.Count == 0)
				return null;
			string Who(int id) => $"{LobbyState.HeroName((string)alive[id]["hero"])} ({L.T("т", "t")}{(int?)alive[id]["tier"]} · {LobbyState.Composition(alive[id]["races"] as JObject)})";
			var names = candidates.Count <= 4 ? string.Join(", ", candidates.Select(Who)) : L.T($"{candidates.Count} игроков", $"{candidates.Count} players");
			var ghost = alive.Count % 2 == 1 ? L.T(" или призрак", " or a ghost") : "";
			return (candidates.Count == 1 ? L.T("Через ход: ", "Turn after: ") : L.T("Через ход — один из: ", "Turn after — one of: ")) + names + ghost + $" [{rule}]";
		}

		private void SetOdds(LobbyView view, int opponentId)
		{
			if(!PrecombatEstimator.Supported)
			{
				view.Odds = L.T("Шансы до боя: недоступны в этой версии HDT", "Pre-combat odds: unavailable in this HDT version");
				return;
			}
			if(!BoardLibrary.Loaded)
			{
				view.Odds = L.T("Шансы до боя: загружаю библиотеку столов…", "Pre-combat odds: loading board library…");
				return;
			}
			var r = PrecombatEstimator.Current;
			if(r == null || r.OpponentId != opponentId || r.Turn != _turn)
			{
				view.Odds = _turn < 2 ? null : L.T("Шансы до боя: жду…", "Pre-combat odds: waiting…");
				return;
			}
			if(r.Running)
				view.Odds = L.T("Шансы до боя: считаю…", "Pre-combat odds: calculating…");
			else if(r.Error != null)
				view.Odds = L.T("Шансы до боя: ", "Pre-combat odds: ") + r.Error;
			else
			{
				view.Odds = L.T($"Шансы до боя ≈ победа {r.Win:P0} · ничья {r.Tie:P0} · поражение {r.Loss:P0}",
					$"Pre-combat odds ≈ win {r.Win:P0} · tie {r.Tie:P0} · loss {r.Loss:P0}");
				view.OddsNote = L.T($"  грубо: мой стол против {r.Boards} похожих столов из записей; победа от {r.MinWin:P0} до {r.MaxWin:P0}",
					$"  rough: my board vs {r.Boards} similar recorded boards; win {r.MinWin:P0}–{r.MaxWin:P0}");
			}
		}

		/// <summary>Запись истории глазами владельца: «ход 5, вы: победа +10» / «ход 3, вы: поражение −3» / «ход 1, ничья».</summary>
		private static string Describe(JObject h, int owner, int opponent)
		{
			int round = ((int?)h["index"] ?? 0) + 1, damage = (int?)h["damage"] ?? 0, target = (int?)h["damageTarget"] ?? 0;
			string r = L.T("ход ", "turn ") + round;
			if(target == owner)
				return $"{r}, {L.T("поражение", "loss")} −{damage}";
			if(target == opponent)
				return $"{r}, {L.T("победа", "win")} +{damage}" + ((bool?)h["isDefeated"] == true ? L.T(" (добил)", " (knocked out)") : "");
			return $"{r}, {L.T("ничья", "tie")}";
		}

		private async Task LoadLobby()
		{
			// HearthMirror иногда не готов сразу после выбора героев — до трёх попыток с паузой.
			for(int attempt = 1; attempt <= 3; attempt++)
			{
				try
				{
					var races = Reflection.Client.GetAvailableBattlegroundsRaces() ?? new List<int>();
					_lobbyRaces = races.Select(r => (Race)r).ToList();
					_tribes = string.Join(", ", races.Select(r => LobbyState.Short(Enum.GetName(typeof(Race), r) ?? r.ToString())));
					var lobby = Reflection.Client.GetBattlegroundsLobbyInfo();
					if(lobby?.Players?.Count > 0)
					{
						_players = lobby.Players;
						ActivityFeed.Post(L.T($"Лобби: {lobby.Players.Count} игроков, типы — {_tribes}", $"Lobby: {lobby.Players.Count} players, tribes — {_tribes}"));
						break;
					}
				}
				catch(Exception ex)
				{
					Paths.Log($"Lobby.LoadLobby (попытка {attempt}): {ex.Message}");
				}
				if(attempt == 3)
				{
					ActivityFeed.Post(L.T("Лобби: данные игры недоступны, повторю позже", "Lobby: game data unavailable, will retry"), ActivityLevel.Warning);
					_lobbyRequested = false; // OnUpdate попробует снова
					return;
				}
				await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
			}

			if(_region == null)
			{
				ActivityFeed.Post(L.T("Таблица лидеров: в этом регионе нет публичной таблицы", "Leaderboard: no public leaderboard for this region"));
				return;
			}
			_ratingsLoading = true;
			try
			{
				_ratings = await _leaderboards.GetAsync(_region, _duos);
				var inTop = _players.Count(p => _ratings.ContainsKey(LeaderboardService.NormalizeName(p.Name)));
				ActivityFeed.Post(_ratings.Count == 0
					? L.T("Таблица лидеров недоступна — MMR соперников не показываю", "Leaderboard unavailable — opponents' MMR not shown")
					: L.T($"Таблица лидеров: {_ratings.Count} игроков, из лобби в топе — {inTop}", $"Leaderboard: {_ratings.Count} players, {inTop} from this lobby"),
					_ratings.Count == 0 ? ActivityLevel.Warning : ActivityLevel.Info);
			}
			finally
			{
				_ratingsLoading = false;
			}
		}

		/// <summary>Герой в лобби и на плитке может отличаться скином (…_SKIN_A) — сравниваем по основе.</summary>
		private static bool SameHero(string a, string b)
		{
			string Base(string id) => id == null ? "" : (id.IndexOf("_SKIN", StringComparison.Ordinal) is var i && i > 0 ? id.Substring(0, i) : id);
			return a != null && b != null && Base(a) == Base(b);
		}

		/// <summary>Регион HDT → регион API Blizzard (у Китая публичной таблицы в этом API нет).</summary>
		private static string ApiRegion(string hdtRegion)
		{
			switch(hdtRegion)
			{
				case "US": return "US";
				case "EU": return "EU";
				case "ASIA": return "AP";
				default: return null;
			}
		}
	}
}
