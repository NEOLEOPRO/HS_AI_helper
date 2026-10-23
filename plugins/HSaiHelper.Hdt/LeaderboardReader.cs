using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security;
using HearthDb.Enums;
using Newtonsoft.Json.Linq;
using ScryDotNet;

namespace HSaiHelper.Hdt
{
	/// <summary>
	/// Таблица лидеров BG из памяти игры — ТОЛЬКО ЧТЕНИЕ, через untapped-scry-dotnet.dll из поставки HDT (как HDT_BGrank).
	/// Пути найдены разведчиком (tools/LeaderboardProbe):
	///   PlayerLeaderboardManager.s_instance
	///     .m_teams[*] (PlayerLeaderboardSoloTeam: m_teamId = номер игрока) .m_playerLeaderboardCards[*] (плитка)
	///       плитка.m_entity (герой: m_cardIdInternal, m_realTimePlayerLeaderboardPlace/TechLevel/Health/Damage/Armor), .m_isNextOpponent, .m_dead
	///       плитка.m_overlay.m_raceCounts (Map: TAG_RACE → количество существ этого типа на столе)
	///     .m_currentWinStreak / .m_currentLoseStreak (Dictionary: номер игрока → серия)
	///     .m_combatHistory (Map: номер игрока → List&lt;RecentCombatInfo&gt;: opponentId, damage, damageTarget, winStreak, loseStreak, …)
	/// Обращаемся только к полям из getFields(): обращение к несуществующему полю роняет библиотеку.
	/// Данные игры меняются во время чтения → ошибки доступа к памяти перехватываются (HandleProcessCorruptedStateExceptions).
	/// </summary>
	internal class LeaderboardReader : IDisposable
	{
		private MonoScry _view;
		private MonoImage _image;
		private int _pid;

		[HandleProcessCorruptedStateExceptions, SecurityCritical]
		public JObject Read()
		{
			try
			{
				var mgr = GetManager();
				if(mgr == null)
					return null;
				var winStreak = ReadIntDictionary(F(mgr, "m_currentWinStreak") as MonoObject);
				var loseStreak = ReadIntDictionary(F(mgr, "m_currentLoseStreak") as MonoObject);

				var players = new JArray();
				foreach(var team in ListItems(F(mgr, "m_teams")))
				{
					var teamId = F(team, "m_teamId") as int?;
					foreach(var tile in ListItems(F(team, "m_playerLeaderboardCards")))
					{
						var entity = F(tile, "m_entity");
						var races = new JObject();
						foreach(var kv in ReadMap(F(F(tile, "m_overlay"), "m_raceCounts") as MonoObject))
						{
							var raceId = kv.Key is MonoStruct st ? F(st, "value__") as int? : kv.Key as int?;
							if(raceId.HasValue)
								races[Enum.GetName(typeof(Race), raceId.Value) ?? raceId.Value.ToString()] = kv.Value as int? ?? 0;
						}
						players.Add(new JObject
						{
							["player_id"] = teamId,
							["hero"] = F(entity, "m_cardIdInternal") as string,
							["place"] = F(entity, "m_realTimePlayerLeaderboardPlace") as int?,
							["tier"] = F(entity, "m_realTimePlayerTechLevel") as int?,
							["health"] = F(entity, "m_realTimeHealth") as int?,
							["damage"] = F(entity, "m_realTimeDamage") as int?,
							["armor"] = F(entity, "m_realTimeArmor") as int?,
							["next_opponent"] = F(tile, "m_isNextOpponent") as bool?,
							["dead"] = F(tile, "m_dead") as bool?,
							["races"] = races,
							["win_streak"] = teamId.HasValue && winStreak.TryGetValue(teamId.Value, out var ws) ? ws : (int?)null,
							["lose_streak"] = teamId.HasValue && loseStreak.TryGetValue(teamId.Value, out var ls) ? ls : (int?)null,
						});
					}
				}

				var history = new JArray();
				foreach(var kv in ReadMap(F(mgr, "m_combatHistory") as MonoObject))
				{
					int index = 0;
					foreach(var rec in ListItems(kv.Value))
					{
						var fields = Fields(rec);
						if(fields == null)
							continue;
						var item = new JObject { ["owner_id"] = kv.Key as int?, ["index"] = index++ };
						foreach(var f in fields)
							if(f.Value is int || f.Value is bool)
								item[f.Key] = JToken.FromObject(f.Value);
						history.Add(item);
					}
				}
				return new JObject { ["players"] = players, ["combat_history"] = history };
			}
			catch(Exception ex)
			{
				Paths.Log("LeaderboardReader: " + ex.GetType().Name + ": " + ex.Message);
				Reset();
				return null;
			}
		}

		private MonoObject GetManager()
		{
			var proc = Process.GetProcessesByName("Hearthstone").FirstOrDefault();
			if(proc == null)
			{
				Reset();
				return null;
			}
			if(_view == null || _pid != proc.Id)
			{
				Reset();
				_pid = proc.Id;
				_view = new MonoScry(Scry.connect(proc.Id));
				try
				{
					_image = _view.getImage();
				}
				catch
				{
					_image = _view.getImage(new List<string> { "Blizzard.T5.ServiceLocator" }, "2021.3.25.61228");
				}
			}
			var cls = _image?["PlayerLeaderboardManager"];
			if(cls == null)
				return null;
			var fields = cls.getFields();
			return fields.TryGetValue("s_instance", out var field) ? field.getStaticValue() as MonoObject : null;
		}

		private static Dictionary<string, object> Fields(object obj)
		{
			if(obj is MonoObject mo)
				return mo.getFields();
			if(obj is MonoStruct ms)
				return ms.getFields();
			return null;
		}

		private static object F(object obj, string name)
		{
			var fields = obj == null ? null : Fields(obj);
			return fields != null && fields.TryGetValue(name, out var v) ? v : null;
		}

		/// <summary>Элементы List&lt;T&gt;: _items (MonoArray) в пределах _size.</summary>
		private static IEnumerable<object> ListItems(object list)
		{
			if(!(F(list, "_items") is MonoArray items))
				yield break;
			var size = F(list, "_size") as int? ?? 0;
			for(uint i = 0; i < size && i < items.size(); i++)
			{
				var item = items[i];
				if(item != null)
					yield return item;
			}
		}

		/// <summary>Blizzard.T5.Core.Map: keySlots/valueSlots в пределах touchedSlots.</summary>
		private static IEnumerable<KeyValuePair<object, object>> ReadMap(MonoObject map)
		{
			if(map == null || !(F(map, "keySlots") is MonoArray keys) || !(F(map, "valueSlots") is MonoArray values))
				yield break;
			var count = F(map, "touchedSlots") as int? ?? 0;
			for(uint i = 0; i < count && i < keys.size() && i < values.size(); i++)
				yield return new KeyValuePair<object, object>(keys[i], values[i]);
		}

		/// <summary>System.Collections.Generic.Dictionary&lt;int,int&gt;: _entries в пределах _count.</summary>
		private static Dictionary<int, int> ReadIntDictionary(MonoObject dict)
		{
			var result = new Dictionary<int, int>();
			if(dict == null || !(F(dict, "_entries") is MonoArray entries))
				return result;
			var count = F(dict, "_count") as int? ?? 0;
			for(uint i = 0; i < count && i < entries.size(); i++)
			{
				var entry = entries[i];
				if(F(entry, "key") is int k && F(entry, "value") is int v)
					result[k] = v;
			}
			return result;
		}

		private void Reset()
		{
			try
			{
				_view?.Dispose();
			}
			catch
			{
				// Соединение уже недействительно.
			}
			_view = null;
			_image = null;
		}

		public void Dispose() => Reset();
	}
}
