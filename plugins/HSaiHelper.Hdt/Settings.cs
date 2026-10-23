using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace HSaiHelper.Hdt
{
	/// <summary>
	/// Настройки панели на оверлее: позиция и свёрнутые разделы. Файл %APPDATA%\HSaiHelper\settings.json.
	/// Любая ошибка чтения/записи — работаем с настройками по умолчанию, HDT не страдает.
	/// </summary>
	internal class Settings
	{
		public double Left { get; set; } = 10;
		public double Top { get; set; } = 140;
		public bool PanelCollapsed { get; set; }
		public bool LobbyCollapsed { get; set; }
		public bool ActivityCollapsed { get; set; }
		public bool CommentsCollapsed { get; set; }
		public bool EstimatesCollapsed { get; set; }
		public bool PoolCollapsed { get; set; }
		public bool ReviewCollapsed { get; set; }
		public bool HelpCollapsed { get; set; } = true;

		/// <summary>Копий каждого существа в общем пуле по тирам 1..7: hearthstone.wiki.gg/wiki/Battlegrounds (подтверждено Blizzard 2024-03-10). Дуо — своя таблица.</summary>
		public int[] PoolCopies { get; set; } = { 15, 15, 13, 11, 9, 7, 5 };

		/// <summary>Локальный ИИ-сервер llama.cpp (OpenAI-совместимый API), см. llm/start_server.ps1.</summary>
		public string AiUrl { get; set; } = "http://127.0.0.1:8091";

		/// <summary>Сам запускать локальный ИИ при входе в BG, если он установлен (AiServer).</summary>
		public bool AiAutoStart { get; set; } = true;

		/// <summary>Файл модели в llm\models (пусто — лучшая из установленных: Gemma-4-26B-A4B → 12B → E4B).</summary>
		public string AiModelFile { get; set; } = "";

		/// <summary>Папка llm проекта (пусто — из llm_dir.txt рядом с плагином, его пишет install.ps1).</summary>
		public string LlmDir { get; set; } = "";

		/// <summary>Язык ответов ИИ: ru — по умолчанию (1.2.0), en — быстрее (русский текст длиннее в токенах). Запрос к модели всегда на английском.</summary>
		public string AiAnswerLanguage { get; set; } = "ru";

		/// <summary>Горячая клавиша «свернуть/развернуть все окна HSAI» (глобальная, игра её не получает): например Ctrl+Shift+H; пусто — выключена.</summary>
		public string CollapseHotkey { get; set; } = "Ctrl+Shift+H";

		/// <summary>Показывать ход мыслей ИИ (модель рассуждает перед ответом — дольше на 10–30 с).</summary>
		public bool AiShowReasoning { get; set; }

		/// <summary>Советы ИИ каждый ход (окно «ИИ»): после боя, когда таверна устоялась, — набор действий с шансами (считает BobsBuddy).</summary>
		public bool AiAutoAdvice { get; set; }

		/// <summary>Язык интерфейса: auto (по языку карт HDT), ru, en.</summary>
		public string Language { get; set; } = "auto";

		/// <summary>Шансы до боя по похожим столам из своих записей: сколько похожих столов брать и сколько боёв считать на каждый.</summary>
		/// <summary>8 столов поровну: на 9 партиях Брайер 0,426 против 0,465 у 4 столов (precombat_eval.py --range-size 8); веса по сходству — хуже.</summary>
		public int PrecombatBoards { get; set; } = 16;
		public int PrecombatIterations { get; set; } = 1000;

		/// <summary>Версия формата настроек — для разовых обновлений значений по умолчанию у тех, у кого файл уже есть.</summary>
		public int SettingsVersion { get; set; }

		private const int CurrentSettingsVersion = 5;

		/// <summary>Разовые обновления старых файлов настроек.</summary>
		private void Upgrade()
		{
			if(SettingsVersion < 2)
			{
				PrecombatBoards = 8;
				PrecombatIterations = 1000;
			}
			// v3 (0.11.2): окна без перетаскивания расставляет раскладка; место «Лобби» 10,140 досталось ему автоматически от старой панели — сбросить.
			if(SettingsVersion < 3 && Panels.TryGetValue("lobby", out var lobby) && lobby.Left == 10 && lobby.Top == 140)
			{
				lobby.Left = null;
				lobby.Top = null;
			}
			// v4 (1.2.0): ответы ИИ по-русски по умолчанию (раньше по умолчанию был английский — его никто не выбирал сам).
			if(SettingsVersion < 4)
			{
				AiAnswerLanguage = "ru";
				// Копий в пуле: т1 — 15, не 16 (вики Hearthstone, подтверждено разработчиком Blizzard 2024-03-10).
				if(PoolCopies != null && PoolCopies.Length > 0 && PoolCopies[0] == 16)
					PoolCopies[0] = 15;
			}
			// v5 (1.2.2): 16 похожих столов вместо 8 — на 14 партиях (133 боя) Брайер 0,444 против 0,445, 32 — уже хуже (0,462);
			// при той же точности оценка меньше зависит от случайного выбора столов. Считается, когда стол 2 с без изменений.
			if(SettingsVersion < 5 && PrecombatBoards == 8)
				PrecombatBoards = 16;
			if(SettingsVersion != CurrentSettingsVersion)
			{
				SettingsVersion = CurrentSettingsVersion;
				Save();
			}
		}

		/// <summary>Размер окон HSAI, % (ползунок в настройках, как масштаб оверлея в HDT); умножается на автоподгонку по ширине экрана.</summary>
		public int PanelScale { get; set; } = 100;

		/// <summary>Случайная «соль» этой установки — для обезличенных id партий в выгрузке (DataExport); к человеку не привязана.</summary>
		public string InstallSalt { get; set; } = "";

		/// <summary>Окна на оверлее: lobby, next, estimates, ai → положение и свёрнутость.</summary>
		public Dictionary<string, PanelState> Panels { get; set; } = new Dictionary<string, PanelState>();

		public PanelState Panel(string key)
		{
			if(!Panels.TryGetValue(key, out var state))
			{
				// Без сохранённого места окно стоит там, где его ставит раскладка (HsaiOverlay.Layout).
				state = new PanelState();
				Panels[key] = state;
			}
			return state;
		}

		/// <summary>«Вернуть окна на места» в настройках.</summary>
		public void ResetPanels()
		{
			Panels = new Dictionary<string, PanelState>();
			Save();
		}

		private static string FilePath => Path.Combine(Paths.Root, "settings.json");

		private static Settings _instance;

		public static Settings Instance
		{
			get
			{
				if(_instance == null)
				{
					_instance = Load();
					_instance.Upgrade();
				}
				return _instance;
			}
		}

		private static Settings Load()
		{
			try
			{
				if(File.Exists(FilePath))
					return JsonConvert.DeserializeObject<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
			}
			catch(Exception ex)
			{
				Paths.Log("Settings: не прочитаны, беру по умолчанию: " + ex.Message);
			}
			return new Settings();
		}

		public void Save()
		{
			try
			{
				File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
			}
			catch(Exception ex)
			{
				Paths.Log("Settings: не сохранены: " + ex.Message);
			}
		}
	}
}
