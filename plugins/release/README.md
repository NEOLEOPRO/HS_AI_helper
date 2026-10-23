# HSaiHelper — помощник для Hearthstone Battlegrounds

Плагин к [Hearthstone Deck Tracker](https://hsdecktracker.net) (HDT): лобби, шансы до боя, оценки партии, советы по сборке
и локальный ИИ, которому можно задать вопрос прямо в игре.

**Бесплатно. Всё работает на вашем компьютере.** Плагин только читает то, что уже знает HDT: ничего не нажимает за вас,
не меняет игру и не трогает её сеть. Платных сервисов и облаков нет.

## Что нужно
- Windows 10/11 и установленный [Hearthstone Deck Tracker](https://hsdecktracker.net).
- Для ИИ-помощника (по желанию): 16 ГБ памяти и больше, 6–20 ГБ на диске; лучше видеокарта NVIDIA (подойдут AMD/Intel или
  только процессор, но медленнее).

## Установка — 3 шага
1. **Скачайте** архив `HSaiHelper-<версия>.zip` со страницы релизов: https://github.com/NEOLEOPRO/HSaiHelper/releases — и **распакуйте** его в любую папку
   (правой кнопкой → «Извлечь все»). Запускать прямо из архива нельзя.
2. Дважды щёлкните **`Install.cmd`**. Откроется чёрное окно, в конце будет «Готово». Нажмите любую клавишу.
   *Если Windows покажет «Windows защитила ваш компьютер» — «Подробнее» → «Выполнить в любом случае».*
3. **Перезапустите HDT** → Options → Tracker → **Plugins** → поставьте галочки у **HSAI Recorder** и **HSAI Lobby**.

Всё. Зайдите в Battlegrounds — появятся окна HSAI.

### ИИ-помощник (по желанию)
Дважды щёлкните **`Setup-AI.cmd`**. Он сам выберет модель по объёму памяти, скачает llama.cpp и модель Gemma 4
(5–17 ГБ — может занять время, докачка при обрыве работает) и скажет плагину, где они. Потом перезапустите HDT:
модель запускается сама при входе в Battlegrounds в щадящем режиме, чтобы игра не тормозила.

Выбрать модель вручную: открыть PowerShell в папке и запустить
`powershell -ExecutionPolicy Bypass -File Setup-AI.ps1 -Model game` (`game` — 26B, быстрая, нужно 32 ГБ памяти;
`smart` — 12B, 16 ГБ; `small` — E4B, слабые ПК).

## Что на экране
Окна перетаскиваются за заголовок и сворачиваются ▾. **Ctrl+Shift+H** сворачивает и разворачивает все окна сразу
(клавиша меняется в настройках). Длинное окно прокручивается колесом мыши.

- **Лобби** — игроки в порядке мест, как в игре: тир, здоровье, состав по типам, серия, шанс топ-4. ▸ у строки — динамика
  состава и похожие сборки из гайдов HDT.
- **Следующий соперник** — шансы до боя (симулятор BobsBuddy, как в HDT, против похожих столов), прошлые бои,
  подозрительность его стола после боя с ним, кто будет через ход.
- **Оценки** — шанс места и риск вылета, сила стола против столов этого хода, **«Сборка и пул»** (какую сборку проще
  добрать, ★ — нужная карта сейчас в таверне, ⚠ — когда стоит сменить сборку), тёмный дар, подсказки на первые ходы,
  после партии — разбор. Раздел **«Как читать»** объясняет каждую цифру.
- **ИИ** — вопрос локальной модели («что купить?», «повышать таверну?»). Она видит стол, руку, таверну, золото, силы героев,
  безделушки и все оценки. «Ход мыслей» — увидеть рассуждение (дольше). Тумблер включает и выключает модель.
- **Настройки** (HDT → Options → Plugins → HSAI Lobby → «Настройки»): язык, модель ИИ, язык ответов, размер окон,
  горячая клавиша, вернуть окна на места, выгрузка данных.

## Если что-то не так
| Проблема | Что сделать |
|---|---|
| Нет HSAI в списке плагинов HDT | Запустите `Install.cmd` ещё раз и перезапустите HDT полностью (значок в трее → Exit). |
| Плагин есть, окон нет | Окна видны только в режиме Battlegrounds (меню BG, партия, после партии). «Настройки» → «Вернуть окна на места». |
| Окна мелкие или крупные | «Настройки» → «Размер окон». |
| «ИИ не установлен» | Запустите `Setup-AI.cmd`, потом перезапустите HDT. |
| ИИ отвечает долго | Первый ответ после запуска — 20–40 с; выключите «ход мыслей»; в настройках выберите модель поменьше. |
| Горячая клавиша не работает | Сочетание занято другой программой — выберите другое в настройках. |

Журналы для разбора проблем: `%APPDATA%\HSaiHelper\plugin.log` и `llm_server.log`.

**Удалить:** удалите папку `%APPDATA%\HearthstoneDeckTracker\Plugins\HSaiHelper` (плагин), `%LOCALAPPDATA%\HSaiHelper` (ИИ)
и при желании `%APPDATA%\HSaiHelper` (ваши записи и настройки).

## Ваши данные и помощь проекту
Записи партий хранятся только у вас: `%APPDATA%\HSaiHelper`. Ничего не отправляется само.

Хотите помочь сделать подсказки и ИИ точнее? «Настройки» → **«Подготовить данные для проекта»** — получится архив без ников,
точного времени и точного MMR (MMR — диапазоном); вопросы к ИИ и заметки — только если поставите галочку. Создайте Issue «Данные партий»
на https://github.com/NEOLEOPRO/HSaiHelper/issues/new/choose и перетащите туда архив. Там же — сообщить об ошибке или предложить идею.

---

# HSaiHelper — Hearthstone Battlegrounds assistant

A [Hearthstone Deck Tracker](https://hsdecktracker.net) plugin: lobby, pre-combat odds, game estimates, build advice and a local AI
you can ask during the game. **Free, runs entirely on your PC, read-only** — it never clicks for you or touches the game or its network.

## Install — 3 steps
1. **Download** `HSaiHelper-<version>.zip` from https://github.com/NEOLEOPRO/HSaiHelper/releases and **extract** it (right-click → Extract All).
2. Double-click **`Install.cmd`**. *If Windows SmartScreen appears: More info → Run anyway.*
3. **Restart HDT** → Options → Tracker → **Plugins** → enable **HSAI Recorder** and **HSAI Lobby**.

**AI assistant (optional):** double-click **`Setup-AI.cmd`** — it picks a model for your RAM, downloads llama.cpp and a Gemma 4
model (5–17 GB) and tells the plugin where they are. Restart HDT; the model starts automatically in Battlegrounds (game-friendly mode).
Needs 16+ GB RAM; an NVIDIA GPU is best.

## On screen
Drag windows by the title, collapse with ▾; **Ctrl+Shift+H** collapses/expands all windows (change it in Settings); long windows scroll.
Lobby · Next opponent (pre-combat odds, past fights, suspicion after the fight, turn-after opponent) · Estimates (placement, elimination
risk, board vs this turn's boards, "Build & pool" — easiest build, ★ needed card in the tavern, ⚠ when to switch; Dark Gift; opening tips;
post-game review; "How to read") · AI (ask the local model; "show reasoning"; on/off). Settings: HDT → Options → Plugins → HSAI Lobby.

## Troubleshooting
No HSAI in the plugin list — run `Install.cmd` again and fully restart HDT (tray icon → Exit). No windows — they show only in
Battlegrounds; Settings → reset window positions. "AI not installed" — run `Setup-AI.cmd`. Logs: `%APPDATA%\HSaiHelper\plugin.log`.
**Uninstall:** delete `%APPDATA%\HearthstoneDeckTracker\Plugins\HSaiHelper`, `%LOCALAPPDATA%\HSaiHelper` and optionally `%APPDATA%\HSaiHelper`.

## Your data
Everything stays in `%APPDATA%\HSaiHelper`; nothing is sent automatically. To help the project: Settings → **Prepare data for the
project** — an archive without names, exact times or exact MMR (MMR as a range); your AI questions/notes only if you tick the box.
Open a "Game data" issue at https://github.com/NEOLEOPRO/HSaiHelper/issues/new/choose and drop the archive there (bugs and ideas — same place).
