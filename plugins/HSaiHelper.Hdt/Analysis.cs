using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HearthDb.Enums;
using Newtonsoft.Json.Linq;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using Core = Hearthstone_Deck_Tracker.API.Core;

namespace HSaiHelper.Hdt
{
	/// <summary>
	/// Разборы на BobsBuddy поверх записанных столов (2026-10-23):
	///   «почему шанс разошёлся» — шанс до боя (похожие столы) против точного в бою: чем стол соперника отличался и что решило бой;
	///   опасные карты сборки соперника — 3 карты, которые могут перевернуть бой (токсичность, «бить первым», уничтожение);
	///   советы на ход (галочка «советы ИИ») — шанс против следующего соперника для каждого действия, ИИ объясняет;
	///   «как обыграть самого опасного» (кнопка) — расстановка, его ключевое существо, что купить против него.
	///
	/// Порядок и важность расчётов (всё тяжёлое — когда стол 2 с без изменений, Settled):
	///   1) шансы до боя (PrecombatEstimator) — важнее всего, остальное ждёт, пока они считаются;
	///   2) «почему шанс разошёлся» и подозрительность — в бою, как только HDT посчитал исход (дальше только анимация), ~20 боёв BobsBuddy;
	///   3) сила стола против поля; 4) подозрительность соперника (Suspicion, свой поток, эталоны — вне партии);
	///   5) советы на ход и «как обыграть» — по галочке/кнопке, одно за раз (Gate), не в бою.
	/// Без силы героя и безделушек (только существа и счётчики) — поэтому цифры здесь «≈», а точный прогноз в бою даёт HDT.
	/// </summary>
	internal static class Analysis
	{
		private const int Iterations = 400, Threads = 2;
		private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
		private static readonly object Lock = new object();
		private static readonly Dictionary<int, (int Turn, BoardSample Board)> LastBoards = new Dictionary<int, (int, BoardSample)>();
		private static string _explanation;
		private static int _explanationTurn;
		private static readonly HashSet<string> Explained = new HashSet<string>();

		/// <summary>
		/// Бой идёт, но HDT уже посчитал его (исход известен почти сразу, дальше — анимация; игроки даже переподключаются, чтобы пропустить её):
		/// «почему шанс разошёлся» и подозрительность — сейчас, не дожидаясь конца боя. Запись боя после — уже не повторяет.
		/// </summary>
		public static void OnLiveCombat(JObject record)
		{
			var me = BoardLibrary.MakeSample(record, "Player");
			var opp = BoardLibrary.MakeSample(record, "Opponent");
			Suspicion.OnCombat(record, opp);
			ExplainCombat(record, me, opp);
		}

		public static void Reset()
		{
			lock(Lock)
			{
				LastBoards.Clear();
				Explained.Clear();
				_explanation = null;
				_explanationTurn = 0;
			}
		}

		public static void SetLastBoard(int playerId, BoardSample board)
		{
			lock(Lock)
				LastBoards[playerId] = (board.Turn, board);
		}

		public static (int Turn, BoardSample Board)? LastBoard(int playerId)
		{
			lock(Lock)
				return LastBoards.TryGetValue(playerId, out var b) ? b : ((int, BoardSample)?)null;
		}

		/// <summary>Последнее объяснение «почему шанс разошёлся» (для окна «Оценки» и сводки ИИ); null — нет.</summary>
		public static string Explanation(int turn)
		{
			lock(Lock)
				return _explanation != null && turn - _explanationTurn <= 1 ? _explanation : null;
		}

		// --- 2) Почему шанс до боя разошёлся с прогнозом в бою ---

		/// <summary>
		/// После записи боя: если шанс до боя (похожие столы) и точный BobsBuddy в бою разошлись на 20+ п.п. — чем стол соперника
		/// отличался от похожих (щиты, токсичность/яд, перерождения, статы) и что решило бой (бой без его щитов, без яда, без каждого существа).
		/// </summary>
		public static void ExplainCombat(JObject record, BoardSample me = null, BoardSample opp = null)
		{
			int turn = (int?)record["turn"] ?? 0;
			var pre = PrecombatEstimator.ForTurn(turn);
			double? exact = (double?)record["output"]?["winRate"];
			me ??= BoardLibrary.SampleOf(record, "Player");
			opp ??= BoardLibrary.SampleOf(record, "Opponent");
			if(pre == null || exact == null || me == null || opp == null || pre.Samples.Count == 0 || Math.Abs(exact.Value - pre.Win) < 0.2)
				return;
			lock(Lock)
				if(!Explained.Add((string)record["key"] ?? turn.ToString()))
					return; // уже разобран в бою
			Task.Run(async () =>
			{
				await Gate.WaitAsync();
				try
				{
					var parts = new List<string>();
					// Чем стол соперника отличался от похожих столов.
					foreach(var (name, count) in Features())
					{
						double his = count(opp), usual = pre.Samples.Average(x => count(x.Sample));
						if(his - usual >= 1.5)
							parts.Add(L.T($"{name.Ru}: {his:F0} (в похожих ≈{usual:F1})", $"{name.En}: {his:F0} (similar boards ≈{usual:F1})"));
					}
					double powerUsual = pre.Samples.Average(x => x.Sample.Power * x.Factor);
					if(powerUsual > 0 && opp.Power / powerUsual >= 1.5)
						parts.Add(L.T($"статы ×{opp.Power / powerUsual:F1} к похожим", $"stats ×{opp.Power / powerUsual:F1} vs similar"));
					else if(powerUsual > 0 && opp.Power / powerUsual <= 0.67)
						parts.Add(L.T($"статы всего ×{opp.Power / powerUsual:F1} к похожим", $"stats only ×{opp.Power / powerUsual:F1} vs similar"));
					// Что решило бой: те же два стола, без одного свойства или одного существа соперника.
					var key = KeyFactors(me, opp, turn);
					var text = L.T($"Ход {turn}: до боя ≈{pre.Win:P0} победы, в бою {exact:P0}. ", $"Turn {turn}: pre-combat ≈{pre.Win:P0} win, in combat {exact:P0}. ")
							   + (parts.Count > 0 ? L.T("У соперника ", "Opponent had ") + string.Join(", ", parts) + ". " : "")
							   + (key.Count > 0 ? L.T("Решило бой: ", "What decided it: ") + string.Join("; ", key) + "." : "");
					lock(Lock)
					{
						_explanation = text;
						_explanationTurn = turn;
					}
					ActivityFeed.Post(text);
				}
				catch(Exception ex)
				{
					Paths.Log("Analysis.Explain: " + ex.Message);
				}
				finally
				{
					Gate.Release();
				}
			});
		}

		private static IEnumerable<((string Ru, string En) Name, Func<BoardSample, double> Count)> Features()
		{
			yield return (("щитов", "divine shields"), b => Count(b, m => (int?)m["div"] > 0));
			yield return (("с ядом/токсичностью", "poisonous/venomous"), b => Count(b, m => (bool?)m["poisonous"] == true || (bool?)m["venomous"] == true));
			yield return (("с перерождением", "reborn"), b => Count(b, m => (bool?)m["reborn"] == true));
			yield return (("золотых", "golden"), b => Count(b, m => (bool?)m["golden"] == true));
		}

		private static int Count(BoardSample b, Func<JObject, bool> f) => b.Minions.OfType<JObject>().Count(f);

		/// <summary>До двух факторов, сильнее всего менявших бой: «без его щитов победа 55 % (было 10 %)», «без «Титуса» — 48 %».</summary>
		private static List<string> KeyFactors(BoardSample me, BoardSample opp, int turn)
		{
			var result = new List<(double Gain, string Text)>();
			var baseShare = PrecombatEstimator.Duel(me, opp, 1, turn, Iterations, Threads);
			if(baseShare == null)
				return new List<string>();
			void Try(BoardSample changed, string ru, string en)
			{
				if(PrecombatEstimator.Duel(me, changed, 1, turn, Iterations, Threads) is double s && s - baseShare.Value >= 0.15)
					result.Add((s - baseShare.Value, L.T($"{ru} — победа ≈{s:P0} (было ≈{baseShare:P0})", $"{en} — ≈{s:P0} win (was ≈{baseShare:P0})")));
			}
			Try(Modify(opp, m => m["div"] = 0), "без его щитов", "without their divine shields");
			Try(Modify(opp, m => { m["poisonous"] = false; m["venomous"] = false; }), "без яда и токсичности", "without poisonous/venomous");
			Try(Modify(opp, m => m["reborn"] = false), "без перерождений", "without reborn");
			var minions = opp.Minions.OfType<JObject>().ToList();
			for(int i = 0; i < minions.Count && minions.Count > 1; i++)
			{
				var name = CardName((string)minions[i]["CardID"]);
				Try(Remove(opp, i), $"без «{name}»", $"without \"{name}\"");
			}
			return result.OrderByDescending(r => r.Gain).Take(2).Select(r => r.Text).ToList();
		}

		private static BoardSample Modify(BoardSample b, Action<JObject> change)
		{
			var minions = (JArray)b.Minions.DeepClone();
			foreach(var m in minions.OfType<JObject>())
				change(m);
			return Copy(b, minions);
		}

		private static BoardSample Remove(BoardSample b, int index)
		{
			var minions = (JArray)b.Minions.DeepClone();
			minions.RemoveAt(index);
			return Copy(b, minions);
		}

		private static BoardSample Reorder(BoardSample b, IEnumerable<JObject> order) => Copy(b, new JArray(order.Select(m => m.DeepClone())));

		private static BoardSample Copy(BoardSample b, JArray minions) => new BoardSample
		{
			Turn = b.Turn, Tier = b.Tier, Races = b.Races, Typed = b.Typed, Player = b.Player, Minions = minions,
			Power = minions.OfType<JObject>().Sum(m => ((int?)m["baseAttack"] ?? 0) + ((int?)m["baseHealth"] ?? 0)),
		};

		// --- 5) Опасные карты сборки соперника ---

		/// <summary>
		/// До трёх карт его вероятной сборки (и увиденных у него), которые могут перевернуть бой: «бить первым» (удваивает эффекты),
		/// токсичность/яд, уничтожение, щиты, «в начале боя». Заметки игрока о картах (data/reference/key_cards.md → key_cards.json рядом
		/// с плагином) — главнее правил по тексту.
		/// </summary>
		public static List<string> DangerCards(JObject tile, ICollection<string> seenCards)
		{
			var guide = CompGuides.Match(tile?["races"] as JObject, seenCards, 1).FirstOrDefault();
			var ids = new HashSet<string>((seenCards ?? new string[0]).Select(Normal));
			var candidates = new List<HearthDb.Card>();
			if(guide != null)
				foreach(var d in guide.Guide.Core.Concat(guide.Guide.Addon))
					if(HearthDb.Cards.AllByDbfId.TryGetValue(d, out var c))
						candidates.Add(c);
			foreach(var id in ids)
				if(HearthDb.Cards.All.TryGetValue(id, out var c) && !candidates.Contains(c))
					candidates.Add(c);
			var notes = KeyCardNotes();
			// Карты из описания гайда («как играть», «что включает») — ключевые для сборки: их стоит ждать у соперника.
			var mentioned = new HashSet<int>(guide?.Guide.Mentioned() ?? Enumerable.Empty<int>());
			return candidates
				.Select(c => (Card: c, Danger: Danger(c, notes), Seen: ids.Contains(c.Id)))
				.Where(x => x.Danger.Score > 0)
				.OrderByDescending(x => x.Danger.Score + (x.Seen ? 2 : 0) + (mentioned.Contains(x.Card.DbfId) ? 1 : 0))
				.Take(3)
				.Select(x => $"«{x.Card.GetLocName(L.CardLocale) ?? x.Card.Name}» — {x.Danger.Why}" + (x.Seen ? L.T(" (видели у него)", " (seen on their board)") : ""))
				.ToList();
		}

		private static (int Score, string Why) Danger(HearthDb.Card c, Dictionary<string, (string Note, int? Importance)> notes)
		{
			// Заметка игрока главнее правил; «важность» 1–5 → вес 2–10 (правила по тексту — до 6), без важности — 9.
			if(notes.TryGetValue(c.Id, out var note) || notes.TryGetValue(c.Name ?? "", out note))
				return (note.Importance is int i ? 2 * i : 9, string.IsNullOrEmpty(note.Note) ? L.T("важная карта", "key card") : note.Note);
			var text = Regex.Replace(c.GetLocText(Locale.enUS) ?? "", "<[^>]+>", "");
			// «Бить первым» — только если удваивает то, что действует в бою (хрипы, отмщения, сплочения, начало боя).
			// Бранн удваивает боевые кличи — они срабатывают в таверне, в бою он пассивен (2026-10-23).
			if(Regex.IsMatch(text, @"trigger(s)? (an extra time|twice)", RegexOptions.IgnoreCase)
			   && Regex.IsMatch(text, @"Deathrattle|Avenge|Rally|Start of Combat|attack", RegexOptions.IgnoreCase)
			   && !Regex.IsMatch(text, @"Battlecr", RegexOptions.IgnoreCase))
				return (6, L.T("удваивает эффекты в бою — бить первым", "doubles combat effects — kill it first"));
			if(Regex.IsMatch(text, @"\b(Venomous|Poisonous)\b"))
				return (5, L.T("токсичность/яд — убивает любое существо", "venomous/poisonous — kills anything"));
			if(Regex.IsMatch(text, @"\bDestroy\b"))
				return (5, L.T("уничтожает существо", "destroys a minion"));
			if(Regex.IsMatch(text, @"Divine Shield", RegexOptions.IgnoreCase))
				return (3, L.T("щиты", "divine shields"));
			if(Regex.IsMatch(text, @"Start of Combat", RegexOptions.IgnoreCase))
				return (2, L.T("эффект в начале боя", "start-of-combat effect"));
			return (0, null);
		}

		private static Dictionary<string, (string Note, int? Importance)> _notes;

		/// <summary>Заметки о ключевых картах: key_cards.json рядом с плагином — {"id или английское имя": "что делает и как играть против"}.</summary>
		public static Dictionary<string, (string Note, int? Importance)> KeyCardNotes()
		{
			if(_notes != null)
				return _notes;
			var result = new Dictionary<string, (string, int?)>(StringComparer.OrdinalIgnoreCase);
			try
			{
				var file = Path.Combine(Path.GetDirectoryName(typeof(Analysis).Assembly.Location) ?? "", "key_cards.json");
				if(File.Exists(file))
					foreach(var p in JObject.Parse(File.ReadAllText(file)).Properties())
						result[p.Name] = p.Value is JObject o
							? ((L.En ? (string)o["en"] : null) ?? (string)o["ru"], (int?)o["importance"])
							: ((string)p.Value, (int?)null);
			}
			catch(Exception ex)
			{
				Paths.Log("Analysis.KeyCards: " + ex.Message);
			}
			return _notes = result;
		}

		/// <summary>
		/// Для ИИ: гайд сборки — описание из HDT и тексты ключевых карт. Правила игры — в текстах карт (клич — в таверне, хрип — при смерти, …);
		/// по ним модель сама выводит взаимодействия и противодействие (у нежити на хрипах почти наверняка Ривендер — бить первым).
		/// </summary>
		public static string GuideForAi(CompGuide g, string whose)
		{
			if(g == null)
				return null;
			var sb = new System.Text.StringBuilder();
			sb.AppendLine($"{whose}: {g.Name} [{g.TierText}]");
			if(!string.IsNullOrEmpty(g.HowToPlay))
				sb.AppendLine("  how to play: " + CompGuides.PlainOf(g.HowToPlay));
			if(!string.IsNullOrEmpty(g.WhenToCommit))
				sb.AppendLine("  commit when: " + CompGuides.PlainOf(g.WhenToCommit));
			var cards = g.Core.Concat(g.Mentioned()).Distinct().Take(8)
				.Select(d => HearthDb.Cards.AllByDbfId.TryGetValue(d, out var c) ? c : null).Where(c => c != null)
				.Select(c => $"{c.GetLocName(L.CardLocale) ?? c.Name} (t{c.TechLevel}): {Shorten(Text(c.Id), 110)}"
							 + (KeyCardNotes().TryGetValue(c.Id, out var n) && !string.IsNullOrEmpty(n.Note) ? $" [player's note: {n.Note}]" : ""));
			sb.AppendLine("  key cards: " + string.Join("; ", cards));
			return sb.ToString();
		}

		private static string Shorten(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

		// --- 3) Советы на ход и 4) «как обыграть самого опасного» ---

		/// <summary>Мой стол из сущностей HDT (UI-поток): существа со статами и словами + счётчики «на всю игру» из последней записи боя.</summary>
		public static BoardSample MyBoard(int turn, int tier) => FromEntities(Core.Game.Player.Board.Where(e => e.IsMinion), turn, tier, GameReview.LastMyCounters);

		/// <summary>Предложения таверны как существа (UI-поток).</summary>
		public static List<JObject> TavernMinions() =>
			Core.Game.Opponent.Board.Where(e => e.IsMinion && !string.IsNullOrEmpty(e.CardId)).OrderBy(e => e.GetTag(GameTag.ZONE_POSITION)).Select(MinionOf).ToList();

		private static BoardSample FromEntities(IEnumerable<Entity> entities, int turn, int tier, JObject counters)
		{
			var minions = new JArray(entities.Where(e => !string.IsNullOrEmpty(e.CardId)).OrderBy(e => e.GetTag(GameTag.ZONE_POSITION)).Select(MinionOf));
			return new BoardSample { Turn = turn, Tier = tier, Minions = minions, Player = counters ?? new JObject(), Races = new Dictionary<string, int>(),
				Power = minions.OfType<JObject>().Sum(m => (int)m["baseAttack"] + (int)m["baseHealth"]) };
		}

		private static JObject MinionOf(Entity e) => new JObject
		{
			["CardID"] = e.CardId, ["baseAttack"] = e.Attack, ["baseHealth"] = e.Health, ["maxHealth"] = e.Health,
			["taunt"] = e.HasTag(GameTag.TAUNT), ["div"] = e.GetTag(GameTag.DIVINE_SHIELD), ["poisonous"] = e.HasTag(GameTag.POISONOUS),
			["venomous"] = e.HasTag(GameTag.VENOMOUS), ["windfury"] = e.HasTag(GameTag.WINDFURY), ["megaWindfury"] = e.HasTag(GameTag.MEGA_WINDFURY),
			["stealth"] = e.HasTag(GameTag.STEALTH), ["reborn"] = e.HasTag(GameTag.REBORN), ["golden"] = HearthDb.Cards.TripleToNormalCardIds.ContainsKey(e.CardId),
			["tier"] = e.GetTag(GameTag.TECH_LEVEL),
			["PrimaryRace"] = HearthDb.Cards.All.TryGetValue(e.CardId, out var c) ? c.Race.ToString() : "INVALID",
			["ScriptDataNum1"] = e.GetTag(GameTag.TAG_SCRIPT_DATA_NUM_1), ["ScriptDataNum2"] = e.GetTag(GameTag.TAG_SCRIPT_DATA_NUM_2),
		};

		/// <summary>Доля побед (ничья — половина) моего стола против набора столов соперника.</summary>
		private static double? Versus(BoardSample me, List<(BoardSample Sample, double Factor)> opponents, int turn)
		{
			double sum = 0;
			int n = 0;
			foreach(var (sample, factor) in opponents)
				if(PrecombatEstimator.Duel(me, sample, factor, turn, Iterations, Threads) is double s)
				{
					sum += s;
					n++;
				}
			return n == 0 ? (double?)null : sum / n;
		}

		/// <summary>
		/// Советы на ход: шанс против следующего соперника (похожие столы шансов до боя) — оставить как есть, купить каждое существо таверны
		/// (полный стол — вместо самого слабого), лучшая расстановка. Числа — сюда, объяснение и порядок действий — ИИ.
		/// </summary>
		public static void TavernAdvice(int turn, int tier, int gold, int upgradeCost, string opponentName, List<(BoardSample, double)> opponents)
		{
			var me = MyBoard(turn, tier);
			var offers = TavernMinions();
			if(me.Minions.Count == 0 && offers.Count == 0 || opponents.Count == 0)
				return;
			Task.Run(async () =>
			{
				if(!await Gate.WaitAsync(0))
					return;
				try
				{
					var lines = L.InEnglish(() => Options(me, offers, opponents, turn, gold));
					var facts = L.InEnglish(() => $"Tavern options this turn (BobsBuddy win share vs the next opponent {opponentName}'s likely boards; no hero powers/trinkets, so ≈):\n"
										   + string.Join("\n", lines) + $"\nGold {gold}, tavern upgrade costs {upgradeCost}.");
					AiChat.SubmitAuto(L.T($"Совет на ход {turn}", $"Advice for turn {turn}"),
						"Using the tavern options with win chances below and the game state, give the best plan for this turn: 1–3 actions in order " +
						"(buy/sell/roll/level/freeze/board order) with the win chance where given, and one short reason each. Bold the first action.", facts);
				}
				catch(Exception ex)
				{
					Paths.Log("Analysis.Advice: " + ex.Message);
				}
				finally
				{
					Gate.Release();
				}
			});
		}

		/// <summary>«Как обыграть самого опасного» (кнопка): его последний увиденный стол (подросший до этого хода) или похожие столы.</summary>
		public static void Counterplay(int turn, int tier, int gold, int threatId, string threatName, string why)
		{
			var me = MyBoard(turn, tier);
			var offers = Core.Game.IsBattlegroundsCombatPhase ? new List<JObject>() : TavernMinions();
			var seen = LastBoard(threatId);
			var tiles = LobbyState.Players?.OfType<JObject>().ToList();
			var tile = tiles?.FirstOrDefault(t => (int?)t["player_id"] == threatId);
			List<(BoardSample, double)> opponents = seen != null
				? new List<(BoardSample, double)> { (seen.Value.Board, BoardLibrary.Factor(seen.Value.Turn, turn)) }
				: BoardLibrary.Similar((tile?["races"] as JObject)?.Properties().ToDictionary(p => p.Name, p => (int)p.Value) ?? new Dictionary<string, int>(),
					(int?)tile?["tier"] ?? tier, turn, 8);
			if(opponents.Count == 0 || me.Minions.Count == 0)
			{
				AiChat.SystemNote(L.T("Нечего считать: нет твоего стола или столов соперника.", "Nothing to calculate: no board of yours or theirs."));
				return;
			}
			var danger = DangerCards(tile, LobbyState.Seen(threatId)?.Value);
			Task.Run(async () =>
			{
				await Gate.WaitAsync();
				try
				{
					var lines = L.InEnglish(() => Options(me, offers, opponents, turn, gold));
					if(seen != null)
					{
						var key = L.InEnglish(() => KeyFactors(me, Grown(seen.Value.Board, BoardLibrary.Factor(seen.Value.Turn, turn)), turn));
						if(key.Count > 0)
							lines.Add("His key factors (removing them helps you most): " + string.Join("; ", key));
					}
					var facts = L.InEnglish(() => $"Threat: {threatName} ({why}). " + (seen != null ? $"Their board was seen on turn {seen.Value.Turn} (stats scaled to now)." : "Their board not seen — similar recorded boards.")
										   + "\nMy options vs them (BobsBuddy win share, ≈):\n" + string.Join("\n", lines)
										   + (danger.Count > 0 ? "\nDangerous cards of their build: " + string.Join("; ", danger) : ""));
					AiChat.SubmitAuto(L.T($"Как обыграть: {threatName}", $"How to beat: {threatName}"),
						"How do I beat this opponent? Use the numbers below: best board order, what to buy or keep against them, which of their minions/effects to play around. 2–4 short points, bold the most important.", facts);
				}
				catch(Exception ex)
				{
					Paths.Log("Analysis.Counterplay: " + ex.Message);
				}
				finally
				{
					Gate.Release();
				}
			});
		}

		private static BoardSample Grown(BoardSample b, double factor) =>
			Modify(b, m =>
			{
				m["baseAttack"] = Math.Max(1, (int)Math.Round(((int?)m["baseAttack"] ?? 1) * factor));
				m["baseHealth"] = Math.Max(1, (int)Math.Round(((int?)m["baseHealth"] ?? 1) * factor));
				m["maxHealth"] = m["baseHealth"];
			});

		/// <summary>Варианты хода с шансом: как есть, лучшая расстановка, покупка каждого существа таверны (если хватает золота).</summary>
		private static List<string> Options(BoardSample me, List<JObject> offers, List<(BoardSample, double)> opponents, int turn, int gold)
		{
			var lines = new List<string>();
			var now = Versus(me, opponents, turn);
			if(now != null)
				lines.Add($"- keep the board as is: {now:P0}");
			// Расстановка: несколько осмысленных порядков (провокации и щиты — первыми, хрипы/перерождения — первыми, наоборот).
			var minions = me.Minions.OfType<JObject>().ToList();
			if(minions.Count >= 2 && now != null)
			{
				var orders = new List<(string Name, IEnumerable<JObject> Order)>
				{
					("taunts and divine shields first", minions.OrderByDescending(m => ((bool?)m["taunt"] == true ? 2 : 0) + ((int?)m["div"] > 0 ? 1 : 0))),
					("deathrattle/reborn minions first", minions.OrderByDescending(m => (bool?)m["reborn"] == true || Text((string)m["CardID"]).Contains("Deathrattle") ? 1 : 0)),
					("reversed", Enumerable.Reverse(minions)),
					("biggest attack last", minions.OrderBy(m => (int?)m["baseAttack"] ?? 0)),
				};
				var best = orders.Select(o => (o.Name, Order: o.Order.ToList(), Share: Versus(Reorder(me, o.Order), opponents, turn)))
					.Where(o => o.Share != null).OrderByDescending(o => o.Share).FirstOrDefault();
				if(best.Share - now >= 0.05)
					lines.Add($"- reorder ({best.Name}: {string.Join(", ", best.Order.Select(m => CardName((string)m["CardID"])))}): {best.Share:P0}");
			}
			if(gold >= 3)
				foreach(var offer in offers)
				{
					JArray board;
					string action;
					if(minions.Count < 7)
					{
						board = new JArray(minions.Select(m => m.DeepClone()).Append(offer.DeepClone()));
						action = $"buy {CardName((string)offer["CardID"])}";
					}
					else
					{
						var weakest = minions.OrderBy(m => ((int?)m["baseAttack"] ?? 0) + ((int?)m["baseHealth"] ?? 0)).First();
						board = new JArray(minions.Where(m => m != weakest).Select(m => m.DeepClone()).Append(offer.DeepClone()));
						action = $"sell {CardName((string)weakest["CardID"])}, buy {CardName((string)offer["CardID"])}";
					}
					if(Versus(Copy(me, board), opponents, turn) is double s)
						lines.Add($"- {action} ({Text((string)offer["CardID"])}): {s:P0}");
				}
			return lines;
		}

		private static string Normal(string id) => id != null && HearthDb.Cards.TripleToNormalCardIds.TryGetValue(id, out var n) ? n : id;

		private static string CardName(string id) =>
			id != null && HearthDb.Cards.All.TryGetValue(id, out var c) ? c.GetLocName(L.CardLocale) ?? c.Name : id ?? "?";

		private static string Text(string id) =>
			id != null && HearthDb.Cards.All.TryGetValue(id, out var c) ? Regex.Replace(c.GetLocText(Locale.enUS) ?? "", "<[^>]+>", "") : "";
	}
}
