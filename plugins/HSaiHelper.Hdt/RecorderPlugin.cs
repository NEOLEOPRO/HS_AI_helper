using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Controls;
using Hearthstone_Deck_Tracker;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Plugins;
using HearthDb.Enums;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Type = System.Type;
using Core = Hearthstone_Deck_Tracker.API.Core;

namespace HSaiHelper.Hdt
{
	/// <summary>
	/// HSAI Recorder: после каждого боя BG сохраняет ровно тот вход, который HDT собрал для BobsBuddy
	/// (столы, руки, безделушки, счётчики «на всю игру»), прогноз BobsBuddy и фактический исход боя.
	/// Пишет JSONL в %APPDATA%\HSaiHelper\recordings\combats_ГГГГ-ММ-ДД.jsonl.
	/// Ничего не меняет в игре и в HDT — только читает.
	/// </summary>
	public class RecorderPlugin : IPlugin
	{
		public string Name => "HSAI Recorder";
		public string Description => L.T(
			$"Версия {Paths.VersionText}. Записывает каждый бой Battlegrounds (вход и прогноз BobsBuddy + фактический исход), таблицу лидеров, гайды сборок и комментарии для проекта HSaiHelper.",
			$"Version {Paths.VersionText}. Records every Battlegrounds combat (BobsBuddy input and odds + actual result), the leaderboard, comp guides and comments for the HSaiHelper project.");
		public string ButtonText => L.T("Открыть папку", "Open folder");
		public string Author => "HSaiHelper";
		public Version Version => Paths.Version;
		public MenuItem MenuItem => null;

		// BobsBuddyInvoker — внутренний класс HDT, поэтому достаём его через reflection.
		private static readonly Type InvokerType = typeof(Core).Assembly.GetType("Hearthstone_Deck_Tracker.BobsBuddy.BobsBuddyInvoker");
		private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
		private static readonly FieldInfo InstancesField = InvokerType?.GetField("_instances", Any);
		private static readonly FieldInfo InputField = InvokerType?.GetField("_input", Any);
		private static readonly FieldInfo TurnField = InvokerType?.GetField("_turn", Any);
		private static readonly PropertyInfo OutputProp = InvokerType?.GetProperty("Output", Any);
		private static readonly PropertyInfo StateProp = InvokerType?.GetProperty("State", Any);
		private static readonly MethodInfo CombatResultMethod = InvokerType?.GetMethod("GetLastCombatResult", Any);
		private static readonly MethodInfo LethalResultMethod = InvokerType?.GetMethod("GetLastLethalResult", Any);

		private readonly HashSet<string> _saved = new HashSet<string>();
		/// <summary>Бои, уже разобранные прямо в бою (Analysis.OnLiveCombat) — ключи боёв HDT уникальны, поэтому без сброса.</summary>
		private readonly HashSet<string> _analyzedLive = new HashSet<string>();
		private bool _enabled;
		private DateTime _lastCheck = DateTime.MinValue;
		private bool _wasInGame;
		private DateTime? _gameEndPendingSince;
		private string _lastHero;
		private int? _lastPlacement;
		private int? _ratingBefore;

		public void OnLoad()
		{
			_enabled = true;
			LogEnvironment();
			GameEvents.OnGameEnd.Add(OnGameEnd);
		}

		/// <summary>
		/// Версии HDT и его библиотек + проверка внутренних полей, на которые опирается плагин.
		/// После обновления HDT первым делом смотреть сюда (%APPDATA%\HSaiHelper\plugin.log).
		/// </summary>
		private static void LogEnvironment()
		{
			string Ver(Type t) => t?.Assembly.GetName().Version?.ToString() ?? "?";
			var missing = new List<string>();
			void Check(object member, string name)
			{
				if(member == null)
					missing.Add(name);
			}
			Check(InvokerType, "BobsBuddyInvoker");
			Check(InstancesField, "_instances");
			Check(InputField, "_input");
			Check(TurnField, "_turn");
			Check(OutputProp, "Output");
			Check(StateProp, "State");
			Check(CombatResultMethod, "GetLastCombatResult");
			Check(LethalResultMethod, "GetLastLethalResult");
			Paths.Log($"Recorder: загружен. HDT {Ver(typeof(Core))}, BobsBuddy {Ver(InputField?.FieldType)}, " +
				$"HearthMirror {Ver(typeof(HearthMirror.Reflection))}, HearthDb {Ver(typeof(HearthDb.Cards))}, плагин {typeof(RecorderPlugin).Assembly.GetName().Version}");
			Paths.Log(missing.Count == 0
				? "Recorder: все внутренние поля HDT найдены — совместимо."
				: "Recorder: НЕ НАЙДЕНЫ " + string.Join(", ", missing) + " — HDT изменился, плагин нужно обновить.");
		}

		public void OnUnload()
		{
			_enabled = false;
			_leaderboard.Dispose();
		}

		// --- Гайды по сборкам (панель HDT «Comp Guides», бесплатная версия — сборки JeefHS) ---

		private string _savedGuidesSignature;
		private object _lastGuidesRefFree;
		private object _lastGuidesRefPremium;

		/// <summary>
		/// HDT грузит гайды сам (ApiWrapper.GetCompsGuides) и держит их в Core.OverlayWindow.BattlegroundsCompsGuidesVM:
		/// Comps — бесплатный список, CompsByTier — премиум. Без таймера: на каждом тике сравниваем ссылки на списки
		/// (дёшево) и сохраняем только когда HDT загрузил новый список. Пишем recordings\comp_guides_ГГГГ-ММ-ДД.json.
		/// </summary>
		private void SaveCompGuides()
		{
			try
			{
				var vm = Core.OverlayWindow?.GetType().GetProperty("BattlegroundsCompsGuidesVM")?.GetValue(Core.OverlayWindow);
				if(vm == null)
					return;
				var freeRef = vm.GetType().GetProperty("Comps")?.GetValue(vm);
				var premiumRef = vm.GetType().GetProperty("CompsByTier")?.GetValue(vm);
				if(ReferenceEquals(freeRef, _lastGuidesRefFree) && ReferenceEquals(premiumRef, _lastGuidesRefPremium))
					return;
				_lastGuidesRefFree = freeRef;
				_lastGuidesRefPremium = premiumRef;
				var namespaces = new[] { "Hearthstone_Deck_Tracker", "HSReplay" };
				var json = new JObject { ["time"] = DateTime.UtcNow, ["language"] = Hearthstone_Deck_Tracker.Helper.GetCardLanguage() };
				foreach(var name in new[] { "Comps", "CompsByTier" })
				{
					var value = vm.GetType().GetProperty(name)?.GetValue(vm);
					if(value != null)
						json[name] = ObjectDump.Dump(new { Items = value }, depth: 4, namespaces)["Items"];
				}
				if(json["Comps"] == null && json["CompsByTier"] == null)
					return;
				var signature = (json["Comps"]?.ToString(Formatting.None) ?? "") + (json["CompsByTier"]?.ToString(Formatting.None) ?? "");
				if(signature == _savedGuidesSignature)
					return;
				_savedGuidesSignature = signature;
				File.WriteAllText(Path.Combine(Paths.Recordings, $"comp_guides_{DateTime.Now:yyyy-MM-dd}.json"), json.ToString(Formatting.Indented));
				CompGuides.Set(json["Comps"] as JArray);
				ActivityFeed.Post(L.T($"Гайды сборок сохранены: {(json["Comps"] as JArray)?.Count ?? 0} шт.", $"Comp guides saved: {(json["Comps"] as JArray)?.Count ?? 0}"));
			}
			catch(Exception ex)
			{
				ActivityFeed.Post(L.T("Гайды сборок: не удалось сохранить (", "Comp guides: could not save (") + ex.Message + ")", ActivityLevel.Warning);
			}
		}

		// --- Таблица лидеров: состав соперников по типам, серии, история боёв всего лобби (из памяти игры, только чтение) ---

		private readonly LeaderboardReader _leaderboard = new LeaderboardReader();
		private Task _lobbyTask;
		private DateTime _lastLobbyRead = DateTime.MinValue;
		private string _lastLobbySignature;
		private readonly HashSet<string> _savedHistory = new HashSet<string>();
		private int _lobbyFailures;
		private Dictionary<int, string> _lastCompositions = new Dictionary<int, string>();

		/// <summary>
		/// Читаем таблицу лидеров в фоне; пишем запись, только если что-то изменилось.
		/// Пауза между чтениями — 4 с, при сбоях растёт (8, 16, … до 60 с) и сбрасывается после успешного чтения.
		/// </summary>
		private void StartLobbyRead()
		{
			var delay = Math.Min(60, 4 * Math.Pow(2, Math.Min(_lobbyFailures, 4)));
			if((DateTime.Now - _lastLobbyRead).TotalSeconds < delay || (_lobbyTask != null && !_lobbyTask.IsCompleted))
				return;
			_lastLobbyRead = DateTime.Now;
			var turn = Core.Game.GetTurnNumber();
			var combatPhase = Core.Game.IsBattlegroundsCombatPhase;
			_lobbyTask = Task.Run(() =>
			{
				var state = _leaderboard.Read();
				if(state == null)
				{
					if(++_lobbyFailures == 1)
						ActivityFeed.Post(L.T("Таблица лидеров: не прочиталась — повторю с паузой", "Leaderboard: read failed — will retry with a pause"), ActivityLevel.Warning);
					return;
				}
				if(_lobbyFailures > 0)
					ActivityFeed.Post(L.T("Таблица лидеров: снова читается", "Leaderboard: reading again"));
				_lobbyFailures = 0;
				var players = state["players"] as JArray;
				LobbyState.Players = players;
				LobbyState.History = state["combat_history"] as JArray;
				if(!combatPhase)
					LobbyState.RecordTrajectory(players, turn);
				PostCompositionChanges(players, combatPhase);
				var signature = players?.ToString(Formatting.None);
				if(signature != _lastLobbySignature && players != null && players.Count > 0)
				{
					_lastLobbySignature = signature;
					Write(new JObject
					{
						["type"] = "lobby_state",
						["time"] = DateTime.UtcNow,
						["turn"] = turn,
						["combat_phase"] = combatPhase,
						["players"] = players,
					}, "lobby");
				}
				// История боёв: только новые записи (ключ — владелец, номер записи, соперник).
				var fresh = new JArray();
				foreach(var rec in state["combat_history"] as JArray ?? new JArray())
				{
					var key = $"{rec["owner_id"]}_{rec["index"]}_{rec["opponentId"]}_{rec["damage"]}";
					if(_savedHistory.Add(key))
						fresh.Add(rec);
				}
				if(fresh.Count > 0)
					Write(new JObject { ["type"] = "combat_history", ["time"] = DateTime.UtcNow, ["turn"] = turn, ["records"] = fresh }, "lobby");
			});
		}

		/// <summary>
		/// В ленту — только смены сборки: основной тип (≥ 3 существ) появился или сменился на другой.
		/// Текущие составы и так видны в таблице «Лобби»; лента — для того, что поменялось.
		/// </summary>
		private void PostCompositionChanges(JArray players, bool combatPhase)
		{
			if(players == null || combatPhase)
				return;
			var changes = new List<string>();
			foreach(var pl in players.OfType<JObject>())
			{
				var id = (int?)pl["player_id"];
				if(id == null || (bool?)pl["dead"] == true)
					continue;
				var races = pl["races"] as JObject;
				var dominant = LobbyState.Dominant(races);
				if(dominant == null)
					continue;
				if(!_lastCompositions.TryGetValue(id.Value, out var old))
					changes.Add($"{LobbyState.HeroName((string)pl["hero"])} {L.T("собирает", "goes")} {LobbyState.Short(dominant)} ({LobbyState.Composition(races)})");
				else if(old != dominant)
					changes.Add($"{LobbyState.HeroName((string)pl["hero"])}: {LobbyState.Short(old)} → {LobbyState.Short(dominant)} ({LobbyState.Composition(races)})");
				_lastCompositions[id.Value] = dominant;
			}
			if(changes.Count > 0)
				ActivityFeed.Post(L.T("Сборки: ", "Builds: ") + string.Join("; ", changes.Take(3)) + (changes.Count > 3 ? L.T($" и ещё {changes.Count - 3}", $" and {changes.Count - 3} more") : ""));
		}

		public void OnButtonPress() => System.Diagnostics.Process.Start(Paths.Recordings);

		public void OnUpdate()
		{
			if(!_enabled || (DateTime.Now - _lastCheck).TotalMilliseconds < 1000)
				return;
			_lastCheck = DateTime.Now;
			try
			{
				if(_gameEndPendingSince.HasValue && (DateTime.Now - _gameEndPendingSince.Value).TotalSeconds >= 5)
					WriteGameEnd();
				if(Core.Game.IsBattlegroundsMatch)
				{
					_wasInGame = true;
					// К концу игры HDT уже сбрасывает героя — запоминаем по ходу партии (запасной вариант для game_end).
					var hero = Core.Game.Player?.Hero;
					if(hero?.CardId != null)
					{
						_lastHero = hero.CardId;
						_lastPlacement = hero.GetTag(GameTag.PLAYER_LEADERBOARD_PLACE);
					}
					_ratingBefore ??= Core.Game.CurrentBattlegroundsRating;
					CollectFinishedCombats(includeCurrent: false);
					// После конца партии таблицы лидеров в игре уже нет — не читаем (иначе ложное «не прочиталась»).
					if(!Core.Game.IsInMenu)
						StartLobbyRead();
					SaveCompGuides();
				}
			}
			catch(Exception ex)
			{
				Paths.Log("Recorder.OnUpdate: " + ex);
			}
		}

		private void OnGameEnd()
		{
			if(!_enabled || !_wasInGame)
				return;
			_wasInGame = false;
			try
			{
				// Последний бой партии заканчивается вместе с игрой — забираем всё, что осталось.
				CollectFinishedCombats(includeCurrent: true);
				// Итог (место, рейтинг после игры) HDT записывает в BgsLastGames.xml чуть позже — пишем game_end через несколько секунд.
				_gameEndPendingSince = DateTime.Now;
			}
			catch(Exception ex)
			{
				Paths.Log("Recorder.OnGameEnd: " + ex);
			}
		}

		private void WriteGameEnd()
		{
			_gameEndPendingSince = null;
			var record = new JObject
			{
				["type"] = "game_end",
				["time"] = DateTime.UtcNow,
				["region"] = Core.Game.CurrentRegion.ToString(),
				["hero"] = _lastHero,
				["placement"] = _lastPlacement,
				["rating"] = _ratingBefore,
			};
			try
			{
				// Последняя партия из истории HDT: герой, место, рейтинг до и после.
				var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HearthstoneDeckTracker", "BgsLastGames.xml");
				var last = System.Xml.Linq.XDocument.Load(file).Root?.Elements("Game")
					.OrderBy(g => (string)g.Attribute("EndTime")).LastOrDefault();
				if(last != null && DateTime.TryParse((string)last.Attribute("EndTime"), out var end) && (DateTime.Now - end).TotalMinutes < 10)
				{
					record["hero"] = (string)last.Attribute("Hero");
					record["placement"] = (int?)last.Attribute("Placemenent"); // так в HDT, с опечаткой
					record["rating"] = (int?)last.Attribute("Rating");
					record["rating_after"] = (int?)last.Attribute("RatingAfter");
					record["duos"] = (string)last.Attribute("Duos") == "true";
				}
			}
			catch(Exception ex)
			{
				Paths.Log("Recorder.WriteGameEnd: не прочитан BgsLastGames.xml: " + ex.Message);
			}
			Write(record);
			ActivityFeed.Post(L.T("Партия окончена: ", "Game over: ") + L.Place(record["placement"] ?? "?") + L.T(" место", " place") +
							  (record["rating_after"] != null ? $", MMR {record["rating"]} → {record["rating_after"]}" : ""));
			_lastCompositions.Clear();
			_lastLobbySignature = null;
			_savedHistory.Clear();
			_lastHero = null;
			_lastPlacement = null;
			_ratingBefore = null;
		}

		private void CollectFinishedCombats(bool includeCurrent)
		{
			if(!(InstancesField?.GetValue(null) is IDictionary instances))
				return;
			foreach(DictionaryEntry entry in instances)
			{
				var key = entry.Key as string;
				var invoker = entry.Value;
				if(key == null || invoker == null || _saved.Contains(key))
					continue;
				var state = StateProp?.GetValue(invoker)?.ToString() ?? "";
				var finished = state.StartsWith("Shopping") || state.StartsWith("GameOver");
				if(!finished && !includeCurrent)
				{
					// Бой ещё идёт, но HDT уже посчитал его — разборы сейчас (исход известен сразу, дальше анимация). В файл пишем после боя, как раньше.
					if(!_analyzedLive.Contains(key) && InputField.GetValue(invoker) is object liveInput && OutputProp.GetValue(invoker) is object liveOutput)
					{
						_analyzedLive.Add(key);
						try
						{
							Analysis.OnLiveCombat(new JObject
							{
								["type"] = "combat", ["key"] = key, ["turn"] = TurnField?.GetValue(invoker) as int?, ["duos"] = Core.Game.IsBattlegroundsDuosMatch,
								["input"] = ObjectDump.Dump(liveInput, depth: 4), ["output"] = ObjectDump.Dump(liveOutput, depth: 0),
							});
						}
						catch(Exception ex)
						{
							Paths.Log("Recorder.Live: " + ex.Message);
						}
					}
					continue;
				}
				var input = InputField.GetValue(invoker);
				var output = OutputProp.GetValue(invoker);
				// Без прогноза — это прошедшие раунды, которые HDT заново проигрывает после перезапуска: они уже записаны (или потеряны), дубли не нужны.
				if(input == null || output == null)
					continue;

				_saved.Add(key);
				var turn = TurnField?.GetValue(invoker) as int?;
				var outJson = ObjectDump.Dump(output, depth: 0);
				var result = SafeInvoke(CombatResultMethod, invoker);
				var pre = turn.HasValue ? PrecombatEstimator.ForTurn(turn.Value) : null;
				ActivityFeed.Post(L.T($"Ход {turn}: {ResultRu(result)}. BobsBuddy в бою: победа {Pct(outJson["winRate"])}, ничья {Pct(outJson["tieRate"])}, поражение {Pct(outJson["lossRate"])}",
									  $"Turn {turn}: {ResultRu(result)}. BobsBuddy in combat: win {Pct(outJson["winRate"])}, tie {Pct(outJson["tieRate"])}, loss {Pct(outJson["lossRate"])}")
								  + (pre != null ? L.T($"; до боя оценивал победу в {pre.Win:P0}", $"; pre-combat estimate: win {pre.Win:P0}") : ""));
				var record = new JObject
				{
					["type"] = "combat",
					["time"] = DateTime.UtcNow,
					["key"] = key,
					["turn"] = TurnField?.GetValue(invoker) as int?,
					["state"] = state,
					["result"] = result,
					["lethal"] = SafeInvoke(LethalResultMethod, invoker),
					["region"] = Core.Game.CurrentRegion.ToString(),
					["rating"] = Core.Game.CurrentBattlegroundsRating,
					["duos"] = Core.Game.IsBattlegroundsDuosMatch,
					["bobsbuddy_version"] = InputField.FieldType.Assembly.GetName().Version.ToString(),
					["input"] = ObjectDump.Dump(input, depth: 4),
					["output"] = outJson,
					// Наша оценка в таверне (до того, как стол соперника стал виден) — чтобы потом мерить её точность.
					["precombat"] = pre == null ? null : new JObject
					{
						["opponent_id"] = pre.OpponentId, ["win"] = pre.Win, ["tie"] = pre.Tie, ["loss"] = pre.Loss,
						["min_win"] = pre.MinWin, ["max_win"] = pre.MaxWin, ["boards"] = pre.Boards,
					},
				};
				Write(record);
				BoardLibrary.Add(record);
				Suspicion.OnCombat(record); // подозрительность стола соперника — боями, в фоне
				Analysis.ExplainCombat(record); // почему шанс до боя разошёлся с прогнозом в бою
				GameReview.AddCombat(record);
			}
		}

		private static string Pct(JToken v) => v == null ? "?" : $"{(double)v * 100:F0}%";

		private static string ResultRu(string r) => r switch
		{
			"Win" => L.T("победа", "win"),
			"Loss" => L.T("поражение", "loss"),
			"Tie" => L.T("ничья", "tie"),
			_ => r ?? "?",
		};

		private static string SafeInvoke(MethodInfo method, object target)
		{
			try
			{
				return method?.Invoke(target, null)?.ToString();
			}
			catch
			{
				return null;
			}
		}

		private static readonly object WriteLock = new object();
		private static readonly List<KeyValuePair<string, string>> PendingWrites = new List<KeyValuePair<string, string>>();
		private const int MaxPending = 500;

		// combats_*.jsonl — бои и итоги партий; lobby_*.jsonl — таблица лидеров (пишется из фонового потока, поэтому под замком).
		// Если диск недоступен (файл занят, нет места) — строки ждут в памяти и дописываются при следующей записи.
		internal static void Write(JObject record, string prefix = "combats")
		{
			var file = Path.Combine(Paths.Recordings, $"{prefix}_{DateTime.Now:yyyy-MM-dd}.jsonl");
			lock(WriteLock)
			{
				PendingWrites.Add(new KeyValuePair<string, string>(file, record.ToString(Formatting.None)));
				try
				{
					foreach(var group in PendingWrites.GroupBy(w => w.Key).ToList())
						File.AppendAllText(group.Key, string.Concat(group.Select(w => w.Value + Environment.NewLine)));
					PendingWrites.Clear();
				}
				catch(Exception ex)
				{
					if(PendingWrites.Count > MaxPending)
						PendingWrites.RemoveRange(0, PendingWrites.Count - MaxPending);
					ActivityFeed.Post(L.T($"Запись на диск не удалась ({ex.Message}) — {PendingWrites.Count} записей ждут повтора",
						$"Disk write failed ({ex.Message}) — {PendingWrites.Count} records waiting to retry"), ActivityLevel.Error);
				}
			}
		}
	}
}
