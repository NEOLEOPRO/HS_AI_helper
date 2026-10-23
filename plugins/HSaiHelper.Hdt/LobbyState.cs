using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace HSaiHelper.Hdt
{
	/// <summary>
	/// Последний снимок таблицы лидеров (его читает Recorder) — общий для плагинов: панель «Лобби» показывает
	/// по нему текущий состав соперников. Плюс короткие подписи типов существ для интерфейса.
	/// </summary>
	internal static class LobbyState
	{
		private static readonly object Lock = new object();
		private static JArray _players;
		private static JArray _history;

		public static JArray Players
		{
			get { lock(Lock) return _players; }
			set { lock(Lock) _players = value; }
		}

		/// <summary>
		/// История боёв всего лобби (RecentCombatInfo): owner_id, opponentId, index (= раунд − 1), damage,
		/// damageTarget (кто получил урон; 100000 — ничья), isDefeated (соперник выбит). Проверено по партии 5.
		/// </summary>
		public static JArray History
		{
			get { lock(Lock) return _history; }
			set { lock(Lock) _history = value; }
		}

		public static void Clear()
		{
			lock(Lock)
			{
				_players = null;
				_history = null;
				Trajectories.Clear();
				SeenBoards.Clear();
			}
		}

		// --- Динамика: состав каждого игрока по раундам (последний снимок таверны в раунде) ---

		private static readonly Dictionary<int, SortedDictionary<int, JObject>> Trajectories = new Dictionary<int, SortedDictionary<int, JObject>>();

		/// <summary>Вызывается Recorder после каждого чтения таблицы лидеров в фазе таверны.</summary>
		public static void RecordTrajectory(JArray players, int turn)
		{
			if(players == null || turn <= 0)
				return;
			lock(Lock)
				foreach(var p in players.OfType<JObject>())
				{
					if(!(p["player_id"]?.Type == JTokenType.Integer) || !(p["races"] is JObject races))
						continue;
					int id = (int)p["player_id"];
					if(!Trajectories.TryGetValue(id, out var byTurn))
						Trajectories[id] = byTurn = new SortedDictionary<int, JObject>();
					byTurn[turn] = (JObject)races.DeepClone();
				}
		}

		/// <summary>«р3 драк2 › р5 драк3 пират1 › р7 аберр6»: только раунды, где состав менялся, последние max штук.</summary>
		public static string TrajectoryText(int playerId, int max = 6)
		{
			List<KeyValuePair<int, JObject>> points;
			lock(Lock)
			{
				if(!Trajectories.TryGetValue(playerId, out var byTurn))
					return null;
				points = byTurn.ToList();
			}
			var steps = new List<string>();
			string last = null;
			foreach(var kv in points)
			{
				var comp = Composition(kv.Value);
				if(comp == last || comp == "—")
					continue;
				last = comp;
				steps.Add(L.T($"ход {kv.Key} ", $"turn {kv.Key} ") + comp);
			}
			return steps.Count == 0 ? null : string.Join(" › ", steps.Skip(Math.Max(0, steps.Count - max)));
		}

		// --- Столы соперников, которые видели в бою с нами (карты — для совпадения с гайдами) ---

		private static readonly Dictionary<int, KeyValuePair<int, HashSet<string>>> SeenBoards = new Dictionary<int, KeyValuePair<int, HashSet<string>>>();

		public static void AddSeen(int playerId, int turn, IEnumerable<string> cardIds)
		{
			lock(Lock)
			{
				if(!SeenBoards.TryGetValue(playerId, out var seen) || seen.Key != turn)
					SeenBoards[playerId] = seen = new KeyValuePair<int, HashSet<string>>(turn, new HashSet<string>());
				seen.Value.UnionWith(cardIds.Where(c => !string.IsNullOrEmpty(c)));
			}
		}

		// --- Сила увиденного стола против записанных столов того же раунда (проверка «необычно сильный стол») ---

		/// <summary>Все карты, увиденные на столах соперников (последний увиденный стол каждого), — для оценки пула.</summary>
		public static List<string> AllSeenCards()
		{
			lock(Lock)
				return SeenBoards.Values.SelectMany(s => s.Value).ToList();
		}

		/// <summary>Карты последних увиденных столов всех, кроме игрока playerId (сложность сбора его стола).</summary>
		public static List<string> AllSeenCardsExcept(int playerId)
		{
			lock(Lock)
				return SeenBoards.Where(kv => kv.Key != playerId).SelectMany(kv => kv.Value.Value).ToList();
		}

		/// <summary>Раунд и карты последнего увиденного стола игрока; null — не видели.</summary>
		public static KeyValuePair<int, List<string>>? Seen(int playerId)
		{
			lock(Lock)
				return SeenBoards.TryGetValue(playerId, out var s) ? new KeyValuePair<int, List<string>>(s.Key, s.Value.ToList()) : (KeyValuePair<int, List<string>>?)null;
		}

		/// <summary>Основной тип сборки: самый многочисленный, если существ этого типа хотя бы 3; иначе null.</summary>
		public static string Dominant(JObject races)
		{
			var top = races?.Properties().Where(p => p.Name != "ALL").OrderByDescending(p => (int)p.Value).FirstOrDefault();
			return top != null && (int)top.Value >= 3 ? top.Name : null;
		}

		private static readonly Dictionary<string, string> RaceShort = new Dictionary<string, string>
		{
			["MECHANICAL"] = "мех", ["QUILBOAR"] = "свин", ["DRAGON"] = "драк", ["ELEMENTAL"] = "элем", ["ABERRATION"] = "аберр",
			["MURLOC"] = "мурл", ["DEMON"] = "демон", ["PIRATE"] = "пират", ["BEAST"] = "зверь", ["UNDEAD"] = "нежить",
			["NAGA"] = "нага", ["ALL"] = "все",
		};

		/// <summary>Имя героя по-русски; полное — для таблицы, без титула после запятой («Торим, владыка бури» → «Торим»).</summary>
		public static string HeroName(string cardId)
		{
			if(cardId == null || !HearthDb.Cards.All.TryGetValue(cardId, out var card))
				return cardId ?? "?";
			var name = card.GetLocName(L.CardLocale) ?? card.Name ?? cardId;
			var comma = name.IndexOf(',');
			return comma > 0 ? name.Substring(0, comma) : name;
		}

		private static readonly Dictionary<string, string> RaceShortEn = new Dictionary<string, string>
		{
			["MECHANICAL"] = "mech", ["QUILBOAR"] = "quil", ["DRAGON"] = "drag", ["ELEMENTAL"] = "elem", ["ABERRATION"] = "aber",
			["MURLOC"] = "murl", ["DEMON"] = "demon", ["PIRATE"] = "pirate", ["BEAST"] = "beast", ["UNDEAD"] = "undead",
			["NAGA"] = "naga", ["ALL"] = "all",
		};

		public static string Short(string race) => (L.En ? RaceShortEn : RaceShort).TryGetValue(race ?? "", out var s) ? s : race?.ToLowerInvariant();

		/// <summary>«мех6 свин1» — типы по убыванию количества.</summary>
		public static string Composition(JObject races) =>
			races == null || !races.HasValues
				? "—"
				: string.Join(" ", races.Properties().OrderByDescending(p => (int)p.Value).Select(p => $"{Short(p.Name)}{(int)p.Value}"));
	}

	internal enum RowStyle { Normal, Me, Next, Dead }

	/// <summary>Строка таблицы «Лобби»: игрок по месту.</summary>
	internal class LobbyRow
	{
		public int PlayerId;
		public string Place, Hero, Tier, Hp, Comp, Streak, Mmr, Top4;

		public RowStyle Style;
		/// <summary>Подробности по клику: динамика состава, предполагаемые сборки, увиденный стол.</summary>
		public List<string> Details = new List<string>();
	}

	/// <summary>Что показать в разделе «Лобби»: шапка, таблица игроков, занятость пула, блок о следующем сопернике.</summary>
	internal class LobbyView
	{
		public List<string> Info = new List<string>();
		public List<LobbyRow> Rows = new List<LobbyRow>();
		public string Pool;
		public List<string> Next = new List<string>();
		/// <summary>Из блока соперника — то, чего нет в строках игроков (прошлые бои, соперник через ход): для сводки ИИ.</summary>
		public List<string> NextBrief = new List<string>();
		/// <summary>Строка «шансы до боя» — выделяется на панели.</summary>
		public string Odds;
		public string OddsNote;
	}
}
