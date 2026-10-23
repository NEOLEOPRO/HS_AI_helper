using System;
using HearthDb.Enums;

namespace HSaiHelper.Hdt
{
	/// <summary>
	/// Язык интерфейса плагинов: русский или английский. Каждая надпись задаётся парой прямо в коде — L.T("Лобби", "Lobby"),
	/// так перевод не теряется и не расходится с текстом. Выбор — settings.json → "Language": "auto" | "ru" | "en";
	/// auto — по языку карт в настройках HDT (ruRU → русский, иначе английский).
	/// Журнал разработчика (plugin.log) не переводится.
	/// </summary>
	internal static class L
	{
		[ThreadStatic]
		private static bool _forceEnglish;

		/// <summary>
		/// Собрать текст на английском независимо от языка интерфейса — для ИИ: английский короче в токенах (~на треть)
		/// и маленькие модели лучше рассуждают на нём.
		/// </summary>
		public static T InEnglish<T>(Func<T> build)
		{
			var was = _forceEnglish;
			_forceEnglish = true;
			try
			{
				return build();
			}
			finally
			{
				_forceEnglish = was;
			}
		}

		public static bool En
		{
			get
			{
				if(_forceEnglish)
					return true;
				var lang = Settings.Instance.Language ?? "auto";
				if(lang == "en")
					return true;
				if(lang == "ru")
					return false;
				try
				{
					return Hearthstone_Deck_Tracker.Helper.GetCardLanguage() != "ruRU";
				}
				catch(Exception)
				{
					return false;
				}
			}
		}

		public static string T(string ru, string en) => En ? en : ru;

		/// <summary>Язык названий карт и героев.</summary>
		public static Locale CardLocale => En ? Locale.enUS : Locale.ruRU;

		/// <summary>Порядковое место: «4-е» / «4th».</summary>
		public static string Place(object place) => En ? $"{place}{Suffix(place)}" : $"{place}-е";

		private static string Suffix(object place) => place?.ToString() switch
		{
			"1" => "st",
			"2" => "nd",
			"3" => "rd",
			_ => "th",
		};
	}
}
