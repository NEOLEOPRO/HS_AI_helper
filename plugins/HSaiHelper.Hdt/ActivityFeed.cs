using System;
using System.Collections.Generic;
using System.Linq;

namespace HSaiHelper.Hdt
{
	internal enum ActivityLevel
	{
		Info,
		Warning,
		Error,
		/// <summary>Комментарий игрока (поле ввода на панели).</summary>
		Comment,
	}

	internal class ActivityItem
	{
		public DateTime Time { get; set; }
		public string Text { get; set; }
		public ActivityLevel Level { get; set; }
		public int Repeats { get; set; } = 1;
	}

	/// <summary>
	/// Лента «Что делает ИИ» для панели на оверлее: последние события плагинов (записан бой, сменился состав соперника,
	/// грузится таблица лидеров, ошибка связи и повтор). Потокобезопасна: писать можно из фоновых задач.
	/// Одинаковые события подряд не дублируются, а получают счётчик повторов.
	/// </summary>
	internal static class ActivityFeed
	{
		private const int MaxItems = 30;
		private static readonly List<ActivityItem> Items = new List<ActivityItem>();
		private static readonly object Lock = new object();

		/// <summary>Вызывается из любого потока; подписчик сам переключается на UI-поток.</summary>
		public static event Action Changed;

		public static void Post(string text, ActivityLevel level = ActivityLevel.Info)
		{
			lock(Lock)
			{
				var last = Items.LastOrDefault();
				if(last != null && last.Text == text && last.Level == level)
				{
					last.Repeats++;
					last.Time = DateTime.Now;
				}
				else
				{
					Items.Add(new ActivityItem { Time = DateTime.Now, Text = text, Level = level });
					if(Items.Count > MaxItems)
						Items.RemoveAt(0);
				}
			}
			if(level != ActivityLevel.Info)
				Paths.Log("Лента: " + text);
			Changed?.Invoke();
		}

		/// <summary>Последние события, новые — первыми.</summary>
		public static List<ActivityItem> Latest(int count)
		{
			lock(Lock)
				return Items.AsEnumerable().Reverse().Take(count).Select(i => new ActivityItem
				{
					Time = i.Time, Text = i.Text, Level = i.Level, Repeats = i.Repeats,
				}).ToList();
		}
	}
}
