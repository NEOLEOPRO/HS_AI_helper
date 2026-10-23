using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BobsBuddy;
using BobsBuddy.Simulation;
using HearthDb.Enums;
using Newtonsoft.Json.Linq;
using Core = Hearthstone_Deck_Tracker.API.Core;
using SimPlayer = BobsBuddy.Simulation.Player;
using Type = System.Type;

namespace HSaiHelper.Hdt
{
	/// <summary>Оценка «шансы до боя» с конкретным соперником.</summary>
	internal class PrecombatResult
	{
		public int Turn;
		public int OpponentId;
		public double Win, Tie, Loss;
		public double MinWin, MaxWin;
		public int Boards;
		public string Error;
		public bool Running;
	}

	/// <summary>Мой стол против «поля» — записанных столов этого раунда (BobsBuddy), вместо суммы атаки и здоровья.</summary>
	internal class FieldResult
	{
		public int Turn;
		public string Signature;
		public double Win, Tie, Loss;
		/// <summary>Скольких столов поля мой стол чаще побеждает, чем проигрывает им.</summary>
		public int Beats;
		public int Boards;
		public bool Running;
	}

	/// <summary>
	/// «Шансы до боя» в таверне, пока стол соперника не виден: мой текущий стол (ровно так, как его собирает для BobsBuddy сам HDT —
	/// метод SetupInputPlayer на своём экземпляре BobsBuddyInvoker, рабочий экземпляр HDT не трогаем) против K похожих столов
	/// из своих записей (BoardLibrary: тот же состав по типам, тир, раунд ±1 с поправкой статов). Результат — среднее и разброс.
	/// Точность на 5 партиях (tools/precombat_eval.py): Брайер ≈ 0,50 против 0,67 у случайного угадывания и ≈ 0,2 у точного BobsBuddy
	/// в начале боя, — это грубая оценка, на панели так и подписано.
	/// Моя сторона собирается в UI-потоке (HDT читает свои сущности), симуляция — в фоне.
	/// </summary>
	internal static class PrecombatEstimator
	{
		private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
		private static readonly Type InvokerType = typeof(Core).Assembly.GetType("Hearthstone_Deck_Tracker.BobsBuddy.BobsBuddyInvoker");
		private static readonly MethodInfo SetupInputPlayer = InvokerType?.GetMethod("SetupInputPlayer", Any);
		private static readonly FieldInfo GameField = InvokerType?.GetField("_game", Any);
		private static readonly FieldInfo InstancesField = InvokerType?.GetField("_instances", Any);
		private static readonly FieldInfo InputField = InvokerType?.GetField("_input", Any);
		private static object _invoker;

		private static readonly object Lock = new object();
		private static PrecombatResult _current;
		private static readonly Dictionary<int, PrecombatResult> ByTurn = new Dictionary<int, PrecombatResult>();
		private static readonly HashSet<int> PostedTurns = new HashSet<int>();

		public static bool Supported => SetupInputPlayer != null && InvokerType != null;

		public static PrecombatResult Current
		{
			get { lock(Lock) return _current; }
		}

		public static bool Busy
		{
			get { lock(Lock) return _current?.Running == true; }
		}

		/// <summary>Последняя оценка, сделанная в таверне перед боем этого раунда (для записи боя и сравнения после).</summary>
		public static PrecombatResult ForTurn(int turn)
		{
			lock(Lock)
				return ByTurn.TryGetValue(turn, out var r) ? r : null;
		}

		public static void Reset()
		{
			lock(Lock)
			{
				_current = null;
				_field = null;
				ByTurn.Clear();
				PostedTurns.Clear();
			}
		}

		/// <summary>Запуск из UI-потока. opponent — плитка соперника из таблицы лидеров игры, me — моя.</summary>
		public static void Start(int turn, JObject opponent, JObject me, string opponentName, List<Race> lobbyRaces)
		{
			if(!Supported || Busy)
				return;
			int opponentId = (int?)opponent["player_id"] ?? 0;
			var running = new PrecombatResult { Turn = turn, OpponentId = opponentId, Running = true };
			lock(Lock)
				_current = running;

			var settings = Settings.Instance;
			var races = (opponent["races"] as JObject)?.Properties().ToDictionary(p => p.Name, p => (int)p.Value) ?? new Dictionary<string, int>();
			var candidates = BoardLibrary.Similar(races, (int?)opponent["tier"] ?? 1, turn, Math.Max(1, settings.PrecombatBoards));
			if(candidates.Count == 0)
			{
				Finish(running, error: L.T("мало записанных столов для этого хода", "not enough recorded boards for this turn"));
				return;
			}

			// Мою сторону собираем сейчас, в UI-потоке: HDT читает свои сущности.
			var inputs = new List<Input>();
			try
			{
				foreach(var (sample, factor) in candidates)
					inputs.Add(BuildInput(turn, opponent, me, sample, factor, lobbyRaces));
			}
			catch(Exception ex)
			{
				var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
				Paths.Log("Precombat.BuildInput: " + inner);
				Finish(running, error: L.T("не удалось собрать твой стол", "could not read your board"));
				return;
			}

			int iterations = Math.Max(200, settings.PrecombatIterations);
			// Два потока: оценка идёт в таверне, и игре нужен процессор (на 24 потоках 1/4 давала заметную нагрузку).
			int threads = Math.Min(2, Environment.ProcessorCount);
			Task.Run(() =>
			{
				var wins = new List<(double win, double tie, double loss)>();
				foreach(var input in inputs)
				{
					try
					{
						var output = new SimulationRunner().SimulateMultiThreaded(input, iterations, threads, 4000).GetAwaiter().GetResult();
						if(output != null && output.simulationCount > 0)
							wins.Add((output.winRate, output.tieRate, output.lossRate));
					}
					catch(Exception ex)
					{
						Paths.Log("Precombat.Simulate: " + (ex is AggregateException ae ? ae.InnerExceptions.FirstOrDefault()?.Message : ex.Message));
					}
				}
				if(wins.Count == 0)
				{
					Finish(running, error: L.T("симулятор не посчитал эти столы", "the simulator could not run these boards"));
					return;
				}
				running.Win = wins.Average(w => w.win);
				running.Tie = wins.Average(w => w.tie);
				running.Loss = wins.Average(w => w.loss);
				running.MinWin = wins.Min(w => w.win);
				running.MaxWin = wins.Max(w => w.win);
				running.Boards = wins.Count;
				Finish(running, error: null);
				bool first;
				lock(Lock)
					first = PostedTurns.Add(turn);
				if(first)
					ActivityFeed.Post(L.T(
						$"Ход {turn}: шансы до боя с {opponentName} ≈ победа {running.Win:P0}, ничья {running.Tie:P0}, поражение {running.Loss:P0} (по {running.Boards} похожим столам)",
						$"Turn {turn}: pre-combat odds vs {opponentName} ≈ win {running.Win:P0}, tie {running.Tie:P0}, loss {running.Loss:P0} ({running.Boards} similar boards)"));
			});
		}

		private static void Finish(PrecombatResult result, string error)
		{
			result.Error = error;
			result.Running = false;
			lock(Lock)
				if(error == null)
					ByTurn[result.Turn] = result;
		}

		private static Input BuildInput(int turn, JObject opponent, JObject me, BoardSample sample, double factor, List<Race> lobbyRaces) =>
			BuildInput(turn, Hp(opponent), (int?)opponent["damage"] ?? 0, (int?)opponent["tier"] ?? 1, me, sample, factor, lobbyRaces);

		// --- Сила стола против поля ---

		private static FieldResult _field;

		public static FieldResult Field
		{
			get { lock(Lock) return _field; }
		}

		public static bool FieldBusy
		{
			get { lock(Lock) return _field?.Running == true; }
		}

		/// <summary>
		/// Сумма «атака + здоровье» обманывает: нежить с перерождениями, щиты, хрипы (2026-10-23; по 142 записанным боям
		/// столы с ≥ 3 нежити выигрывали заметно чаще, чем «по статам»: р11 340 против 1599 — победа 99,8 %). Поэтому сила стола —
		/// BobsBuddy: мой стол против 12 записанных столов раунда, взятых равномерно от слабых к сильным. Запуск из UI-потока.
		/// </summary>
		public static void StartField(int turn, JObject me, List<Race> lobbyRaces, string signature, int boards = 12, int iterations = 300)
		{
			if(!Supported || Busy || FieldBusy)
				return;
			var running = new FieldResult { Turn = turn, Signature = signature, Running = true };
			var previous = Field;
			lock(Lock)
				_field = running;
			void Fail()
			{
				// оставляем прошлый результат, но с новой подписью — чтобы не перезапускать по кругу
				lock(Lock)
					_field = previous == null ? new FieldResult { Turn = turn, Signature = signature } : new FieldResult
					{
						Turn = previous.Turn, Signature = signature, Win = previous.Win, Tie = previous.Tie, Loss = previous.Loss,
						Beats = previous.Beats, Boards = previous.Boards,
					};
			}
			var samples = BoardLibrary.FieldSample(turn, boards);
			if(samples.Count < 4)
			{
				Fail();
				return;
			}
			var inputs = new List<Input>();
			try
			{
				foreach(var (sample, factor) in samples)
					inputs.Add(BuildInput(turn, 30, 0, sample.Tier, me, sample, factor, lobbyRaces));
			}
			catch(Exception ex)
			{
				Paths.Log("Field.BuildInput: " + (ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex).Message);
				Fail();
				return;
			}
			int threads = Math.Min(2, Environment.ProcessorCount);
			Task.Run(() =>
			{
				var results = new List<(double win, double tie, double loss)>();
				foreach(var input in inputs)
				{
					try
					{
						var output = new SimulationRunner().SimulateMultiThreaded(input, iterations, threads, 3000).GetAwaiter().GetResult();
						if(output != null && output.simulationCount > 0)
							results.Add((output.winRate, output.tieRate, output.lossRate));
					}
					catch(Exception)
					{
						// неподдержанный стол из записи — пропускаем
					}
				}
				if(results.Count < 4)
				{
					Fail();
					return;
				}
				running.Win = results.Average(r => r.win);
				running.Tie = results.Average(r => r.tie);
				running.Loss = results.Average(r => r.loss);
				running.Beats = results.Count(r => r.win > r.loss);
				running.Boards = results.Count;
				running.Running = false;
			});
		}

		private static Input BuildInput(int turn, int opponentHp, int opponentDamage, int opponentTier, JObject me, BoardSample sample, double factor, List<Race> lobbyRaces)
		{
			var simulator = new Simulator();
			var input = new Input();
			var invoker = Invoker();
			InputField?.SetValue(invoker, input); // на случай, если SetupInputPlayer смотрит в _input своего экземпляра
			SetupInputPlayer.Invoke(invoker, new object[] { simulator, Core.Game.Player, input.Player, Core.Game.PlayerEntity, true, false });

			input.Player.Health = Hp(me);
			input.Player.DamageTaken = (int?)me["damage"] ?? 0;
			input.Player.Tier = (int?)me["tier"] ?? 1;
			input.Opponent.Health = opponentHp;
			input.Opponent.DamageTaken = opponentDamage;
			input.Opponent.Tier = opponentTier;
			input.SetTurn(turn);
			input.DamageCap = DamageCap();
			if(lobbyRaces?.Count > 0)
				input.availableRaces = lobbyRaces;
			var hdtInput = LatestHdtInput();
			if(hdtInput != null)
				input.Anomaly = hdtInput.Anomaly;

			CopyCounters(sample.Player, input.Opponent);
			foreach(var m in sample.Minions.OfType<JObject>())
			{
				var minion = CreateMinion(simulator, m, factor);
				if(minion != null)
					input.Opponent.Side.Add(minion);
			}
			return input;
		}

		/// <summary>
		/// Бой двух записанных столов (как tools/BobsSim --field): доля побед a (ничья — половина); статы b × factorB.
		/// Без силы героя и безделушек — только существа и счётчики «на всю игру». Можно из любого потока.
		/// </summary>
		public static double? Duel(BoardSample a, BoardSample b, double factorB, int turn, int iterations, int threads)
		{
			try
			{
				var simulator = new Simulator();
				var input = new Input();
				foreach(var m in a.Minions.OfType<JObject>())
					if(CreateMinion(simulator, m, 1, friendly: true) is Minion x)
						input.Player.Side.Add(x);
				foreach(var m in b.Minions.OfType<JObject>())
					if(CreateMinion(simulator, m, factorB) is Minion y)
						input.Opponent.Side.Add(y);
				CopyCounters(a.Player, input.Player);
				CopyCounters(b.Player, input.Opponent);
				input.Player.Health = input.Opponent.Health = 30;
				input.Player.Tier = a.Tier;
				input.Opponent.Tier = b.Tier;
				input.SetTurn(turn);
				var o = new SimulationRunner().SimulateMultiThreaded(input, iterations, threads, 4000).GetAwaiter().GetResult();
				return o == null || o.simulationCount == 0 ? (double?)null : o.winRate + 0.5 * o.tieRate;
			}
			catch(Exception)
			{
				return null; // неподдержанное сочетание — пропускаем
			}
		}

		/// <summary>Свой экземпляр BobsBuddyInvoker — только чтобы вызвать SetupInputPlayer; в список экземпляров HDT он не попадает.</summary>
		private static object Invoker()
		{
			if(_invoker == null)
			{
				_invoker = Activator.CreateInstance(InvokerType, Any, null, new object[] { "hsai_precombat" }, null);
				if(GameField != null && GameField.GetValue(_invoker) == null)
					GameField.SetValue(_invoker, Core.Game);
			}
			return _invoker;
		}

		private static Input LatestHdtInput()
		{
			try
			{
				if(InstancesField?.GetValue(null) is IDictionary instances)
					return instances.Values.Cast<object>().Where(i => i != _invoker)
						.Select(i => InputField?.GetValue(i) as Input).LastOrDefault(i => i != null);
			}
			catch(Exception)
			{
				// список меняется в другом потоке — обойдёмся без аномалии
			}
			return null;
		}

		private static int Hp(JObject tile) => ((int?)tile["health"] ?? 0) - ((int?)tile["damage"] ?? 0) + ((int?)tile["armor"] ?? 0);

		private static int DamageCap()
		{
			var game = Core.Game.GameEntity;
			if(game == null || !Enum.TryParse("BACON_COMBAT_DAMAGE_CAP_ENABLED", out GameTag enabled) || !Enum.TryParse("BACON_COMBAT_DAMAGE_CAP", out GameTag cap))
				return 0;
			return game.GetTag(enabled) == 1 ? game.GetTag(cap) : 0;
		}

		private static readonly HashSet<string> NotCounters = new HashSet<string> { "Health", "DamageTaken", "Tier", "HeroIsKelThuzad" };

		/// <summary>Счётчики «на всю игру» стороны из записи (UndeadAttackBonus, BloodGemAtkBuff, …) — по именам полей BobsBuddy.Player.</summary>
		private static void CopyCounters(JObject source, SimPlayer target)
		{
			foreach(var p in source.Properties())
			{
				if(NotCounters.Contains(p.Name) || p.Value.Type != JTokenType.Integer)
					continue;
				try
				{
					var field = typeof(SimPlayer).GetField(p.Name, BindingFlags.Public | BindingFlags.Instance);
					if(field != null && field.FieldType == typeof(int))
						field.SetValue(target, (int)p.Value);
					else if(typeof(SimPlayer).GetProperty(p.Name, BindingFlags.Public | BindingFlags.Instance) is PropertyInfo prop
							&& prop.CanWrite && prop.PropertyType == typeof(int))
						prop.SetValue(target, (int)p.Value);
				}
				catch(Exception)
				{
					// поле изменилось в новой версии BobsBuddy — пропускаем
				}
			}
		}

		/// <summary>Существо из дампа BobsBuddy (как в tools/BobsSim), статы × factor для стола из соседнего раунда.</summary>
		private static Minion CreateMinion(Simulator simulator, JObject m, double factor, bool friendly = false)
		{
			var cardId = (string)m["CardID"];
			if(string.IsNullOrEmpty(cardId))
				return null;
			var minion = simulator.MinionFactory.CreateFromCardId(cardId, friendly);
			int Scale(string key) => Math.Max(1, (int)Math.Round(((int?)m[key] ?? 1) * factor));
			minion.baseAttack = Scale("baseAttack");
			minion.baseHealth = Scale("baseHealth");
			minion.maxAttack = minion.baseAttack;
			minion.maxHealth = Math.Max(minion.baseHealth, Scale("maxHealth"));
			minion.taunt = (bool?)m["taunt"] == true;
			minion.div = (int?)m["div"] ?? 0;
			minion.cleave = (bool?)m["cleave"] == true;
			minion.poisonous = (bool?)m["poisonous"] == true;
			minion.venomous = (bool?)m["venomous"] == true;
			minion.windfury = (bool?)m["windfury"] == true;
			minion.megaWindfury = (bool?)m["megaWindfury"] == true;
			minion.stealth = (bool?)m["stealth"] == true;
			minion.golden = (bool?)m["golden"] == true;
			minion.reborn = (bool?)m["reborn"] == true;
			minion.tier = (int?)m["tier"] ?? 0;
			minion.ScriptDataNum1 = (int?)m["ScriptDataNum1"] ?? 0;
			minion.ScriptDataNum2 = (int?)m["ScriptDataNum2"] ?? 0;
			minion.ScriptDataNum3 = (int?)m["ScriptDataNum3"] ?? 0;
			minion.ScriptDataNum4 = (int?)m["ScriptDataNum4"] ?? 0;
			if(Enum.TryParse((string)m["PrimaryRace"], out Race race) && race != Race.INVALID)
				minion.PrimaryRace = race;
			return minion;
		}
	}
}
