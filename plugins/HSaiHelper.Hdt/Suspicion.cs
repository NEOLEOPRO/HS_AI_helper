using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HSaiHelper.Hdt
{
	/// <summary>
	/// Подозрительность стола соперника — боями, а не суммой статов (та завышала столы-«статы» и занижала нежить: −0,40 в среднем).
	/// Сила стола = log2 f: во сколько раз увеличить статы столов поля (12 записанных столов хода), чтобы стол с ними сравнялся
	/// (доля побед 50 %). Статы умножаем у поля, а не у стола: его сила часто не в статах (жетоны, хрипы, баффы в бою).
	/// Эталон хода — сила каждого из 12 столов поля против остальных 11 (среднее и разброс); K = (сила − среднее) / разброс / 2,33.
	/// 1,0 — порог «необычно сильный стол» (≈ 1 из 100). Проверено (tools/BobsSim --field --strength): 260 записанных столов ходов 3+ —
	/// K ≥ 1 у 1,5 % (по статам — 1,9 %), у нежити в среднем −0,06. Ходы 1–2 не оцениваем (одно-два существа).
	/// Сложность сбора: стол из карт, которые разбирают и другие, собрать труднее. D = Σ копий карты (золотая — 3) /
	/// доступно в пуле (копий на тир − увиденные на других столах и у меня); z по ln(1 + D) против эталона хода (tools/assembly_difficulty.py,
	/// 13 партий). Итог: z = 0,8·z силы + 0,6·z сложности (сумма квадратов весов = 1 — порог «≈ 1 из 100» сохраняется).
	/// Эталон считается в фоне один раз и хранится в cache\field_reference.json (пересчёт — когда столов хода стало на четверть больше).
	/// </summary>
	internal static class Suspicion
	{
		private const int Iterations = 200, Threads = 2, FieldSize = 12, Steps = 8;
		private const double Bound = 6;

		private class Reference
		{
			public double Mean, Sd;
			public int Boards;
		}

		private static readonly object Lock = new object();
		private static Dictionary<int, Reference> _references;
		private static readonly Dictionary<int, Result> ByPlayer = new Dictionary<int, Result>();
		private static readonly Dictionary<int, (int Player, List<string> Others)> Expected = new Dictionary<int, (int, List<string>)>(); // ход → чей стол будет в записи

		public class Result
		{
			public int Turn;
			/// <summary>Итог (1,0 — порог) и составляющие: сила стола и сложность сбора — тоже в долях порога.</summary>
			public double K, KStrength, KDifficulty;
			/// <summary>Сила: log2 — во сколько раз усилить статы поля, чтобы сравняться.</summary>
			public double Strength;
		}

		/// <summary>Эталон ln(1 + D) по ходам (tools/assembly_difficulty.py, 13 партий, копии 15/15/13/11/9/7/5): среднее, разброс.</summary>
		private static readonly Dictionary<int, (double Mean, double Sd)> DifficultyReference = new Dictionary<int, (double, double)>
		{
			[3] = (0.161, 0.037), [4] = (0.243, 0.081), [5] = (0.284, 0.102), [6] = (0.318, 0.126), [7] = (0.472, 0.094), [8] = (0.576, 0.132),
			[9] = (0.603, 0.092), [10] = (0.743, 0.121), [11] = (0.938, 0.252), [12] = (0.762, 0.168), [13] = (1.083, 0.193),
		};
		private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

		private static string File => Path.Combine(Paths.Cache, "field_reference.json");

		public static void Reset()
		{
			lock(Lock)
			{
				ByPlayer.Clear();
				Expected.Clear();
			}
		}

		/// <summary>Последняя оценка игрока; null — стол не видели или ещё считаем.</summary>
		public static Result Of(int playerId)
		{
			lock(Lock)
				return ByPlayer.TryGetValue(playerId, out var v) ? v : null;
		}

		/// <summary>
		/// В бою: чей стол будет в записи этого хода (запись приходит после боя, когда «следующий» уже другой) и карты на других столах
		/// (мои и увиденные у остальных) — для сложности сбора.
		/// </summary>
		public static void Expect(int turn, int playerId, List<string> others)
		{
			if(turn >= 3 && playerId > 0)
				lock(Lock)
					Expected[turn] = (playerId, others ?? new List<string>());
		}

		/// <summary>Запись боя от Recorder (уже добавлена в BoardLibrary): считаем подозрительность стола соперника в фоне.</summary>
		public static void OnCombat(JObject record)
		{
			int turn = (int?)record["turn"] ?? 0;
			int playerId;
			List<string> others;
			lock(Lock)
			{
				if(!Expected.TryGetValue(turn, out var expected))
					return;
				Expected.Remove(turn);
				(playerId, others) = expected;
			}
			var candidate = BoardLibrary.SampleOf(record, "Opponent");
			if(candidate == null || !PrecombatEstimator.Supported)
				return;
			Task.Run(async () =>
			{
				await Gate.WaitAsync();
				try
				{
					var field = BoardLibrary.FieldSample(turn, FieldSize).Where(f => !ReferenceEquals(f.Sample, candidate)).Select(f => f.Sample).ToList();
					if(field.Count < 6)
						return;
					var reference = ReferenceFor(turn, field);
					if(reference == null)
						return;
					var strength = Strength(candidate, field, turn);
					double zStrength = (strength - reference.Mean) / reference.Sd;
					double zDifficulty = DifficultyZ(candidate, others, turn);
					var result = new Result
					{
						Turn = turn, Strength = strength, KStrength = zStrength / 2.33, KDifficulty = zDifficulty / 2.33,
						K = (0.8 * zStrength + 0.6 * zDifficulty) / 2.33,
					};
					lock(Lock)
						ByPlayer[playerId] = result;
					if(result.K >= 1)
						Flag(playerId, result, reference);
				}
				catch(Exception ex)
				{
					Paths.Log("Suspicion: " + ex.Message);
				}
				finally
				{
					Gate.Release();
				}
			});
		}

		/// <summary>z сложности сбора: ln(1 + Σ копий / доступно в пуле) против эталона хода.</summary>
		private static double DifficultyZ(BoardSample board, List<string> others, int turn)
		{
			var seen = PoolOdds.Taken(others);
			double d = 0;
			foreach(var m in board.Minions.OfType<JObject>())
			{
				var id = (string)m["CardID"];
				if(string.IsNullOrEmpty(id))
					continue;
				bool golden = (bool?)m["golden"] == true || HearthDb.Cards.TripleToNormalCardIds.ContainsKey(id);
				if(HearthDb.Cards.TripleToNormalCardIds.TryGetValue(id, out var normal))
					id = normal;
				int tier = HearthDb.Cards.All.TryGetValue(id, out var card) && card.TechLevel > 0 ? card.TechLevel : (int?)m["tier"] ?? 1;
				int available = PoolOdds.Copies(tier) - (seen.TryGetValue(id, out var n) ? n : 0);
				d += (golden ? 3 : 1) / (double)Math.Max(1, available);
			}
			var (mean, sd) = DifficultyReference.TryGetValue(turn, out var r) ? r
				: turn > 13 ? (1.083 + 0.08 * (turn - 13), 0.2) : (0.16, 0.05);
			return (Math.Log(1 + d) - mean) / Math.Max(0.08, sd);
		}

		private static void Flag(int playerId, Result r, Reference reference)
		{
			int turn = r.Turn;
			double k = r.K, strength = r.Strength;
			var tile = LobbyState.Players?.OfType<JObject>().FirstOrDefault(t => (int?)t["player_id"] == playerId);
			var name = tile == null ? "?" : LobbyState.HeroName((string)tile["hero"]);
			ActivityFeed.Post(L.T(
				$"Необычно сильный стол: {name}, ход {turn} — подозрительность {k:F1} (сила {r.KStrength:F1}: поле нужно усилить ×{Math.Pow(2, strength):F1}; сложность сбора {r.KDifficulty:F1}). Не обвинение — так бывает при удачных триплетах.",
				$"Unusually strong board: {name}, turn {turn} — suspicion {k:F1} (strength {r.KStrength:F1}: the field needs ×{Math.Pow(2, strength):F1} stats; assembly difficulty {r.KDifficulty:F1}). Not an accusation — lucky triples happen."),
				ActivityLevel.Warning);
			RecorderPlugin.Write(new JObject
			{
				["type"] = "unusual_board", ["time"] = DateTime.UtcNow, ["turn"] = turn, ["player_id"] = playerId, ["hero"] = (string)tile?["hero"],
				["method"] = "combat+difficulty", ["k"] = k, ["k_strength"] = r.KStrength, ["k_difficulty"] = r.KDifficulty, ["strength"] = strength, ["ref_mean"] = reference.Mean, ["ref_sd"] = reference.Sd, ["ref_boards"] = reference.Boards,
			});
		}

		/// <summary>Эталон хода: из файла, если столов с тех пор прибавилось меньше чем на четверть; иначе — пересчёт (12 поисков).</summary>
		private static Reference ReferenceFor(int turn, List<BoardSample> field)
		{
			int boards = BoardLibrary.CountForTurn(turn);
			lock(Lock)
			{
				_references ??= Load();
				if(_references.TryGetValue(turn, out var cached) && boards < cached.Boards * 1.25)
					return cached;
			}
			var strengths = field.Select(b => Strength(b, field.Where(f => f != b).ToList(), turn)).Where(s => s > -Bound + 0.01).ToList();
			if(strengths.Count < 5)
				return null;
			double mean = strengths.Average();
			double sd = Math.Sqrt(strengths.Sum(x => (x - mean) * (x - mean)) / (strengths.Count - 1));
			if(sd <= 0.05)
				return null;
			var reference = new Reference { Mean = mean, Sd = sd, Boards = boards };
			lock(Lock)
			{
				_references[turn] = reference;
				try
				{
					System.IO.File.WriteAllText(File, JsonConvert.SerializeObject(_references, Formatting.Indented));
				}
				catch(Exception ex)
				{
					Paths.Log("Suspicion: эталон не сохранён: " + ex.Message);
				}
			}
			return reference;
		}

		private static Dictionary<int, Reference> Load()
		{
			try
			{
				if(System.IO.File.Exists(File))
					return JsonConvert.DeserializeObject<Dictionary<int, Reference>>(System.IO.File.ReadAllText(File)) ?? new Dictionary<int, Reference>();
			}
			catch(Exception)
			{
				// битый файл — пересчитаем
			}
			return new Dictionary<int, Reference>();
		}

		/// <summary>Двоичный поиск log2 f в [−6, 6]: доля побед стола против поля со статами × f = 50 %.</summary>
		private static double Strength(BoardSample board, List<BoardSample> field, int turn)
		{
			double Share(double log2f)
			{
				double f = Math.Pow(2, log2f), sum = 0;
				int n = 0;
				foreach(var opp in field)
					if(PrecombatEstimator.Duel(board, opp, f, turn, Iterations, Threads) is double s)
					{
						sum += s;
						n++;
					}
				return n == 0 ? 0.5 : sum / n;
			}
			double lo = -Bound, hi = Bound;
			if(Share(hi) > 0.5)
				return hi;
			if(Share(lo) < 0.5)
				return lo;
			for(int step = 0; step < Steps; step++)
			{
				double mid = (lo + hi) / 2;
				if(Share(mid) > 0.5)
					lo = mid;
				else
					hi = mid;
			}
			return (lo + hi) / 2;
		}
	}
}
