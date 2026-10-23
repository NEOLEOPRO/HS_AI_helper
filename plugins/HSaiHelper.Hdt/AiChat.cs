using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Core = Hearthstone_Deck_Tracker.API.Core;

namespace HSaiHelper.Hdt
{
	/// <summary>Advice — ответ на автоматический запрос (совет на ход, «как обыграть»): выделяется на панели.</summary>
	internal enum ChatRole { Me, Ai, Note, System, Advice }

	internal class ChatItem
	{
		public DateTime Time;
		public int Turn;
		public ChatRole Role;
		public string Text;
		/// <summary>Ход мыслей модели (если включён тумблер «ход мыслей»).</summary>
		public string Reasoning;
	}

	/// <summary>
	/// «Командная строка» к нашему ИИ: вопрос игрока + сводка текущей партии (что видно на панели: игроки, мой стол, следующий соперник,
	/// шансы до боя, сборки) → локальная модель llama.cpp (OpenAI-совместимый сервер, по умолчанию http://127.0.0.1:8091) → ответ на панели.
	/// Только своё железо, без интернета. Числа модель не считает — берёт из сводки (симулятор, статистика).
	/// «/заметка текст» — просто заметка без ИИ. Всё пишется в записи партии (combats_*.jsonl, type "ai_chat").
	/// Если сервер не запущен — понятное сообщение, плагин работает дальше.
	/// </summary>
	internal static class AiChat
	{
		private static readonly object Lock = new object();
		private static readonly List<ChatItem> Items = new List<ChatItem>();
		private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
		private static bool _busy;

		public static event Action Changed;

		public static bool Busy
		{
			get { lock(Lock) return _busy; }
		}

		/// <summary>
		/// Сводка партии для модели — собирает LobbyPlugin в момент вопроса (UI-поток), всегда по-английски:
		/// русский текст ~на треть длиннее в токенах, а маленькие модели лучше рассуждают на английском.
		/// </summary>
		public static Func<string> ContextProvider { get; set; }

		public static void Submit(string text)
		{
			text = text?.Trim();
			if(string.IsNullOrEmpty(text))
				return;
			var notePrefix = new[] { "/заметка", "/note" }.FirstOrDefault(p => text.StartsWith(p, StringComparison.OrdinalIgnoreCase));
			if(notePrefix != null)
			{
				var note = text.Substring(notePrefix.Length).Trim();
				if(note.Length > 0)
				{
					Add(ChatRole.Note, note);
					ActivityFeed.Post("✎ " + note, ActivityLevel.Comment);
				}
				return;
			}
			if(Busy)
			{
				Add(ChatRole.System, L.T("ИИ ещё отвечает на прошлый вопрос — подождите.", "The AI is still answering the previous question."));
				return;
			}
			Add(ChatRole.Me, text);
			lock(Lock)
				_busy = true;
			string context = null;
			try
			{
				context = ContextProvider?.Invoke();
			}
			catch(Exception ex)
			{
				Paths.Log("AiChat.Context: " + ex);
			}
			var history = Latest(8).Where(i => i.Role == ChatRole.Me || i.Role == ChatRole.Ai).ToList();
			Task.Run(() => Ask(text, context, history));
		}

		/// <summary>
		/// Автоматический запрос без набора в чате (галочка «советы ИИ», кнопка «как обыграть»): заголовок на панели, вопрос и числа
		/// (посчитанные кодом) — к сводке партии. Если ИИ занят — молча пропускаем (совет на ход придёт в следующий раз).
		/// Из любого потока: сводку партии собираем в UI-потоке.
		/// </summary>
		public static void SubmitAuto(string title, string question, string facts)
		{
			lock(Lock)
			{
				if(_busy)
					return;
				_busy = true;
			}
			var dispatcher = System.Windows.Application.Current?.Dispatcher;
			dispatcher?.BeginInvoke(new Action(() =>
			{
				string context = null;
				try
				{
					context = ContextProvider?.Invoke();
				}
				catch(Exception ex)
				{
					Paths.Log("AiChat.Context: " + ex);
				}
				Add(ChatRole.System, "⚑ " + title + L.T(" — считаю…", " — thinking…"));
				Task.Run(() => Ask(question, (context ?? "") + "\n" + facts, new List<ChatItem>(), advice: true));
			}));
			if(dispatcher == null)
				lock(Lock)
					_busy = false;
		}

		/// <summary>Служебная строка в окне «ИИ» (из любого потока).</summary>
		public static void SystemNote(string text) => Add(ChatRole.System, text);

		private static async Task Ask(string question, string context, List<ChatItem> history, bool advice = false)
		{
			var started = DateTime.Now;
			ActivityFeed.Post(L.T("ИИ думает над вопросом…", "The AI is thinking…"));
			try
			{
				var messages = new JArray { new JObject { ["role"] = "system", ["content"] = SystemPrompt() } };
				// Прошлые реплики — без сводки (она меняется), чтобы модель помнила разговор.
				foreach(var h in history.Take(Math.Max(0, history.Count - 1)))
					messages.Add(new JObject { ["role"] = h.Role == ChatRole.Me ? "user" : "assistant", ["content"] = h.Text });
				messages.Add(new JObject
				{
					["role"] = "user",
					["content"] = (string.IsNullOrEmpty(context) ? "(no game in progress)" : "Game state:\n" + context)
								  + "\n\nPlayer's question: " + question,
				});
				// «Ход мыслей»: модель сначала рассуждает (видно на панели) — дольше (+10–30 с), зато видно, почему она так советует.
				bool think = Settings.Instance.AiShowReasoning;
				var body = new JObject
				{
					["messages"] = messages,
					["temperature"] = 0.3,
					// С рассуждением 1500 токенов мало: в тесте (bench_llm --think) 22 из 36 ответов «что купить» оборвались без итога.
					["max_tokens"] = think ? 4000 : 400,
					["chat_template_kwargs"] = think ? new JObject { ["enable_thinking"] = true, ["reasoning_effort"] = "medium" }
						: new JObject { ["enable_thinking"] = false, ["reasoning_effort"] = "low" },
				};
				var url = Settings.Instance.AiUrl.TrimEnd('/') + "/v1/chat/completions";
				var response = await Http.PostAsync(url, new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"));
				var json = JObject.Parse(await response.Content.ReadAsStringAsync());
				var answer = ((string)json["choices"]?[0]?["message"]?["content"])?.Trim();
				var reasoning = ((string)json["choices"]?[0]?["message"]?["reasoning_content"])?.Trim();
				if(string.IsNullOrEmpty(answer) && !string.IsNullOrEmpty(reasoning))
					answer = L.T("(не успел закончить рассуждение — ниже ход мыслей; спросите короче или выключите «ход мыслей»)",
						"(ran out of time while reasoning — the reasoning is below; ask a shorter question or turn reasoning off)");
				if(string.IsNullOrEmpty(answer))
					throw new InvalidOperationException((string)json["error"]?["message"] ?? "пустой ответ");
				Add(advice ? ChatRole.Advice : ChatRole.Ai, answer, reasoning);
				// Полный обмен — сырьё для «учителя → ученика»: большая модель потом ответит на тот же вопрос с той же сводкой (tools/teacher.py).
				RecorderPlugin.Write(new JObject
				{
					["type"] = "ai_exchange",
					["time"] = DateTime.UtcNow,
					["question"] = question,
					["context"] = context,
					["answer"] = answer,
					["reasoning"] = reasoning,
					["model"] = (string)json["model"],
					["seconds"] = Math.Round((DateTime.Now - started).TotalSeconds, 1),
					["answer_language"] = Settings.Instance.AiAnswerLanguage,
				});
				ActivityFeed.Post(L.T($"ИИ ответил за {(DateTime.Now - started).TotalSeconds:F0} с", $"The AI answered in {(DateTime.Now - started).TotalSeconds:F0} s"));
			}
			catch(HttpRequestException)
			{
				Add(ChatRole.System, AiServer.State == AiServerState.Starting
					? L.T("ИИ ещё загружает модель (10–30 с) — спросите чуть позже.", "The AI is still loading the model (10–30 s) — ask again shortly.")
					: AiServer.State == AiServerState.NotInstalled
						? L.T("ИИ не установлен: нет llama.cpp или модели в папке llm проекта.", "The AI is not installed: llama.cpp or the model is missing in the project's llm folder.")
						: L.T("ИИ не отвечает. Он запускается сам при входе в Battlegrounds; можно и вручную: llm\\start_server.ps1.",
							"The AI is not responding. It starts automatically in Battlegrounds; or run llm\\start_server.ps1."));
			}
			catch(Exception ex)
			{
				var reason = ex is TaskCanceledException ? L.T("слишком долго, нет ответа", "timed out") : ex.Message;
				Add(ChatRole.System, L.T("ИИ: ошибка — ", "AI error — ") + reason);
				Paths.Log("AiChat: " + ex);
			}
			finally
			{
				lock(Lock)
					_busy = false;
				Changed?.Invoke();
			}
		}

		/// <summary>
		/// Роль и правила BG — всегда по-английски (правила — core_rules_en.md рядом с плагином, его кладёт install.ps1).
		/// Язык ответа — Settings.AiAnswerLanguage: русский ответ ~на треть длиннее в токенах, значит и дольше.
		/// </summary>
		private static string SystemPrompt()
		{
			var language = Settings.Instance.AiAnswerLanguage == "ru"
				? "Answer in Russian, using Russian Battlegrounds player terms (триплет; «Яд» is permanent poison, «Токсичность» is venomous — removed after the first hit). " +
				  "Card and hero names in the game state are already in Russian — use them exactly. Tribes in Russian: beast — звери, demon — демоны, dragon — драконы, " +
				  "elemental — элементали, mech — механизмы, murloc — мурлоки, naga — наги, pirate — пираты, quilboar — свинобразы, undead — нежить, aberration — аберрации."
				: "Answer in English.";
			var role = "You are an AI assistant for a strong Hearthstone Battlegrounds player (high MMR). Answer briefly (2–5 sentences). " +
					   // «Говорит очевидные вещи» (2026-10-23): сильному игроку не нужен пересказ цифр с панели.
					   "Do not restate numbers the player already sees on the panel and skip generic advice. Give 1–3 specific, non-obvious points " +
					   "tied to the actual cards, heroes, tavern offers and opponents in the game state (what to buy, sell or keep, when to level or roll, " +
					   "which opponent is the threat and why, board order). If the standard move is right, say it in one sentence. " +
					   "Name a build or archetype only as given in the game state (likely build / tribes); never invent one; if unclear, say so. " +
					   // Правила — в текстах карт и описаниях гайдов: модель сама выводит из них взаимодействия и противодействие.
					   "Card texts and the build guides in the game state define how things work (a Battlecry triggers when played in the tavern, " +
					   "a Deathrattle when the minion dies, etc.) — the player knows these basics, so do not explain them; use them to find interactions: " +
					   "which key cards the opponent's build likely runs even if not seen yet (e.g. a deathrattle undead or beast build usually has Titus Rivendare — kill it first), " +
					   "what to kill first, how to order the board against them, what to buy to counter them. " +
					   "Give board-order advice only from the reorder numbers in the state (minions attack left to right; do not guess). " +
					   "Buying a key card of the opponent's build also takes a copy out of the shared pool — say so when that is the reason. " +
					   "Take numbers (odds, damage, percentages) only from the game state — they come from the simulator and statistics; never invent or recompute them. " +
					   "If the data is not enough, say so. " +
				   // Риск и награда: MMR сильно награждает верхние места, поэтому выбор линии зависит от положения.
				   "Weigh risk against reward using the placement estimates in the game state: if the top-4 chance is low or the elimination risk is high, " +
				   "a high-variance line (going for triplets, rolling for a key card or a triplet copy, levelling for a power spike) can be the right call — " +
				   "say so and use the pool/triplet odds; if the top-4 chance is high, prefer safe lines that keep health and board strength. " +
				   "Name the trade-off explicitly (what you gain, what you risk). " + language;
			try
			{
				var file = Path.Combine(Path.GetDirectoryName(typeof(AiChat).Assembly.Location) ?? "", "core_rules_en.md");
				if(File.Exists(file))
					return role + "\n\n" + File.ReadAllText(file);
			}
			catch(Exception)
			{
				// без правил — тоже работает
			}
			return role;
		}

		private static void Add(ChatRole role, string text, string reasoning = null)
		{
			int turn = 0;
			try
			{
				if(Core.Game.IsBattlegroundsMatch)
					turn = Core.Game.GetTurnNumber();
			}
			catch(Exception)
			{
				// вне партии — без раунда
			}
			lock(Lock)
				Items.Add(new ChatItem { Time = DateTime.Now, Turn = turn, Role = role, Text = text, Reasoning = reasoning });
			if(role != ChatRole.System)
				RecorderPlugin.Write(new JObject
				{
					["type"] = "ai_chat",
					["time"] = DateTime.UtcNow,
					["turn"] = turn,
					["role"] = role.ToString().ToLowerInvariant(),
					["text"] = text,
				});
			Changed?.Invoke();
		}

		public static List<ChatItem> Latest(int count)
		{
			lock(Lock)
				return Items.Skip(Math.Max(0, Items.Count - count)).ToList();
		}
	}
}
