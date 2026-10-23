using System;
using System.Collections.Generic;
using System.Linq;
using HearthDb.Enums;
using Newtonsoft.Json.Linq;

namespace HSaiHelper.Hdt
{
	/// <summary>Раздел «Оценки» на панели: то, что сильный игрок не посчитает в голове быстро.</summary>
	internal class EstimatesView
	{
		/// <summary>Шанс топ-4 (0..1) — для полосы оценки; null — не считали.</summary>
		public double? Top4;
		public List<string> Lines = new List<string>();
		/// <summary>«Пул ≈» и «Разбор партии» — отдельные сворачиваемые подразделы окна.</summary>
		public List<string> Pool = new List<string>();
		public List<string> Review = new List<string>();
		/// <summary>Топ-4 по каждому игроку (для таблицы «Лобби»): player_id → 0..1.</summary>
		public Dictionary<int, double> Top4ById = new Dictionary<int, double>();
	}

	/// <summary>
	/// Шанс места и риск вылета: остаток партии разыгрывается 2000 раз. Это эвристика, а не симулятор боёв:
	/// сила игрока = тир + форма (последние бои из истории лобби, серии); бой — логистическая вероятность по разнице сил
	/// (мой ближайший бой — по нашей оценке «шансы до боя», если она есть); урон — тир победителя + выжившие, с ограничением урона,
	/// которое снимается при 4 живых; при нечётном числе живых один бьётся с призраком. Задача — порядок величин и динамика, а не точная цифра.
	/// </summary>
	internal static class PlacementModel
	{
		// Калибровка по 7 партиям (tools/placement_eval.py, 444 прогноза, Брайер топ-4 / 1-е):
		// было тир 0,45 + форма 0,35 + серии, крутизна 1,2 — 0,251 / 0,133 (хуже «всем поровну» 0,232 / 0,119);
		// стало тир 0,2, без формы и серий, крутизна 0,6 — 0,205 / 0,108. Недавние победы/поражения будущее не предсказывают — решает здоровье.
		private const double TierWeight = 0.2;
		private const double FormWeight = 0;
		private const double Slope = 0.6;

		public class Result
		{
			public Dictionary<int, double> Top4 = new Dictionary<int, double>();
			public Dictionary<int, double> First = new Dictionary<int, double>();
			public Dictionary<int, double> ExpectedPlace = new Dictionary<int, double>();
			/// <summary>Мой риск вылета за 1, 2, 3 боя.</summary>
			public double[] Death = new double[3];
		}

		private class P
		{
			public int Id;
			public double Hp;
			public int Tier;
			public double Strength;
		}

		public static Result Run(List<JObject> tiles, List<JObject> history, int me, int nextId, PrecombatResult precombat, int damageCap, int runs = 2000)
		{
			var alive = new List<P>();
			var fixedPlaces = new Dictionary<int, int>();
			foreach(var t in tiles)
			{
				int id = (int?)t["player_id"] ?? 0;
				double hp = ((int?)t["health"] ?? 0) - ((int?)t["damage"] ?? 0) + ((int?)t["armor"] ?? 0);
				if(hp <= 0 || (bool?)t["dead"] == true)
				{
					fixedPlaces[id] = (int?)t["place"] ?? 8;
					continue;
				}
				int tier = (int?)t["tier"] ?? 1;
				alive.Add(new P { Id = id, Hp = hp, Tier = tier, Strength = TierWeight * tier + FormWeight * Form(id, t, history) });
			}
			var result = new Result();
			if(alive.Count == 0)
				return result;

			var rnd = new Random(12345);
			var placeSum = alive.ToDictionary(p => p.Id, p => 0.0);
			var top4 = alive.ToDictionary(p => p.Id, p => 0);
			var first = alive.ToDictionary(p => p.Id, p => 0);
			var death = new int[3];
			for(int run = 0; run < runs; run++)
			{
				var hp = alive.ToDictionary(p => p.Id, p => p.Hp);
				var living = alive.ToList();
				int round = 0;
				while(living.Count > 1 && round < 25)
				{
					var order = living.OrderBy(_ => rnd.Next()).ToList();
					// Ближайший бой известен: я против следующего соперника.
					if(round == 0 && order.Any(p => p.Id == me) && order.Any(p => p.Id == nextId))
					{
						order.RemoveAll(p => p.Id == me || p.Id == nextId);
						order.Insert(0, alive.First(p => p.Id == nextId));
						order.Insert(0, alive.First(p => p.Id == me));
					}
					bool capOn = damageCap > 0 && living.Count > 4;
					for(int i = 0; i < order.Count; i += 2)
					{
						var a = order[i];
						var b = i + 1 < order.Count ? order[i + 1] : null; // нечётное число — бой с призраком
						// pA — вероятность победы a (без ничьих), pTie — ничьей; остаток — победа b.
						double pA, pTie = 0.08;
						if(b == null)
							pA = 0.5 * (1 - pTie);
						else if(round == 0 && precombat != null && precombat.Error == null && !precombat.Running && a.Id == me && b.Id == nextId)
						{
							pA = precombat.Win;
							pTie = precombat.Tie;
						}
						else
							pA = (1 - pTie) / (1 + Math.Exp(-Slope * (a.Strength - b.Strength)));
						double x = rnd.NextDouble();
						if(x < pTie)
							continue;
						bool aWins = x < pTie + pA;
						if(b == null)
						{
							if(!aWins)
								hp[a.Id] -= Damage(Math.Min(6, a.Tier + round / 2), capOn, damageCap, rnd);
							continue;
						}
						var winner = aWins ? a : b;
						var loser = aWins ? b : a;
						hp[loser.Id] -= Damage(Math.Min(6, winner.Tier + round / 2), capOn, damageCap, rnd);
					}
					var dead = living.Where(p => hp[p.Id] <= 0).OrderBy(p => hp[p.Id]).ToList();
					if(round < 3 && dead.Any(p => p.Id == me))
						for(int k = round; k < 3; k++)
							death[k]++;
					foreach(var d in dead)
					{
						int place = living.Count;
						placeSum[d.Id] += place;
						if(place <= 4)
							top4[d.Id]++;
						living.Remove(d);
					}
					round++;
				}
				foreach(var p in living)
				{
					placeSum[p.Id] += 1;
					top4[p.Id]++;
					first[p.Id]++;
				}
			}
			foreach(var p in alive)
			{
				result.Top4[p.Id] = top4[p.Id] / (double)runs;
				result.First[p.Id] = first[p.Id] / (double)runs;
				result.ExpectedPlace[p.Id] = placeSum[p.Id] / runs;
			}
			foreach(var kv in fixedPlaces)
			{
				result.Top4[kv.Key] = kv.Value <= 4 ? 1 : 0;
				result.First[kv.Key] = kv.Value == 1 ? 1 : 0;
				result.ExpectedPlace[kv.Key] = kv.Value;
			}
			for(int k = 0; k < 3; k++)
				result.Death[k] = death[k] / (double)runs;
			return result;
		}

		/// <summary>Форма: последние три боя из истории лобби (победа +, поражение −, свежие весомее) и текущие серии.</summary>
		private static double Form(int id, JObject tile, List<JObject> history)
		{
			double form = 0, weight = 1;
			foreach(var h in history.Where(h => (int?)h["owner_id"] == id).OrderByDescending(h => (int?)h["index"]).Take(3))
			{
				int target = (int?)h["damageTarget"] ?? 0;
				if(target == id)
					form -= 0.35 * weight;
				else if(target == (int?)h["opponentId"])
					form += 0.35 * weight;
				weight *= 0.7;
			}
			form += 0.1 * Math.Min(5, (int?)tile["win_streak"] ?? 0) - 0.1 * Math.Min(5, (int?)tile["lose_streak"] ?? 0);
			return form;
		}

		/// <summary>Урон: тир победителя + выжившие (примерно 1..2×тир), с ограничением урона, пока живых больше четырёх.</summary>
		private static double Damage(int tier, bool capOn, int cap, Random rnd)
		{
			double dmg = tier + rnd.Next(1, Math.Max(2, 2 * tier + 1));
			return capOn ? Math.Min(cap, dmg) : dmg;
		}
	}

	/// <summary>
	/// Общий пул в цифрах: сколько копий карты осталось и шанс увидеть её в таверне за ролл.
	/// Копий на тир — Settings.PoolCopies: 15/15/13/11/9/7/5 (hearthstone.wiki.gg/wiki/Battlegrounds — «Those numbers are correct», Mitchell Loewen, Blizzard, 2024-03-10; было 16 на т1 по старым источникам; предположение 5; уточнять),
	/// предложений таверны — по нашим логам: т1 — 3, т2–3 — 4, т4–5 — 5, т6 — 6 (т7 — как т6).
	/// Из пула вычитаем только то, что видели: мой стол и рука, столы соперников в боях с нами (золотое = 3 копии).
	/// Чужие невиданные столы не вычитаются — шанс поэтому ориентировочный (≈).
	/// </summary>
	internal static class PoolOdds
	{
		private static readonly int[] ShopSize = { 3, 4, 4, 5, 5, 6, 6 };

		public static int Copies(int tier)
		{
			var copies = Settings.Instance.PoolCopies ?? new[] { 15, 15, 13, 11, 9, 7, 5 };
			return tier >= 1 && tier <= copies.Length ? copies[tier - 1] : 5;
		}

		public static int Slots(int tier) => ShopSize[Math.Max(1, Math.Min(7, tier)) - 1];

		/// <summary>Карта может появиться в таверне этого лобби: тип лобби, нейтральная или «все типы».</summary>
		public static bool Eligible(HearthDb.Card card, HashSet<Race> lobby) =>
			lobby.Count == 0 || card.Race == Race.INVALID || card.Race == Race.ALL || lobby.Contains(card.Race)
			|| card.SecondaryRace != Race.INVALID && lobby.Contains(card.SecondaryRace);

		/// <summary>Сколько копий каждой карты (обычный ID) уже не в пуле — по тому, что видели.</summary>
		public static Dictionary<string, int> Taken(IEnumerable<string> cardIds)
		{
			var taken = new Dictionary<string, int>();
			foreach(var id in cardIds.Where(c => !string.IsNullOrEmpty(c)))
			{
				bool golden = HearthDb.Cards.TripleToNormalCardIds.TryGetValue(id, out var normal);
				var key = golden ? normal : id;
				taken[key] = (taken.TryGetValue(key, out var n) ? n : 0) + (golden ? 3 : 1);
			}
			return taken;
		}

		/// <summary>Шанс увидеть хотя бы одну копию карты за один ролл на тире myTier; null — карта выше тира.</summary>
		public static double? RollChance(HearthDb.Card card, int myTier, HashSet<Race> lobby, Dictionary<string, int> taken, out int left)
		{
			left = Math.Max(0, Copies(card.TechLevel) - (taken.TryGetValue(card.Id, out var t) ? t : 0));
			if(card.TechLevel > myTier)
				return null;
			double pool = 0;
			foreach(var c in HearthDb.Cards.BaconPoolMinions.Values)
			{
				if(c.TechLevel < 1 || c.TechLevel > myTier || HearthDb.Cards.TripleToNormalCardIds.ContainsKey(c.Id) || !Eligible(c, lobby))
					continue;
				pool += Math.Max(0, Copies(c.TechLevel) - (taken.TryGetValue(c.Id, out var n) ? n : 0));
			}
			if(pool <= 0 || left == 0)
				return 0;
			double miss = 1;
			for(int i = 0; i < Slots(myTier); i++)
				miss *= Math.Max(0, 1 - left / Math.Max(1, pool - i));
			return 1 - miss;
		}

		public static double AfterRolls(double perRoll, int rolls) => 1 - Math.Pow(1 - perRoll, rolls);
	}

	/// <summary>
	/// Разбор партии: сила моего стола по ходам — доля побед против поля (BobsBuddy против записанных столов хода; не статы — те обманывают
	/// на нежити), переломный момент,
	/// самый неудачный и самый удачный бой (исход против прогноза BobsBuddy). Данные — записи боёв Recorder.
	/// </summary>
	internal static class GameReview
	{
		public class Round
		{
			public int Turn;
			public double Power;
			public double? Win;
			public string Result;
			/// <summary>Мой стол перед боем (дамп BobsBuddy) — для разбора партии ИИ; названия — в момент вопроса, на языке ответа.</summary>
			public JArray Board;
			public int Tier;
		}

		private static readonly object Lock = new object();
		private static readonly SortedDictionary<int, Round> Rounds = new SortedDictionary<int, Round>();

		private static readonly SortedDictionary<int, double> Top4ByTurn = new SortedDictionary<int, double>();
		private static readonly SortedDictionary<int, double> FieldByTurn = new SortedDictionary<int, double>();

		/// <summary>Сила стола против поля в таверне этого хода (доля побед, ничья — половина) — последнее значение перед боем.</summary>
		public static void RecordField(int turn, double share)
		{
			if(turn > 0)
				lock(Lock)
					FieldByTurn[turn] = share;
		}

		public static void Reset()
		{
			lock(Lock)
			{
				Rounds.Clear();
				Top4ByTurn.Clear();
				FieldByTurn.Clear();
			}
		}

		/// <summary>Мой шанс топ-4 в таверне этого раунда (последнее значение перед боем) — для графика и переломного момента.</summary>
		public static void RecordTop4(int turn, double top4)
		{
			if(turn > 0)
				lock(Lock)
					Top4ByTurn[turn] = top4;
		}

		/// <summary>Счётчики «на всю игру» моей стороны (UndeadAttackBonus, …) из последней записи боя — для советов (Analysis).</summary>
		public static JObject LastMyCounters;

		public static void AddCombat(JObject record)
		{
			int turn = (int?)record["turn"] ?? 0;
			if(record["input"]?["Player"] is JObject me)
				LastMyCounters = new JObject(me.Properties().Where(p => p.Value.Type == JTokenType.Integer || p.Value.Type == JTokenType.Boolean));
			if(turn <= 0 || !(record["input"]?["Player"]?["Side"] is JArray side))
				return;
			var round = new Round
			{
				Turn = turn,
				Power = side.Sum(m => ((int?)m["baseAttack"] ?? 0) + ((int?)m["baseHealth"] ?? 0)),
				Win = (double?)record["output"]?["winRate"],
				Result = (string)record["result"],
				Tier = (int?)record["input"]?["Player"]?["Tier"] ?? 0,
				Board = side,
			};
			lock(Lock)
				Rounds[turn] = round;
		}

		private static string ResultRu(string r) => r == "Win" ? L.T("победа", "win") : r == "Loss" ? L.T("поражение", "loss") : r == "Tie" ? L.T("ничья", "tie") : r;

		private static List<(int Turn, double Ratio)> Ratios()
		{
			lock(Lock)
				return FieldByTurn.Where(kv => kv.Key >= 2).Select(kv => (kv.Key, kv.Value)).ToList();
		}

		/// <summary>
		/// Для ИИ после партии: по ходу — тир, мой стол, исход и прогноз BobsBuddy в бою. Без этого на «что думаешь о партии»
		/// модель пересказывала цифры с панели (2026-10-23) — теперь видит, как рос стол и где бои шли против прогноза.
		/// </summary>
		public static string TurnsForAi()
		{
			List<Round> rounds;
			lock(Lock)
				rounds = Rounds.Values.ToList();
			if(rounds.Count == 0)
				return null;
			var sb = new System.Text.StringBuilder();
			sb.AppendLine("My game by turn (tier · my board before combat · result · BobsBuddy win chance in combat):");
			foreach(var r in rounds)
			{
				var board = string.Join(", ", (r.Board ?? new JArray()).OfType<JObject>().Select(m =>
				{
					var id = (string)m["CardID"];
					var name = id != null && HearthDb.Cards.All.TryGetValue(id, out var c) ? c.GetLocName(L.CardLocale) ?? c.Name : (string)m["minionName"];
					return $"{name} {(int?)m["baseAttack"]}/{(int?)m["baseHealth"]}" + ((bool?)m["golden"] == true ? " golden" : "");
				}));
				sb.AppendLine($"  turn {r.Turn} · t{r.Tier} · {board} · {r.Result?.ToLowerInvariant()} · {r.Win:P0}");
			}
			return sb.ToString();
		}

		public static IEnumerable<string> Lines(bool gameOver)
		{
			var ratios = Ratios();
			if(ratios.Count > 0)
				yield return L.T("Сила стола против поля (доля побед): ", "Board vs the field (win share): ")
							 + string.Join(" › ", ratios.Skip(Math.Max(0, ratios.Count - 6)).Select(x => $"{L.T("ход ", "turn ")}{x.Turn} {x.Ratio:P0}"));
			List<KeyValuePair<int, double>> top4;
			List<Round> rounds;
			lock(Lock)
			{
				top4 = Top4ByTurn.ToList();
				rounds = Rounds.Values.Where(r => r.Win.HasValue).ToList();
			}
			if(top4.Count > 1)
				yield return L.T("Шанс топ-4 по ходам: ", "Top-4 chance by turn: ")
							 + string.Join(" › ", top4.Skip(Math.Max(0, top4.Count - 8)).Select(x => $"{L.T("ход ", "turn ")}{x.Key} {x.Value:P0}"));
			if(!gameOver)
				yield break;
			// Переломный момент — самое большое падение шанса топ-4 между соседними раундами (сила стола сама по себе обманчива:
			// «378 % → 176 % медианы» — всё ещё сильный стол). Причина — исход боя этого раунда против прогноза BobsBuddy.
			var drop = top4.Zip(top4.Skip(1), (a, b) => (Turn: a.Key, From: a.Value, To: b.Value)).OrderBy(x => x.To - x.From).FirstOrDefault();
			if(drop.Turn > 0 && drop.To < drop.From - 0.1)
			{
				var fight = rounds.FirstOrDefault(r => r.Turn == drop.Turn);
				var why = fight == null ? "" : L.T($" (бой на ходу {fight.Turn}: {ResultRu(fight.Result)} при шансе победы {fight.Win:P0})",
					$" (turn {fight.Turn} fight: {fight.Result?.ToLowerInvariant()} at {fight.Win:P0} to win)");
				yield return L.T($"Переломный момент: после хода {drop.Turn} шанс топ-4 упал с {drop.From:P0} до {drop.To:P0}",
					$"Turning point: after turn {drop.Turn} the top-4 chance fell from {drop.From:P0} to {drop.To:P0}") + why;
			}
			var unlucky = rounds.Where(r => r.Result == "Loss").OrderByDescending(r => r.Win).FirstOrDefault();
			if(unlucky != null && unlucky.Win > 0.4)
				yield return L.T($"Самый неудачный бой: ход {unlucky.Turn} — шанс победы {unlucky.Win:P0}, но поражение",
					$"Unluckiest fight: turn {unlucky.Turn} — {unlucky.Win:P0} to win, but lost");
			var lucky = rounds.Where(r => r.Result == "Win").OrderBy(r => r.Win).FirstOrDefault();
			if(lucky != null && lucky.Win < 0.4)
				yield return L.T($"Самый удачный бой: ход {lucky.Turn} — шанс {lucky.Win:P0}, но победа",
					$"Luckiest fight: turn {lucky.Turn} — {lucky.Win:P0} to win, but won");
		}
	}
}
