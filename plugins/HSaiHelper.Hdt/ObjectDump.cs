using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace HSaiHelper.Hdt
{
	/// <summary>
	/// Превращает объекты BobsBuddy (Input / Player / Minion / ...) в JSON через reflection.
	/// Берём только простые значения (числа, флаги, строки, enum) — так в запись автоматически попадают
	/// все счётчики «на всю игру» и поля существ, даже если BobsBuddy добавит новые.
	/// Вложенность ограничена, чтобы не уйти в циклические ссылки (Simulator и т. п.).
	/// </summary>
	internal static class ObjectDump
	{
		private const BindingFlags Inst = BindingFlags.Public | BindingFlags.Instance;
		private const int MaxItems = 64;

		private static readonly string[] DefaultNamespaces = { "BobsBuddy" };

		/// <param name="namespaces">Вложенные объекты из этих пространств имён раскрываются (по умолчанию — BobsBuddy).</param>
		public static JObject Dump(object obj, int depth = 2, string[] namespaces = null)
		{
			namespaces ??= DefaultNamespaces;
			if(obj == null)
				return null;
			var type = obj.GetType();
			var json = new JObject { ["$type"] = type.Name };

			foreach(var p in type.GetProperties(Inst))
			{
				if(!p.CanRead || p.GetIndexParameters().Length > 0)
					continue;
				Add(json, p.Name, p.PropertyType, () => p.GetValue(obj), depth, namespaces);
			}
			foreach(var f in type.GetFields(Inst))
				Add(json, f.Name, f.FieldType, () => f.GetValue(obj), depth, namespaces);
			return json;
		}

		private static void Add(JObject json, string name, Type declared, Func<object> getter, int depth, string[] namespaces)
		{
			if(json.ContainsKey(name))
				return;
			var t = Nullable.GetUnderlyingType(declared) ?? declared;
			if(typeof(Delegate).IsAssignableFrom(t) || t.FullName?.StartsWith("BobsBuddy.Simulation.Simulator") == true)
				return;

			object value;
			try
			{
				value = getter();
			}
			catch
			{
				return;
			}
			if(value == null)
				return;

			if(IsSimple(t))
			{
				json[name] = t.IsEnum ? new JValue(value.ToString()) : JToken.FromObject(value);
				return;
			}
			if(depth <= 0)
				return;

			if(value is IEnumerable seq && !(value is string))
			{
				var arr = new JArray();
				foreach(var item in seq.Cast<object>().Take(MaxItems))
				{
					if(item == null || item is Delegate)
						continue;
					var it = item.GetType();
					arr.Add(IsSimple(it) ? (it.IsEnum ? new JValue(item.ToString()) : JToken.FromObject(item)) : Dump(item, depth - 1, namespaces));
				}
				if(arr.Count > 0)
					json[name] = arr;
				return;
			}

			// Вложенные сущности BobsBuddy (прикреплённое существо, аномалия и т. п.) — на один уровень глубже.
			if(namespaces.Any(ns => t.Namespace?.StartsWith(ns) == true))
				json[name] = Dump(value, depth - 1, namespaces);
		}

		private static bool IsSimple(Type t) => t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal);
	}
}
