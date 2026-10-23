using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HSaiHelper.Hdt
{
	/// <summary>Стол из прошлого боя: обе стороны каждого записанного боя (призраки — нет).</summary>
	internal class BoardSample
	{
		public int Turn;
		public int Tier;
		public Dictionary<string, int> Races;
		public int Typed;
		public double Power;
		/// <summary>Существа в виде дампа BobsBuddy (CardID, baseAttack, baseHealth, taunt, div, …).</summary>
		public JArray Minions;
		/// <summary>Счётчики «на всю игру» этой стороны (UndeadAttackBonus, BloodGemAtkBuff, …).</summary>
		public JObject Player;
		/// <summary>Ключ записи боя и сторона — найти стол, только что добавленный из записи (Suspicion).</summary>
		public string Key;
	}

	/// <summary>
	/// Библиотека столов для «шансов до боя»: читается из своих записей Recorder (recordings\combats_*.jsonl) в фоне при запуске
	/// и пополняется после каждого боя. Похожий стол ищется так же, как в tools/precombat_eval.py («диапазон по составу»):
	/// состав по типам (косинус), тир, число существ с типом, раунд ±1 с поправкой статов по кривой роста.
	/// </summary>
	internal static class BoardLibrary
	{
		private static readonly object Lock = new object();
		private static readonly List<BoardSample> Samples = new List<BoardSample>();
		private static Task _loading;

		public static int Count
		{
			get { lock(Lock) return Samples.Count; }
		}

		public static bool Loaded => _loading?.IsCompleted == true;

		public static void EnsureLoaded()
		{
			lock(Lock)
				_loading ??= Task.Run(LoadAll);
		}

		private static void LoadAll()
		{
			int files = 0;
			var own = new HashSet<string>();
			try
			{
				foreach(var file in Directory.GetFiles(Paths.Recordings, "combats_*.jsonl").OrderBy(f => f))
				{
					files++;
					foreach(var line in File.ReadLines(file))
					{
						if(!line.Contains("\"type\":\"combat\""))
							continue;
						try
						{
							var record = JObject.Parse(line);
							own.Add(SeedKey((string)record["key"]) + "|" + (int?)record["turn"]);
							Add(record);
						}
						catch(Exception)
						{
							// битая строка (оборванная запись) — пропускаем
						}
					}
				}
			}
			catch(Exception ex)
			{
				Paths.Log("BoardLibrary: " + ex.Message);
			}
			int mine = Count, seed = LoadSeed(own);
			ActivityFeed.Post(L.T($"Библиотека столов: {mine} своих" + (seed > 0 ? $" + {seed} стартовых" : "") + $" ({files} дн. записей)",
				$"Board library: {mine} own" + (seed > 0 ? $" + {seed} starter" : "") + $" boards ({files} days of recordings)"));
		}

		/// <summary>
		/// Стартовый набор столов рядом с плагином (seed_boards.jsonl.gz, tools/build_seed_boards.py: обезличенные записи проекта) —
		/// чтобы шансы до боя, сила стола и подозрительность работали с первой партии. Свои же записи (тот же ключ) не дублируем.
		/// </summary>
		private static int LoadSeed(HashSet<string> own)
		{
			int before = Count;
			try
			{
				var file = Path.Combine(Path.GetDirectoryName(typeof(BoardLibrary).Assembly.Location) ?? "", "seed_boards.jsonl.gz");
				if(!File.Exists(file))
					return 0;
				using(var gz = new System.IO.Compression.GZipStream(File.OpenRead(file), System.IO.Compression.CompressionMode.Decompress))
				using(var reader = new StreamReader(gz, System.Text.Encoding.UTF8))
				{
					string line;
					while((line = reader.ReadLine()) != null)
					{
						try
						{
							var record = JObject.Parse(line);
							if(!own.Contains((string)record["key"] + "|" + (int?)record["turn"]))
								Add(record);
						}
						catch(Exception)
						{
							// битая строка — пропускаем
						}
					}
				}
			}
			catch(Exception ex)
			{
				Paths.Log("BoardLibrary.Seed: " + ex.Message);
			}
			return Count - before;
		}

		/// <summary>Ключ боя в стартовом наборе: SHA-256, первые 16 hex (как tools/build_seed_boards.py).</summary>
		private static string SeedKey(string key)
		{
			using(var sha = System.Security.Cryptography.SHA256.Create())
			{
				var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key ?? ""));
				return string.Concat(hash.Take(8).Select(b => b.ToString("x2")));
			}
		}

		/// <summary>Запись боя Recorder → два стола (мой и соперника). Дуо пока не берём.</summary>
		public static void Add(JObject record)
		{
			foreach(var side in new[] { "Player", "Opponent" })
				if(MakeSample(record, side) is BoardSample sample)
					lock(Lock)
						Samples.Add(sample);
		}

		/// <summary>
		/// Стол из записи боя, не добавляя в библиотеку (для разборов прямо в бою: HDT считает исход сразу, дальше — анимация).
		/// null — дуо, призрак, пусто.
		/// </summary>
		public static BoardSample MakeSample(JObject record, string side)
		{
			if((bool?)record["duos"] == true || !(record["input"] is JObject input))
				return null;
			int turn = (int?)record["turn"] ?? 0;
			if(turn <= 0)
				return null;
			{
				if(!(input[side] is JObject player) || side == "Opponent" && (bool?)player["HeroIsKelThuzad"] == true)
					return null;
				if(!(player["Side"] is JArray minions) || minions.Count == 0)
					return null;
				// Как в таблице лидеров игры: существо двух типов считается в обоих.
				var races = new Dictionary<string, int>();
				double power = 0;
				foreach(var m in minions)
				{
					power += ((int?)m["baseAttack"] ?? 0) + ((int?)m["baseHealth"] ?? 0);
					foreach(var r in new[] { (string)m["PrimaryRace"], (string)m["SecondaryRace"] })
						if(!string.IsNullOrEmpty(r) && r != "INVALID" && r != "BLANK")
							races[r] = races.TryGetValue(r, out var n) ? n + 1 : 1;
				}
				int typed = races.Where(kv => kv.Key != "ALL").Sum(kv => kv.Value);
				var counters = new JObject(player.Properties().Where(p => p.Value.Type == JTokenType.Integer || p.Value.Type == JTokenType.Boolean));
				return new BoardSample
				{
					Turn = turn, Tier = (int?)player["Tier"] ?? 0, Races = races, Typed = typed, Power = power, Minions = minions, Player = counters,
					Key = $"{(string)record["key"]}|{side}",
				};
			}
		}

		/// <summary>Медиана силы стола (атака + здоровье) по раундам.</summary>
		private static Dictionary<int, double> Curve(List<BoardSample> samples) =>
			samples.GroupBy(s => s.Turn).ToDictionary(g => g.Key, g =>
			{
				var v = g.Select(s => s.Power).OrderBy(x => x).ToList();
				return Math.Max(1, v[v.Count / 2]);
			});

		/// <summary>Во сколько раз статы стола хода fromTurn нужно умножить, чтобы сравнить его с ходом toTurn (по медианам); 1 — нет данных.</summary>
		public static double Factor(int fromTurn, int toTurn)
		{
			List<BoardSample> all;
			lock(Lock)
				all = Samples.ToList();
			var curve = Curve(all);
			return fromTurn == toTurn ? 1 : curve.TryGetValue(toTurn, out var a) && curve.TryGetValue(fromTurn, out var b) && b > 0 ? a / b : 1;
		}

		/// <summary>Медиана силы стола (атака + здоровье) в этом раунде по всем записанным столам; 0 — данных нет.</summary>
		public static double MedianPower(int turn)
		{
			List<BoardSample> all;
			lock(Lock)
				all = Samples.ToList();
			return Curve(all).TryGetValue(turn, out var v) ? v : 0;
		}

		/// <summary>
		/// Насколько стол сильнее типичного для раунда: сила в логарифмах против среднего и разброса записанных столов (рост ≈ ×2 за раунд,
		/// поэтому логарифм), Percentile = Φ(z). По рангу «сильнее всех N» нельзя: при ~16 столах в раунде это 1 случай из 17 (проверено:
		/// 5 из 78 столов), а по z ≥ 2,33 («≈ 99 %») — 1 из 77. Мало столов (меньше 12) — соседние раунды с поправкой по кривой; null — не с чем.
		/// </summary>
		public static (double Percentile, int Count, double Z)? PowerPercentile(int turn, double power)
		{
			if(power <= 0)
				return null;
			List<BoardSample> all;
			lock(Lock)
				all = Samples.ToList();
			var curve = Curve(all);
			for(int window = 0; window <= 1; window++)
			{
				var logs = all.Where(s => Math.Abs(s.Turn - turn) <= window && s.Power > 0)
					.Select(s => Math.Log(s.Turn == turn ? s.Power : curve.TryGetValue(turn, out var a) && curve.TryGetValue(s.Turn, out var b) ? s.Power * a / b : s.Power))
					.ToList();
				if(logs.Count < 12)
					continue;
				double mean = logs.Average();
				double sd = Math.Sqrt(logs.Sum(x => (x - mean) * (x - mean)) / (logs.Count - 1));
				if(sd <= 0)
					return null;
				double z = (Math.Log(power) - mean) / sd;
				return (NormalCdf(z), logs.Count, z);
			}
			return null;
		}

		/// <summary>Функция распределения стандартного нормального закона (приближение Абрамовица — Стиган, точность ~1e-7).</summary>
		private static double NormalCdf(double z)
		{
			double t = 1 / (1 + 0.2316419 * Math.Abs(z));
			double d = 0.3989423 * Math.Exp(-z * z / 2);
			double p = d * t * (0.3193815 + t * (-0.3565638 + t * (1.781478 + t * (-1.821256 + t * 1.330274))));
			return z > 0 ? 1 - p : p;
		}

		/// <summary>Стол из этой записи боя (уже в библиотеке): side — "Player" или "Opponent"; null — нет (дуо, призрак, пусто).</summary>
		public static BoardSample SampleOf(JObject record, string side)
		{
			var key = $"{(string)record["key"]}|{side}";
			lock(Lock)
				return Samples.LastOrDefault(s => s.Key == key);
		}

		public static int CountForTurn(int turn)
		{
			lock(Lock)
				return Samples.Count(s => s.Turn == turn);
		}

		/// <summary>
		/// «Поле» раунда для силы стола: k столов этого раунда (мало — ±1 с поправкой по кривой), равномерно от слабых к сильным
		/// (по статам — только чтобы разброс был, сама сила считается боями).
		/// </summary>
		public static List<(BoardSample Sample, double Factor)> FieldSample(int turn, int k)
		{
			List<BoardSample> all;
			lock(Lock)
				all = Samples.ToList();
			var curve = Curve(all);
			for(int window = 0; window <= 1; window++)
			{
				var pool = all.Where(s => Math.Abs(s.Turn - turn) <= window && s.Power > 0).OrderBy(s => s.Power).ToList();
				if(pool.Count < Math.Min(k, 8) && window == 0)
					continue;
				var picked = new List<(BoardSample, double)>();
				int n = Math.Min(k, pool.Count);
				for(int i = 0; i < n; i++)
				{
					var s = pool[(int)((i + 0.5) * pool.Count / n)];
					picked.Add((s, s.Turn == turn ? 1.0 : curve.TryGetValue(turn, out var a) && curve.TryGetValue(s.Turn, out var b) ? a / b : 1.0));
				}
				return picked;
			}
			return new List<(BoardSample, double)>();
		}

		/// <summary>K самых похожих столов и множитель статов (поправка на разницу в раундах).</summary>
		public static List<(BoardSample Sample, double Factor)> Similar(Dictionary<string, int> target, int tier, int turn, int k)
		{
			List<BoardSample> all;
			lock(Lock)
				all = Samples.ToList();
			var curve = Curve(all);
			int targetTyped = target.Where(kv => kv.Key != "ALL").Sum(kv => kv.Value);
			// Сначала — только тот же тир (тир = золото, ушедшее на повышения, а не на стол).
			// На 9 партиях (precombat_eval.py --tier-mode hard): Брайер 0,465 против 0,517 с тиром «в добавку». Не хватает столов — соседний тир.
			for(int attempt = 0; attempt < 4; attempt++)
			{
				int window = attempt % 2 + 1;
				bool sameTier = attempt < 2;
				var scored = all.Where(s => Math.Abs(s.Turn - turn) <= window && (!sameTier || s.Tier == tier))
					.Select(s => (s, score: Cosine(target, s.Races) + (sameTier ? 0 : 0.3 * (s.Tier == tier ? 1 : 0) + 0.15 * (Math.Abs(s.Tier - tier) == 1 ? 1 : 0))
											 - 0.05 * Math.Abs(s.Typed - targetTyped) - 0.1 * Math.Abs(s.Turn - turn)))
					.OrderByDescending(x => x.score)
					.Take(k)
					.Select(x => (x.s, x.s.Turn == turn ? 1.0
						: curve.TryGetValue(turn, out var a) && curve.TryGetValue(x.s.Turn, out var b) ? a / b : 1.0))
					.ToList();
				if(scored.Count >= Math.Min(2, k))
					return scored;
			}
			return new List<(BoardSample, double)>();
		}

		private static double Cosine(Dictionary<string, int> a, Dictionary<string, int> b)
		{
			double dot = 0, na = 0, nb = 0;
			foreach(var kv in a)
			{
				na += kv.Value * kv.Value;
				if(b.TryGetValue(kv.Key, out var v))
					dot += kv.Value * v;
			}
			foreach(var kv in b)
				nb += kv.Value * kv.Value;
			if(na == 0 && nb == 0)
				return 1;
			return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
		}
	}
}
