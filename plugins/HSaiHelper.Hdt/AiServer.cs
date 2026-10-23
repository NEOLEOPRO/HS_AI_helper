using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HSaiHelper.Hdt
{
	internal enum AiServerState { Unknown, NotInstalled, Starting, Ready, Failed, Busy }

	/// <summary>
	/// Локальный ИИ подключается сам: при входе в Battlegrounds, если сервер по Settings.AiUrl не отвечает, а llama.cpp и модель есть —
	/// запускаем llama-server в щадящем режиме (рядом с игрой: эксперты MoE в обычной памяти, 4 ГБ видеопамяти остаются игре, низкий приоритет,
	/// 6 потоков — как llm/start_server.ps1 -Load gentle). Уже запущенный вручную сервер просто используем.
	/// Где llama.cpp и модели — файл llm_dir.txt рядом с плагином (его пишет install.ps1) или Settings.LlmDir.
	/// Запущенный нами сервер выключается вместе с HDT.
	/// </summary>
	internal static class AiServer
	{
		private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
		private static readonly object Lock = new object();
		private static Process _process;
		private static Task _task;
		private static DateTime _lastAttempt = DateTime.MinValue;
		private static bool _hooked;

		private static AiServerState _state;

		public static AiServerState State
		{
			get => _state;
			private set
			{
				if(_state == value)
					return;
				_state = value;
				Changed?.Invoke();
			}
		}

		/// <summary>Состояние сменилось (для тумблера на панели). Вызывается из любого потока.</summary>
		public static event Action Changed;

		/// <summary>Тумблер «ИИ: вкл/выкл» на панели: выключение гасит запущенный нами сервер и освобождает видеокарту.</summary>
		public static void SetEnabled(bool enabled)
		{
			Settings.Instance.AiAutoStart = enabled;
			Settings.Instance.Save();
			if(enabled)
			{
				_lastAttempt = DateTime.MinValue;
				State = AiServerState.Unknown;
				EnsureRunning();
			}
			else
			{
				Stop();
				State = AiServerState.Unknown;
				ActivityFeed.Post(L.T("ИИ выключен — видеопамять свободна", "AI turned off — video memory is free"));
			}
			Changed?.Invoke();
		}

		/// <summary>Подпись для тумблера.</summary>
		public static string StatusText()
		{
			if(!Settings.Instance.AiAutoStart)
				return L.T("○ ИИ выключен — нажмите, чтобы включить", "○ AI is off — click to turn on");
			switch(State)
			{
				case AiServerState.Ready: return L.T($"● ИИ включён{(ModelName != null ? $" · {ModelName}" : "")} — нажмите, чтобы выключить", $"● AI is on{(ModelName != null ? $" · {ModelName}" : "")} — click to turn off");
				case AiServerState.Starting: return L.T("◐ ИИ запускается (10–30 с)…", "◐ AI is starting (10–30 s)…");
				case AiServerState.NotInstalled: return L.T("✕ ИИ не установлен (нет llama.cpp или модели)", "✕ AI not installed (llama.cpp or model missing)");
				case AiServerState.Failed: return L.T("✕ ИИ не запустился — нажмите, чтобы повторить", "✕ AI failed to start — click to retry");
				case AiServerState.Busy: return L.T("◌ ИИ ждёт: работает другая модель (тест/обучение)", "◌ AI waiting: another model is running (test/training)");
				default: return L.T("◌ ИИ включён, подключаюсь…", "◌ AI is on, connecting…");
			}
		}

		public static string ModelName { get; private set; }

		/// <summary>Вызывается раз в секунду из LobbyPlugin, когда открыт режим BG; сама решает, нужно ли что-то делать.</summary>
		public static void EnsureRunning()
		{
			if(!Settings.Instance.AiAutoStart)
				return;
			lock(Lock)
			{
				if(_task != null && !_task.IsCompleted)
					return;
				var wait = State == AiServerState.Failed ? 300 : State == AiServerState.Ready ? 30 : 15;
				if((DateTime.Now - _lastAttempt).TotalSeconds < wait)
					return;
				_lastAttempt = DateTime.Now;
				_task = Task.Run(Check);
			}
		}

		private static async Task Check()
		{
			if(await Healthy())
			{
				if(State != AiServerState.Ready)
				{
					State = AiServerState.Ready;
					ActivityFeed.Post(L.T($"ИИ подключён{(ModelName != null ? $" ({ModelName})" : "")}", $"AI connected{(ModelName != null ? $" ({ModelName})" : "")}"));
				}
				return;
			}
			if(_process != null && !_process.HasExited)
				return; // наш сервер ещё грузит модель
			// Одна большая модель за раз: если работает другой llama-server (тест моделей, «учитель»), второй не запускаем —
			// две модели заняли 99 % памяти, подвисало всё (2026-10-23).
			if(Process.GetProcessesByName("llama-server").Length > 0)
			{
				if(State != AiServerState.Busy)
					ActivityFeed.Post(L.T("ИИ: занята другая модель (тест/обучение) — второй экземпляр не запускаю, чтобы не кончилась память",
						"AI: another model is running (test/training) — not starting a second one to save memory"), ActivityLevel.Warning);
				State = AiServerState.Busy;
				return;
			}
			var dir = LlmDir();
			var server = dir == null ? null : Path.Combine(dir, "llama.cpp", "llama-server.exe");
			var model = dir == null ? null : FindModel(Path.Combine(dir, "models"));
			if(server == null || !File.Exists(server) || model == null)
			{
				if(State != AiServerState.NotInstalled)
					ActivityFeed.Post(L.T("ИИ не установлен: нет llama.cpp или модели (см. llm\\start_server.ps1)", "AI not installed: llama.cpp or the model is missing (see llm\\start_server.ps1)"), ActivityLevel.Warning);
				State = AiServerState.NotInstalled;
				return;
			}
			Start(server, model);
			// Ждём загрузку модели (обычно 10–20 с) — до 3 минут.
			for(int i = 0; i < 90; i++)
			{
				await Task.Delay(2000);
				if(_process == null || _process.HasExited)
					break;
				if(await Healthy())
				{
					State = AiServerState.Ready;
					ActivityFeed.Post(L.T($"ИИ запущен и подключён: {ModelName} (щадящий режим рядом с игрой)", $"AI started and connected: {ModelName} (game-friendly mode)"));
					return;
				}
			}
			State = AiServerState.Failed;
			ActivityFeed.Post(L.T("ИИ: не удалось запустить сервер — подробности в %APPDATA%\\HSaiHelper\\llm_server.log", "AI: could not start the server — see %APPDATA%\\HSaiHelper\\llm_server.log"), ActivityLevel.Warning);
		}

		private static void Start(string server, string model)
		{
			ModelName = Path.GetFileNameWithoutExtension(model);
			State = AiServerState.Starting;
			ActivityFeed.Post(L.T($"ИИ: запускаю локальную модель {ModelName}…", $"AI: starting the local model {ModelName}…"));
			var port = new Uri(Settings.Instance.AiUrl).Port;
			var moe = Regex.IsMatch(ModelName, @"A\d+B", RegexOptions.IgnoreCase);
			var args = $"-m \"{model}\" --host 127.0.0.1 --port {port} -c 8192 --fit on -fa auto --jinja --fit-target 4096 --prio -1 -t 6"
					   + (moe ? " --cpu-moe" : "") + $" --log-file \"{Path.Combine(Paths.Root, "llm_server.log")}\"";
			_process = Process.Start(new ProcessStartInfo(server, args)
			{
				UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
				WorkingDirectory = Path.GetDirectoryName(server) ?? "",
			});
			Paths.Log($"AiServer: запущен {server} {args}");
			if(!_hooked)
			{
				_hooked = true;
				AppDomain.CurrentDomain.ProcessExit += (s, e) => Stop();
			}
		}

		/// <summary>Выключить сервер, если его запускали мы (вручную запущенный не трогаем).</summary>
		public static void Stop()
		{
			try
			{
				if(_process != null && !_process.HasExited)
					_process.Kill();
			}
			catch(Exception)
			{
				// уже закрыт
			}
			_process = null;
			if(State == AiServerState.Starting || State == AiServerState.Ready)
				State = AiServerState.Unknown;
		}

		private static async Task<bool> Healthy()
		{
			try
			{
				var r = await Http.GetAsync(Settings.Instance.AiUrl.TrimEnd('/') + "/health");
				return r.IsSuccessStatusCode;
			}
			catch(Exception)
			{
				return false;
			}
		}

		/// <summary>Папка llm проекта: Settings.LlmDir, иначе llm_dir.txt рядом с плагином (пишет install.ps1).</summary>
		private static string LlmDir()
		{
			if(!string.IsNullOrEmpty(Settings.Instance.LlmDir) && Directory.Exists(Settings.Instance.LlmDir))
				return Settings.Instance.LlmDir;
			try
			{
				var file = Path.Combine(Path.GetDirectoryName(typeof(AiServer).Assembly.Location) ?? "", "llm_dir.txt");
				var dir = File.Exists(file) ? File.ReadAllText(file).Trim() : null;
				return dir != null && Directory.Exists(dir) ? dir : null;
			}
			catch(Exception)
			{
				return null;
			}
		}

		/// <summary>Модель для игры: из настроек, иначе первая из списка (лучшие по tools/bench_llm.py для игры).</summary>
		private static string FindModel(string models)
		{
			if(!Directory.Exists(models))
				return null;
			var preferred = new[] { Settings.Instance.AiModelFile, "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf", "gemma-4-12b-it-Q4_K_M.gguf", "gemma-4-E4B-it-Q4_K_M.gguf" };
			return preferred.Where(f => !string.IsNullOrEmpty(f)).Select(f => Path.Combine(models, f)).FirstOrDefault(File.Exists);
		}
	}
}
