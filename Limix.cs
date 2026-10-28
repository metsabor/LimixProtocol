// LimixProtocol - DPI-bypass launcher and manager.
// C# 5 / WPF: compiles with the csc.exe built into Windows (no SDK needed).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;
using Ellipse = System.Windows.Shapes.Ellipse;

[assembly: AssemblyTitle("LimixProtocol")]
[assembly: AssemblyProduct("LimixProtocol")]
[assembly: AssemblyVersion("1.1.0.0")]

namespace Limix
{
    static class Program
    {
        // =====================================================================
        //  Data types
        // =====================================================================
        class Svc
        {
            public string Id, Name, Test;
            public string[] Dom, Ips;
            public bool Def;
            public Svc(string id, string name, string test, bool def, string dom, string ips)
            {
                Id = id; Name = name; Test = test; Def = def;
                Dom = dom.Split(' ');
                Ips = ips == null ? new string[0] : ips.Split(' ');
            }
        }

        class Strat
        {
            public string Id, NameKey, DescKey;
            public int Num;
            public string[] St;
        }

        // =====================================================================
        //  State
        // =====================================================================
        static string Root, BinDir, ListsDir, CfgPath;
        static bool silent, quitting;
        static Window win;
        static Application app;
        static Mutex mtx;
        static EventWaitHandle showEv;
        static System.Windows.Forms.NotifyIcon tray;
        static System.Windows.Forms.ToolStripMenuItem miOpen, miToggle, miExit;
        static DispatcherTimer applyTimer, upTimer;

        static Dictionary<string, string> cfg = new Dictionary<string, string>();
        static List<Svc> Svcs = new List<Svc>();
        static List<Strat> Strats = new List<Strat>();
        static List<string> custom = new List<string>();
        static List<string> excl = new List<string>();

        static readonly object engLock = new object();
        static Process eng;
        static DateTime engStart, lastCrash = DateTime.MinValue;
        static bool engStopping, pulseOn;
        static int restarts;
        static volatile bool testing;
        static string state = "off";
        static string bestId;
        static int curPage;
        static bool promoArm, promoShown, auroraOn;
        static DateTime lastPromo = DateTime.MinValue;
        static DispatcherTimer promoTimer;
        static Geometry ghGeo;

        static readonly string[] PageNames = { "Home", "Services", "Domains", "Strategies", "Health", "Logs", "Settings", "About" };
        static readonly string[] Guard = { "winws.exe", "WinDivert.dll", "WinDivert64.sys", "cygwin1.dll" };
        static readonly string[][] Accents = new string[][] {
            new[] { "red", "#E5484D" }, new[] { "orange", "#F76B15" }, new[] { "yellow", "#F5C400" },
            new[] { "green", "#30A46C" }, new[] { "teal", "#12A594" }, new[] { "blue", "#3E63DD" },
            new[] { "purple", "#8E4EC6" }, new[] { "pink", "#D6409F" }, new[] { "gray", "#8B8D98" } };

        // =====================================================================
        //  Config
        // =====================================================================
        static string CG(string k, string d) { string v; return cfg.TryGetValue(k, out v) ? v : d; }
        static bool CB(string k, bool d) { string v; if (!cfg.TryGetValue(k, out v)) return d; return v == "1"; }
        static void CS(string k, string v) { cfg[k] = v; SaveCfg(); }
        static void CSB(string k, bool v) { CS(k, v ? "1" : "0"); }

        static void LoadCfg()
        {
            try
            {
                if (File.Exists(CfgPath))
                    foreach (string l in File.ReadAllLines(CfgPath))
                    {
                        int i = l.IndexOf('=');
                        if (i > 0) cfg[l.Substring(0, i)] = l.Substring(i + 1);
                    }
            }
            catch { }
        }

        static void SaveCfg()
        {
            try { File.WriteAllLines(CfgPath, cfg.Select(kv => kv.Key + "=" + kv.Value).ToArray(), Encoding.UTF8); }
            catch { }
        }

        static string FindBin()
        {
            List<string> c = new List<string>();
            c.Add(Root);
            c.Add(Path.Combine(Root, "bin"));
            c.Add(Path.Combine(Root, "engine"));
            try
            {
                foreach (string d in Directory.GetDirectories(Root))
                {
                    c.Add(d);
                    c.Add(Path.Combine(d, "bin"));
                    try { foreach (string dd in Directory.GetDirectories(d)) c.Add(dd); } catch { }
                }
                DirectoryInfo par = Directory.GetParent(Root);
                if (par != null) c.Add(Path.Combine(par.FullName, "bin"));
            }
            catch { }
            foreach (string p in c)
                if (File.Exists(Path.Combine(p, "winws.exe"))) return p;
            return null;
        }

        static void InitPaths()
        {
            Root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            CfgPath = Path.Combine(Root, "limix.cfg");
            BinDir = FindBin();
            ListsDir = Path.Combine(Root, "lists");
            try { Directory.CreateDirectory(ListsDir); } catch { }
        }

        static string HostsFile { get { return Path.Combine(ListsDir, "limix-hosts.txt"); } }
        static string ExclFile { get { return Path.Combine(ListsDir, "limix-exclude.txt"); } }
        static string IpFile { get { return Path.Combine(ListsDir, "limix-ipset.txt"); } }

        // =====================================================================
        //  Localization (RU / EN). Add another language: extend T() and L().
        // =====================================================================
        static Dictionary<string, string[]> S = new Dictionary<string, string[]>();
        static void T(string k, string ru, string en) { S[k] = new string[] { ru, en }; }
        static string Lang { get { return CG("lang", CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en"); } }
        static string L(string k) { string[] v; if (!S.TryGetValue(k, out v)) return k; return v[Lang == "en" ? 1 : 0]; }

        static void InitStrings()
        {
            T("nav.home", "Главная", "Home"); T("nav.services", "Сервисы", "Services"); T("nav.domains", "Домены", "Domains");
            T("nav.strategies", "Стратегии", "Strategies"); T("nav.logs", "Журнал", "Logs"); T("nav.settings", "Настройки", "Settings"); T("nav.about", "О программе", "About");

            T("st.off", "Отключено", "Disconnected"); T("st.on", "Защита активна", "Protected"); T("st.starting", "Запуск…", "Starting…");
            T("hint.off", "Нажмите, чтобы включить обход блокировок", "Press to enable the bypass");
            T("hint.on", "Выбранные сервисы работают через обход DPI", "Selected services go through the DPI bypass");
            T("hint.starting", "Запускаем движок…", "Starting the engine…");
            T("w.on", "Вкл", "On"); T("w.off", "Выкл", "Off");
            T("home.strategy", "Стратегия", "Strategy"); T("home.uptime", "Время работы", "Uptime");
            T("home.services", "Сервисов", "Services"); T("home.autostart", "Автозапуск", "Autostart");

            T("svc.title", "Сервисы", "Services");
            T("svc.sub", "Включите то, что должно работать. Домены добавятся в список автоматически и применятся сразу.", "Turn on what should work. Domains are added to the list automatically and applied instantly.");
            T("svc.all", "Включить все", "Enable all"); T("svc.none", "Выключить все", "Disable all"); T("svc.domains", "доменов", "domains");

            T("dom.title", "Свои домены", "Custom domains");
            T("dom.sub", "Добавляйте любые сайты, которые нужно пропускать через обход. Изменения применяются автоматически.", "Add any sites that should go through the bypass. Changes are applied automatically.");
            T("dom.import", "Импорт .txt", "Import .txt"); T("dom.export", "Экспорт", "Export"); T("dom.folder", "Папка списков", "Lists folder");
            T("dom.custom", "Обходить", "Bypass"); T("dom.custom.d", "Домен вида example.com (поддомены включаются сами)", "Domain like example.com (subdomains are included)");
            T("dom.excl", "Исключения", "Exclusions"); T("dom.excl.d", "Эти домены движок не трогает никогда", "The engine never touches these domains");
            T("dom.add", "Добавить", "Add");

            T("str.title", "Стратегии", "Strategies");
            T("str.sub", "У каждого провайдера свои фильтры. Выберите рабочий метод вручную или запустите автоподбор.", "Every ISP filters differently. Pick a working method by hand or run the auto-test.");
            T("str.auto", "Автоподбор", "Auto-select"); T("str.apply", "Применить лучшую", "Apply best");
            T("str.std", "Стандартная", "Standard"); T("str.alt", "Альтернативная", "Alternative");
            T("sd.1", "fake + multidisorder. Сбалансированный вариант для большинства провайдеров.", "fake + multidisorder. A balanced choice for most ISPs.");
            T("sd.2", "fake + fakedsplit с авто-TTL. Мягкий метод.", "fake + fakedsplit with auto-TTL. A gentle method.");
            T("sd.3", "multisplit с seqovl. Если fake-пакеты режет провайдер.", "multisplit with seqovl. When the ISP drops fake packets.");
            T("sd.4", "fake + split2 с seqovl и badseq.", "fake + split2 with seqovl and badseq.");
            T("sd.5", "hostfakesplit (нужна свежая версия winws).", "hostfakesplit (needs a recent winws build).");
            T("test.need", "Включите хотя бы один сервис на вкладке «Сервисы».", "Enable at least one service on the Services tab.");
            T("test.running", "Проверяю:", "Testing:");
            T("test.best", "Лучшая: {0} — доступно {1} из {2}", "Best: {0} — {1} of {2} reachable");
            T("test.none", "Ни одна стратегия не помогла. Возможно, блокировка идёт по IP — нужен VPN/прокси.", "No strategy helped. The block may be IP-based — you may need a VPN/proxy.");
            T("test.err", "не запустилась", "failed to start");

            T("log.title", "Журнал", "Logs"); T("log.copy", "Копировать", "Copy"); T("log.clear", "Очистить", "Clear");
            T("log.started", "Движок запущен, стратегия:", "Engine started, strategy:"); T("log.stopped", "Движок остановлен", "Engine stopped");
            T("log.crash", "Движок остановился неожиданно", "The engine stopped unexpectedly");
            T("log.nohosts", "Список доменов пуст: включите сервис или добавьте свой домен", "The host list is empty: enable a service or add a domain");
            T("log.pinned", "Хэши файлов движка сохранены как эталон", "Engine file hashes saved as the trusted baseline");
            T("log.killed", "Остановлен посторонний winws.exe", "Stopped a foreign winws.exe");

            T("err.nobin", "Не найден движок: положите папку bin (с winws.exe) рядом с LimixProtocol.exe.", "Engine not found: put the bin folder (with winws.exe) next to LimixProtocol.exe.");
            T("err.hash", "Файлы движка изменились (хэш не совпадает с эталоном). Проверьте их или обновите эталон в Настройках.", "Engine files changed (hash mismatch). Check them or refresh the baseline in Settings.");
            T("err.exit", "winws.exe сразу завершился, код выхода:", "winws.exe exited immediately, exit code:");

            T("set.title", "Настройки", "Settings"); T("set.appearance", "ОФОРМЛЕНИЕ", "APPEARANCE");
            T("set.theme", "Тема", "Theme"); T("th.system", "Системная", "System"); T("th.dark", "Тёмная", "Dark"); T("th.light", "Светлая", "Light");
            T("set.accent", "Цвет акцента", "Accent color"); T("set.lang", "Язык", "Language");
            T("acc.red", "Красный", "Red"); T("acc.orange", "Оранжевый", "Orange"); T("acc.yellow", "Жёлтый", "Yellow"); T("acc.green", "Зелёный", "Green");
            T("acc.teal", "Бирюзовый", "Teal"); T("acc.blue", "Синий", "Blue"); T("acc.purple", "Фиолетовый", "Purple"); T("acc.pink", "Розовый", "Pink"); T("acc.gray", "Серый", "Gray");
            T("set.startup", "ЗАПУСК", "STARTUP");
            T("set.autostart", "Запускать вместе с Windows", "Start with Windows");
            T("set.autostart.d", "Тихо, без окна и без запроса UAC (через планировщик). Окно откроется, если запустить LimixProtocol.exe из папки.", "Silent, no window and no UAC prompt (via Task Scheduler). The window opens when you run LimixProtocol.exe from its folder.");
            T("set.autoconn", "Подключаться при запуске", "Connect on launch"); T("set.autoconn.d", "Обход включается сам при старте программы", "The bypass turns on by itself when the app starts");
            T("set.tray", "Значок в трее", "Tray icon"); T("set.tray.d", "Без значка программа работает совсем незаметно", "Without it the app runs completely unnoticed");
            T("set.closetray", "Крестик сворачивает в трей", "Close button minimizes to tray");
            T("set.engine", "ДВИЖОК", "ENGINE");
            T("set.ip", "Режим IP-диапазонов", "IP-range mode"); T("set.ip.d", "Нужен Telegram и сервисам, которые заходят напрямую по IP, без домена", "Needed by Telegram and services that connect straight to an IP without a domain");
            T("set.watch", "Перезапуск при сбое", "Restart on crash"); T("set.watch.d", "Если winws.exe упадёт, он поднимется заново", "If winws.exe dies it is started again");
            T("set.security", "БЕЗОПАСНОСТЬ", "SECURITY");
            T("set.verify", "Проверять целостность файлов", "Verify file integrity"); T("set.verify.d", "SHA-256 для winws.exe и драйвера WinDivert; подмена файла блокирует запуск", "SHA-256 for winws.exe and the WinDivert driver; a swapped file blocks startup");
            T("set.pin", "Запомнить текущие хэши", "Pin current hashes");
            T("hash.ok", "✔ Файлы совпадают с эталоном", "✔ Files match the baseline"); T("hash.bad", "✖ Файлы отличаются от эталона!", "✖ Files differ from the baseline!"); T("hash.none", "Эталон ещё не задан (появится при первом подключении)", "No baseline yet (created on first connect)");
            T("set.tools", "ОБСЛУЖИВАНИЕ", "MAINTENANCE");
            T("set.diag", "Диагностика", "Diagnostics"); T("set.dns", "Сбросить DNS-кэш", "Flush DNS cache"); T("set.driver", "Сбросить драйвер WinDivert", "Reset WinDivert driver"); T("set.folder", "Открыть папку", "Open folder");
            T("diag.title", "Диагностика", "Diagnostics"); T("diag.dns", "DNS-кэш очищен", "DNS cache flushed"); T("diag.drv", "Драйвер WinDivert сброшен", "WinDivert driver reset");

            T("nav.health", "Диагностика", "Health");
            T("home.status", "Статус сервисов", "Service status");
            T("home.check", "Проверить", "Check");
            T("home.chips.off", "Включите защиту — здесь появится статус каждого сервиса.", "Turn the protection on to see the status of each service here.");
            T("health.title", "Диагностика и самоисправление", "Diagnostics & self-repair");
            T("health.sub", "Проверяет систему, находит типичные причины поломки и сама пытается их исправить.", "Checks your system, finds the usual causes of failures and tries to fix them by itself.");
            T("health.run", "Проверить и исправить", "Check & fix");
            T("health.copy", "Копировать отчёт", "Copy report");
            T("health.issue", "Сообщить о проблеме", "Report a problem");
            T("health.av", "Исключение антивируса", "Antivirus exclusion");
            T("health.idle", "Нажмите «Проверить и исправить» — это займёт около минуты.", "Press “Check & fix” — it takes about a minute.");
            T("health.running", "Проверяю…", "Checking…");
            T("health.done", "Готово: ошибок — {0}, предупреждений — {1}.", "Done: {0} errors, {1} warnings.");
            T("health.copied", "Отчёт скопирован. Вставьте его в обращение на GitHub.", "Report copied. Paste it into your GitHub issue.");
            T("h.ok", "Всё в порядке", "All good");
            T("h.fixed", "исправлено", "fixed");
            T("h.admin", "Права администратора", "Administrator rights");
            T("h.os", "Система", "System");
            T("h.engine", "Файлы движка", "Engine files");
            T("h.missing", "Не хватает:", "Missing:");
            T("h.av", "Похоже, файлы удалил антивирус. Восстановите их из карантина и добавьте папку в исключения.", "An antivirus probably removed them. Restore them from quarantine and exclude the folder.");
            T("h.unblock", "Блокировка файлов Windows", "Windows file blocking");
            T("h.unblocked", "Снята блокировка у файлов:", "Unblocked files:");
            T("h.hash", "Целостность файлов", "File integrity");
            T("h.conflict", "Конфликтующие программы", "Conflicting software");
            T("h.stopped", "остановлена", "stopped");
            T("h.driver", "Драйвер WinDivert", "WinDivert driver");
            T("h.lists", "Списки и сервисы", "Lists and services");
            T("h.defaults", "Не было включённых сервисов — включил YouTube и Discord.", "No services were enabled — turned on YouTube and Discord.");
            T("h.dns", "DNS", "DNS");
            T("h.dnsfail", "DNS не отвечает. Проверьте интернет или смените DNS (например, на 1.1.1.1).", "DNS is not answering. Check your internet or change DNS (for example to 1.1.1.1).");
            T("h.net", "Интернет", "Internet");
            T("h.nonet", "Нет доступа в интернет.", "No internet access.");
            T("h.hvci", "Целостность памяти Windows", "Windows memory integrity");
            T("h.start", "Запуск движка", "Engine start");
            T("h.healed", "Движок удалось запустить после очистки драйвера.", "The engine started after cleaning the driver.");
            T("heal.run", "Запуск не удался, пробую исправить автоматически:", "Start failed, trying to fix it automatically:");
            T("heal.switched", "Переключился на рабочую стратегию:", "Switched to a working strategy:");
            T("ex.admin", "Нет прав администратора — запустите LimixProtocol от имени администратора.", "No administrator rights — run LimixProtocol as administrator.");
            T("ex.blocked", "Драйвер WinDivert, возможно, заблокирован: антивирусом или «Целостностью памяти» Windows (Безопасность Windows → Изоляция ядра). Отключите её или добавьте папку в исключения антивируса.", "The WinDivert driver may be blocked by an antivirus or by Windows “Memory integrity” (Windows Security → Core isolation). Turn it off or exclude the folder in your antivirus.");
            T("ex.sign", "Windows не принял подпись драйвера (код 577). Проверьте Secure Boot и целостность WinDivert64.sys.", "Windows rejected the driver signature (code 577). Check Secure Boot and the integrity of WinDivert64.sys.");
            T("ex.missing", "Не найден файл драйвера или движка. Проверьте папку bin: нужны WinDivert.dll и WinDivert64.sys.", "A driver or engine file is missing. Check the bin folder: WinDivert.dll and WinDivert64.sys are required.");
            T("ex.disabled", "Служба драйвера отключена. Нажмите «Проверить и исправить» на вкладке «Диагностика».", "The driver service is disabled. Press “Check & fix” on the Health tab.");
            T("ex.pending", "Драйвер помечен на удаление. Перезагрузите компьютер или нажмите «Сбросить драйвер WinDivert».", "The driver is marked for deletion. Reboot or press “Reset WinDivert driver”.");
            T("ex.args", "Эта стратегия не поддерживается вашей версией движка — выберите другую или запустите автоподбор.", "This strategy is not supported by your engine version — pick another or run auto-select.");
            T("ex.dll", "Не загружается библиотека (cygwin1.dll или WinDivert.dll). Проверьте, что файлы в bin целые и подходят для 64-битной Windows.", "A library fails to load (cygwin1.dll or WinDivert.dll). Make sure the files in bin are intact and built for 64-bit Windows.");
            T("av.confirm", "Добавить папку программы в исключения Microsoft Defender? Это снижает защиту для этой папки. Делайте это, только если антивирус удаляет файлы движка.", "Add the program folder to Microsoft Defender exclusions? This lowers protection for that folder. Do it only if your antivirus removes the engine files.");
            T("av.done", "Папка добавлена в исключения Defender", "Folder added to Defender exclusions");
            T("set.fx", "Эффект стекла", "Glass effect");
            T("fx.off", "Выкл", "Off");
            T("fx.soft", "Мягкий", "Soft");
            T("fx.strong", "Сильный", "Strong");
            T("set.corners", "Углы", "Corners");
            T("cr.sharp", "Острые", "Sharp");
            T("cr.soft", "Мягкие", "Soft");
            T("cr.round", "Круглые", "Round");
            T("set.anim", "Анимации", "Animations");
            T("set.anim.d", "Переходы между страницами и плавающие блики", "Page transitions and floating glow");
            T("set.promo", "Подсказка про GitHub", "GitHub tip");
            T("set.promo.d", "Небольшое окошко справа внизу при включении защиты", "A small card in the bottom-right corner when you turn the protection on");
            T("set.heal", "Самоисправление при сбое", "Self-repair on failure");
            T("set.heal.d", "Если запуск не удался, программа чистит драйвер и пробует другие стратегии", "If startup fails, the app cleans the driver and tries other strategies");
            T("promo.title", "Нравится LimixProtocol?", "Enjoying LimixProtocol?");
            T("promo.body", "Поставьте ⭐ на GitHub — это помогает проекту. Там же обновления и поддержка.", "Give it a ⭐ on GitHub — it helps the project. Updates and support live there too.");
            T("promo.open", "Открыть GitHub", "Open GitHub");
            T("promo.off", "Отключить", "Turn off");
            T("about.gh", "Открыть GitHub", "Open GitHub");
            T("about.rel", "Релизы", "Releases");
            T("about.upd", "Проверить обновления", "Check for updates");
            T("upd.checking", "Проверяю…", "Checking…");
            T("upd.latest", "У вас последняя версия.", "You have the latest version.");
            T("upd.new", "Доступна новая версия: {0}", "A new version is available: {0}");
            T("upd.none", "Релизов пока нет.", "No releases yet.");
            T("upd.fail", "Не удалось проверить обновления.", "Could not check for updates.");
            T("tip.pin", "Поверх всех окон", "Always on top");
            T("tip.min", "Свернуть", "Minimize");
            T("tip.close", "Закрыть", "Close");
            T("tip.gh", "Открыть проект на GitHub", "Open the project on GitHub");
            T("tray.open", "Открыть", "Open"); T("tray.connect", "Подключить", "Connect"); T("tray.disconnect", "Отключить", "Disconnect"); T("tray.exit", "Выход", "Exit");

            T("svc.note",
              "ChatGPT, Gemini и Claude в ряде стран ограничивают доступ по IP-адресу страны. Обход DPI IP не меняет: если сервис отказывает по стране, нужен VPN.",
              "ChatGPT, Gemini and Claude restrict access by country in some regions. A DPI bypass does not change your IP: if the service refuses your country, you need a VPN.");

            T("about.body",
              "LimixProtocol — менеджер обхода DPI-блокировок. Пакетную работу выполняет движок winws.exe с драйвером WinDivert из папки bin, а LimixProtocol управляет им: сервисы, списки доменов, стратегии, автоподбор, автозапуск и проверка целостности файлов.\n\n" +
              "Как это работает: программа не шифрует и не туннелирует трафик. Она меняет способ отправки пакетов так, чтобы оборудование провайдера не распознало нужный сайт. Провайдер по-прежнему видит, к каким IP-адресам вы подключаетесь.\n\n" +
              "Если сервис заблокирован по IP-адресу или сам ограничивает доступ по стране (так бывает с Telegram, Instagram, Facebook, ChatGPT, Gemini, Claude), одного обхода DPI не хватит — нужен VPN или прокси.\n\n" +
              "Совет: если сайт не открывается, запустите «Автоподбор» на вкладке «Стратегии».",
              "LimixProtocol is a DPI-bypass manager. The packet work is done by the winws.exe engine with the WinDivert driver from the bin folder, and LimixProtocol controls it: services, domain lists, strategies, auto-select, autostart and file integrity checks.\n\n" +
              "How it works: the app does not encrypt or tunnel your traffic. It changes how packets are sent so your ISP's equipment fails to recognize the site. Your ISP can still see which IP addresses you connect to.\n\n" +
              "If a service is blocked by IP address or restricts access by country itself (common for Telegram, Instagram, Facebook, ChatGPT, Gemini, Claude), a DPI bypass alone is not enough — you will need a VPN or proxy.\n\n" +
              "Tip: if a site does not open, run the Auto-select on the Strategies tab.");
        }

        // =====================================================================
        //  Services & strategies
        // =====================================================================
        const string TG_IPS = "91.108.4.0/22 91.108.8.0/22 91.108.12.0/22 91.108.16.0/22 91.108.20.0/22 91.108.56.0/22 149.154.160.0/20 185.76.151.0/24 91.105.192.0/23 95.161.64.0/20";

        static void InitSvcs()
        {
            Svcs.Add(new Svc("youtube", "YouTube", "https://www.youtube.com/", true, "youtube.com youtu.be googlevideo.com ytimg.com ggpht.com youtubei.googleapis.com youtube-nocookie.com yt3.ggpht.com youtubekids.com jnn-pa.googleapis.com", null));
            Svcs.Add(new Svc("discord", "Discord", "https://discord.com/", true, "discord.com discord.gg discordapp.com discordapp.net discord.media discordcdn.com discord.gift discord.new discordstatus.com dis.gd discord.dev", null));
            Svcs.Add(new Svc("telegram", "Telegram", "https://web.telegram.org/", true, "telegram.org t.me telegram.me telegra.ph telesco.pe tdesktop.com telegram.dog cdn-telegram.org", TG_IPS));
            Svcs.Add(new Svc("whatsapp", "WhatsApp", "https://web.whatsapp.com/", true, "whatsapp.com whatsapp.net wa.me", null));
            Svcs.Add(new Svc("instagram", "Instagram", "https://www.instagram.com/", true, "instagram.com cdninstagram.com ig.me instagr.am", null));
            Svcs.Add(new Svc("facebook", "Facebook", "https://www.facebook.com/", true, "facebook.com fb.com fbcdn.net fbsbx.com facebook.net fb.me messenger.com meta.com threads.net", null));
            Svcs.Add(new Svc("chatgpt", "ChatGPT", "https://chatgpt.com/", false, "chatgpt.com openai.com oaistatic.com oaiusercontent.com", null));
            Svcs.Add(new Svc("gemini", "Gemini", "https://gemini.google.com/", false, "gemini.google.com aistudio.google.com ai.google.dev generativelanguage.googleapis.com notebooklm.google.com makersuite.google.com bard.google.com deepmind.google labs.google", null));
            Svcs.Add(new Svc("claude", "Claude", "https://claude.ai/", false, "claude.ai claude.com anthropic.com claudeusercontent.com", null));
            Svcs.Add(new Svc("tiktok", "TikTok", "https://www.tiktok.com/", false, "tiktok.com tiktokcdn.com tiktokv.com tiktokcdn-us.com ttwstatic.com byteoversea.com ibytedtos.com muscdn.com musical.ly ibyteimg.com", null));
            Svcs.Add(new Svc("x", "X (Twitter)", "https://x.com/", false, "twitter.com x.com t.co twimg.com", null));
            Svcs.Add(new Svc("twitch", "Twitch", "https://www.twitch.tv/", false, "twitch.tv ttvnw.net jtvnw.net twitchcdn.net twitchsvc.net", null));
            Svcs.Add(new Svc("spotify", "Spotify", "https://open.spotify.com/", false, "spotify.com scdn.co spotifycdn.com spoti.fi", null));
            Svcs.Add(new Svc("soundcloud", "SoundCloud", "https://soundcloud.com/", false, "soundcloud.com sndcdn.com", null));
            Svcs.Add(new Svc("reddit", "Reddit", "https://www.reddit.com/", false, "reddit.com redd.it redditstatic.com redditmedia.com", null));
            Svcs.Add(new Svc("pinterest", "Pinterest", "https://www.pinterest.com/", false, "pinterest.com pinimg.com", null));
        }

        static bool SvcOn(string id)
        {
            foreach (Svc s in Svcs) if (s.Id == id) return CB("svc." + id, s.Def);
            return false;
        }
        static int SvcCount() { int n = 0; foreach (Svc s in Svcs) if (SvcOn(s.Id)) n++; return n; }

        const string TLS = "--dpi-desync-fake-tls=\"{BIN}tls_clienthello_www_google_com.bin\"";
        const string QUIC = "--filter-udp=443 {HL} --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fake-quic=\"{BIN}quic_initial_www_google_com.bin\"";
        const string DISC = "--filter-udp=50000-50100 --filter-l7=discord,stun --dpi-desync=fake --dpi-desync-repeats=6";
        const string H80 = "--filter-tcp=80 {HL} --dpi-desync=fake,fakedsplit --dpi-desync-autottl=2 --dpi-desync-fooling=md5sig";

        static void AddStrat(string id, string nameKey, int num, string descKey, params string[] st)
        {
            Strat s = new Strat();
            s.Id = id; s.NameKey = nameKey; s.Num = num; s.DescKey = descKey; s.St = st;
            Strats.Add(s);
        }

        static void InitStrats()
        {
            AddStrat("std", "str.std", 0, "sd.1", QUIC, DISC, H80,
                "--filter-tcp=443 {HL} --dpi-desync=fake,multidisorder --dpi-desync-split-pos=1,midsld --dpi-desync-repeats=8 --dpi-desync-fooling=md5sig,badseq " + TLS);
            AddStrat("alt1", "str.alt", 1, "sd.2", QUIC, DISC, H80,
                "--filter-tcp=443 {HL} --dpi-desync=fake,fakedsplit --dpi-desync-split-pos=1 --dpi-desync-autottl=5 --dpi-desync-fooling=md5sig " + TLS);
            AddStrat("alt2", "str.alt", 2, "sd.3", QUIC, DISC, H80,
                "--filter-tcp=443 {HL} --dpi-desync=multisplit --dpi-desync-split-seqovl=652 --dpi-desync-split-pos=2 --dpi-desync-split-seqovl-pattern=\"{BIN}tls_clienthello_www_google_com.bin\"");
            AddStrat("alt3", "str.alt", 3, "sd.4", QUIC, DISC, H80,
                "--filter-tcp=443 {HL} --dpi-desync=fake,split2 --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-fooling=badseq --dpi-desync-badseq-increment=2 --dpi-desync-repeats=8 " + TLS);
            AddStrat("alt4", "str.alt", 4, "sd.5", QUIC, DISC, H80,
                "--filter-tcp=443 {HL} --dpi-desync=fake,hostfakesplit --dpi-desync-fooling=ts --dpi-desync-hostfakesplit-mod=host=www.google.com --dpi-desync-repeats=6");
        }

        static string SName(Strat s)
        {
            return s.Num == 0 ? L(s.NameKey) : L(s.NameKey) + " " + s.Num;
        }

        static Strat CurStrat()
        {
            string id = CG("strategy", "std");
            foreach (Strat s in Strats) if (s.Id == id) return s;
            return Strats[0];
        }

        // =====================================================================
        //  Lists and engine arguments
        // =====================================================================
        static List<string> ReadLines(string p)
        {
            List<string> r = new List<string>();
            try
            {
                if (File.Exists(p))
                    foreach (string l in File.ReadAllLines(p))
                    {
                        string t = l.Trim();
                        if (t.Length > 0 && !t.StartsWith("#")) r.Add(t);
                    }
            }
            catch { }
            return r;
        }

        static string NormDomain(string s)
        {
            if (s == null) return null;
            s = s.Trim().ToLowerInvariant();
            int i = s.IndexOf("://", StringComparison.Ordinal);
            if (i >= 0) s = s.Substring(i + 3);
            foreach (char c in new char[] { '/', '?', '#', ':' })
            {
                int k = s.IndexOf(c);
                if (k >= 0) s = s.Substring(0, k);
            }
            if (s.StartsWith("*.")) s = s.Substring(2);
            s = s.Trim('.');
            return Regex.IsMatch(s, @"^[a-z0-9_\-]+(\.[a-z0-9_\-]+)+$") ? s : null;
        }

        static void LoadDomainFiles()
        {
            custom = ReadLines(Path.Combine(Root, "limix-custom.txt"));
            excl = ReadLines(Path.Combine(Root, "limix-excluded.txt"));
        }

        static void SaveDomains()
        {
            try
            {
                File.WriteAllLines(Path.Combine(Root, "limix-custom.txt"), custom.ToArray(), Encoding.UTF8);
                File.WriteAllLines(Path.Combine(Root, "limix-excluded.txt"), excl.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        static bool BuildLists()
        {
            SortedSet<string> h = new SortedSet<string>();
            foreach (Svc s in Svcs) if (SvcOn(s.Id)) foreach (string d in s.Dom) h.Add(d);
            foreach (string d in custom) h.Add(d);
            SortedSet<string> x = new SortedSet<string>();
            x.Add("limix-placeholder.invalid"); // keeps the exclusion file non-empty
            foreach (string d in excl) x.Add(d);
            List<string> ips = new List<string>();
            foreach (Svc s in Svcs) if (SvcOn(s.Id)) ips.AddRange(s.Ips);
            try
            {
                File.WriteAllLines(HostsFile, h.ToArray());
                File.WriteAllLines(ExclFile, x.ToArray());
                File.WriteAllLines(IpFile, ips.ToArray());
            }
            catch (Exception ex) { Log("lists: " + ex.Message); }
            return h.Count > 0;
        }

        static string BuildArgs(Strat s)
        {
            string bin = BinDir + "\\";
            string hl = "--hostlist=\"" + HostsFile + "\" --hostlist-exclude=\"" + ExclFile + "\"";
            string il = "--ipset=\"" + IpFile + "\"";
            bool ip = CB("ip", true) && File.Exists(IpFile) && new FileInfo(IpFile).Length > 0;
            List<string> parts = new List<string>();
            foreach (string st in s.St) parts.Add(st.Replace("{HL}", hl).Replace("{BIN}", bin));
            if (ip) foreach (string st in s.St) if (st.Contains("{HL}")) parts.Add(st.Replace("{HL}", il).Replace("{BIN}", bin));
            return "--wf-tcp=80,443 --wf-udp=443,50000-50100 " + string.Join(" --new ", parts.ToArray());
        }

        // =====================================================================
        //  Helpers: processes, logging, UI dispatch
        // =====================================================================
        static string Sh(string exe, string args, out int code)
        {
            code = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(10000)) { try { p.Kill(); } catch { } }
                    else code = p.ExitCode;
                    return o;
                }
            }
            catch (Exception ex) { return "ERR " + ex.Message; }
        }
        static string Sh(string exe, string args) { int c; return Sh(exe, args, out c); }

        static StringBuilder logSb = new StringBuilder();
        static void Log(string s)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + s;
            string snap = null;
            lock (logSb)
            {
                logSb.AppendLine(line);
                if (logSb.Length > 200000) { logSb.Remove(0, 100000); snap = logSb.ToString(); }
            }
            UI(delegate
            {
                TextBox lb = Find<TextBox>("LogBox");
                if (lb == null) return;
                if (snap != null) lb.Text = snap; else lb.AppendText(line + "\r\n");
                lb.ScrollToEnd();
            });
        }

        static void UI(Action a)
        {
            Window w = win;
            if (w == null) return;
            if (w.Dispatcher.CheckAccess()) a(); else w.Dispatcher.BeginInvoke(a);
        }

        static T Find<T>(string name) where T : class
        {
            return win == null ? null : win.FindName(name) as T;
        }

        static Color Col(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }

        // =====================================================================
        //  Engine control
        // =====================================================================
        static bool EngineRunning
        {
            get { try { return eng != null && !eng.HasExited; } catch { return false; } }
        }

        static void SetState(string s)
        {
            string prev = state;
            state = s;
            if (s == "off") lock (chipState) { chipState.Clear(); }
            UI(delegate { RefreshState(); BuildChips(); if (s == "on" && prev != "on") MaybePromo(); });
        }

        static void KillAllWinws()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("winws"))
                {
                    try { p.Kill(); p.WaitForExit(2000); } catch { }
                }
            }
            catch { }
        }

        static void ResetDriver()
        {
            foreach (string n in new string[] { "WinDivert", "WinDivert14" })
            {
                Sh("sc.exe", "stop " + n);
                Sh("sc.exe", "delete " + n);
            }
        }

        static Process StartWinws(Strat s, out string err)
        {
            err = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(BinDir, "winws.exe"), BuildArgs(s));
                psi.WorkingDirectory = BinDir;
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                Process p = Process.Start(psi);
                p.OutputDataReceived += (o, e) => { if (e.Data != null) EngLine(e.Data); };
                p.ErrorDataReceived += (o, e) => { if (e.Data != null) EngLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                return p;
            }
            catch (Exception ex) { err = ex.Message; return null; }
        }

        static void Fail(string msg)
        {
            Log("✖ " + msg);
            promoArm = false;
            SetState("off");
            UI(delegate
            {
                if (win.IsVisible) MessageBox.Show(win, msg, "LimixProtocol", MessageBoxButton.OK, MessageBoxImage.Warning);
                else if (tray != null && tray.Visible) tray.ShowBalloonTip(4000, "LimixProtocol", msg, System.Windows.Forms.ToolTipIcon.Warning);
            });
        }

        static void DoConnect()
        {
            lock (engLock)
            {
                if (EngineRunning) return;
                SetState("starting");
                if (BinDir == null) { Fail(L("err.nobin") + "\n" + Root); return; }

                int hs = HashState();
                if (hs == 0) { PinHashes(); Log(L("log.pinned")); }
                else if (hs == 2 && CB("verify", true)) { Fail(L("err.hash")); RefreshHashAsync(); return; }

                if (Process.GetProcessesByName("winws").Length > 0) { KillAllWinws(); Log(L("log.killed")); }
                if (!BuildLists()) Log(L("log.nohosts"));

                Strat s = CurStrat();
                engStopping = false;
                string err;
                Process p = TryStart(s, out err);
                if (p == null && CB("heal", true))
                {
                    Log(L("heal.run") + " " + (err ?? "").Replace("\n", " | "));
                    QuickHeal();
                    p = TryStart(s, out err);
                    if (p == null)
                    {
                        foreach (Strat alt in new List<Strat>(Strats))
                        {
                            if (alt.Id == s.Id) continue;
                            string e2;
                            p = TryStart(alt, out e2);
                            if (p != null)
                            {
                                s = alt; CS("strategy", alt.Id);
                                Log(L("heal.switched") + " " + SName(alt));
                                UI(BuildStrategies);
                                break;
                            }
                        }
                    }
                }
                if (p == null) { Fail(err); return; }

                eng = p; engStart = DateTime.Now;
                p.EnableRaisingEvents = true;
                p.Exited += OnEngExit;
                Log(L("log.started") + " " + SName(s));
                SetState("on");
            }
            RefreshHashAsync();
            CheckChips();
        }

        static void StopEngine(bool quiet)
        {
            lock (engLock)
            {
                engStopping = true;
                try { if (eng != null && !eng.HasExited) { eng.Kill(); eng.WaitForExit(3000); } } catch { }
                eng = null;
                KillAllWinws();
                if (!quiet)
                {
                    ResetDriver();
                    Log(L("log.stopped"));
                    SetState("off");
                }
            }
        }

        static void OnEngExit(object sender, EventArgs e)
        {
            if (engStopping || testing) return;
            Log(L("log.crash"));
            SetState("off");
            if ((DateTime.Now - lastCrash).TotalMinutes > 2) restarts = 0;
            lastCrash = DateTime.Now;
            if (CB("watch", true) && restarts < 3)
            {
                restarts++;
                ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(2000); DoConnect(); });
            }
        }

        static void Toggle()
        {
            if (testing) return;
            if (EngineRunning) ThreadPool.QueueUserWorkItem(delegate { StopEngine(false); });
            else { promoArm = true; ThreadPool.QueueUserWorkItem(delegate { DoConnect(); }); }
        }

        static void ScheduleApply()
        {
            if (!EngineRunning || applyTimer == null) return;
            applyTimer.Stop(); applyTimer.Start();
        }

        static void Touch() { RefreshState(); BuildChips(); ScheduleApply(); }

        // =====================================================================
        //  Integrity (SHA-256 pinning)
        // =====================================================================
        static string Sha(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        // 0 = no baseline, 1 = ok, 2 = mismatch
        static int HashState()
        {
            if (BinDir == null) return 0;
            bool any = false;
            foreach (string f in Guard)
            {
                string pin = CG("hash." + f, null);
                if (pin == null) continue;
                any = true;
                string p = Path.Combine(BinDir, f);
                if (!File.Exists(p) || Sha(p) != pin) return 2;
            }
            return any ? 1 : 0;
        }

        static void PinHashes()
        {
            if (BinDir == null) return;
            foreach (string f in Guard)
            {
                string p = Path.Combine(BinDir, f);
                if (File.Exists(p)) cfg["hash." + f] = Sha(p);
            }
            SaveCfg();
        }

        static void RefreshHashAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                int hs = 0; StringBuilder sb = new StringBuilder();
                try
                {
                    hs = HashState();
                    if (BinDir != null)
                        foreach (string f in Guard)
                        {
                            string p = Path.Combine(BinDir, f);
                            sb.AppendLine(f.PadRight(18) + (File.Exists(p) ? Sha(p).Substring(0, 24) + "…" : "—"));
                        }
                }
                catch { }
                UI(delegate
                {
                    TextBlock st = Find<TextBlock>("HashStatus"), info = Find<TextBlock>("HashInfo");
                    if (st == null) return;
                    st.Text = L(hs == 1 ? "hash.ok" : (hs == 2 ? "hash.bad" : "hash.none"));
                    st.Foreground = new SolidColorBrush(hs == 1 ? Col("#30A46C") : (hs == 2 ? Col("#E5484D") : Col("#9498A1")));
                    info.Text = sb.ToString().TrimEnd();
                });
            });
        }

        // =====================================================================
        //  Autostart (Task Scheduler: runs elevated at logon without a UAC prompt)
        // =====================================================================
        const string TaskName = "LimixProtocol";

        static bool IsAutostart() { int c; Sh("schtasks.exe", "/Query /TN \"" + TaskName + "\"", out c); return c == 0; }

        static string TaskXml(string exe)
        {
            string user = System.Security.SecurityElement.Escape(System.Security.Principal.WindowsIdentity.GetCurrent().Name);
            string e = System.Security.SecurityElement.Escape(exe);
            string d = System.Security.SecurityElement.Escape(Path.GetDirectoryName(exe));
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<RegistrationInfo><Description>LimixProtocol</Description></RegistrationInfo>" +
                "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>" +
                "<Principals><Principal id=\"Author\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable>" +
                "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled>" +
                "<Hidden>false</Hidden><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>" +
                "<Actions Context=\"Author\"><Exec><Command>" + e + "</Command><Arguments>--silent</Arguments><WorkingDirectory>" + d + "</WorkingDirectory></Exec></Actions></Task>";
        }

        static void SetAutostart(bool on)
        {
            int c;
            if (on)
            {
                string tmp = Path.Combine(Path.GetTempPath(), "limix-task.xml");
                try { File.WriteAllText(tmp, TaskXml(Assembly.GetExecutingAssembly().Location), Encoding.Unicode); } catch { }
                string o = Sh("schtasks.exe", "/Create /TN \"" + TaskName + "\" /XML \"" + tmp + "\" /F", out c);
                try { File.Delete(tmp); } catch { }
                if (c != 0) Log("schtasks: " + o.Trim());
            }
            else Sh("schtasks.exe", "/Delete /TN \"" + TaskName + "\" /F", out c);
        }

        // =====================================================================
        //  Auto-test of strategies
        // =====================================================================
        static bool Reach(string url)
        {
            try
            {
                HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
                r.Timeout = 6000; r.ReadWriteTimeout = 6000; r.Proxy = null; r.KeepAlive = false; r.AllowAutoRedirect = false;
                r.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                using (WebResponse w = r.GetResponse()) { return true; }
            }
            catch (WebException ex) { return ex.Response != null; }
            catch { return false; }
        }

        static int Probe(List<Svc> en)
        {
            int ok = 0;
            List<Thread> ths = new List<Thread>();
            foreach (Svc s in en)
            {
                Svc sv = s;
                Thread th = new Thread(() => { if (Reach(sv.Test)) Interlocked.Increment(ref ok); });
                th.IsBackground = true; th.Start(); ths.Add(th);
            }
            foreach (Thread th2 in ths) th2.Join(9000);
            return ok;
        }

        static void SetProg(double f)
        {
            Border pf = Find<Border>("ProgFill");
            if (pf == null) return;
            ScaleTransform st = pf.RenderTransform as ScaleTransform;
            if (st == null) return;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(f, TimeSpan.FromMilliseconds(300)));
        }

        static void AddResult(string name, int score, int total)
        {
            StackPanel res = Find<StackPanel>("TestResults");
            if (res == null) return;
            TextBlock tb = new TextBlock();
            tb.Margin = new Thickness(0, 0, 0, 3);
            if (score < 0) { tb.Text = "✖  " + name + " — " + L("test.err"); tb.SetResourceReference(TextBlock.ForegroundProperty, "Dim"); }
            else
            {
                tb.Text = (score == total ? "✔  " : "•  ") + name + " — " + score + "/" + total;
                if (score == 0) tb.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            }
            res.Children.Add(tb);
        }

        static void RunTest()
        {
            if (testing) return;
            TextBlock status = Find<TextBlock>("TestStatus");
            List<Svc> en = Svcs.Where(x => SvcOn(x.Id) && x.Test != null).ToList();
            if (en.Count == 0) { status.Text = L("test.need"); return; }
            if (BinDir == null) { status.Text = L("err.nobin"); return; }
            testing = true; bestId = null;
            Find<Button>("AutoBtn").IsEnabled = false;
            Find<Button>("ApplyBest").Visibility = Visibility.Collapsed;
            Find<StackPanel>("TestResults").Children.Clear();
            bool was = EngineRunning;
            List<Strat> list = new List<Strat>(Strats);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (was) StopEngine(true);
                    BuildLists();
                    int n = list.Count, bestScore = 0;
                    string bestName = null;
                    for (int i = 0; i < n; i++)
                    {
                        Strat s = list[i];
                        int idx = i;
                        UI(delegate { status.Text = L("test.running") + " " + SName(s) + "  (" + (idx + 1) + "/" + n + ")"; SetProg((double)idx / n); });
                        string err;
                        int score = -1;
                        Process p = StartWinws(s, out err);
                        if (p != null)
                        {
                            Thread.Sleep(2300);
                            if (!p.HasExited) score = Probe(en);
                            try { if (!p.HasExited) p.Kill(); p.WaitForExit(2500); } catch { }
                        }
                        int sc = score;
                        if (sc > bestScore) { bestScore = sc; bestId = s.Id; bestName = SName(s); }
                        UI(delegate { AddResult(SName(s), sc, en.Count); });
                    }
                    int bs = bestScore; string bn = bestName; int total = en.Count;
                    UI(delegate
                    {
                        SetProg(1);
                        if (bs > 0)
                        {
                            status.Text = string.Format(L("test.best"), bn, bs, total);
                            Find<Button>("ApplyBest").Visibility = Visibility.Visible;
                        }
                        else status.Text = L("test.none");
                    });
                }
                finally
                {
                    KillAllWinws();
                    testing = false;
                    if (was) DoConnect();
                    UI(delegate { Find<Button>("AutoBtn").IsEnabled = true; RefreshState(); });
                }
            });
        }

        // =====================================================================
        //  Theme / language
        // =====================================================================
        static bool IsDark()
        {
            string t = CG("theme", "system");
            if (t == "dark") return true;
            if (t == "light") return false;
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
                return v is int && (int)v == 0;
            }
            catch { return true; }
        }

        static string AccentHex()
        {
            string id = CG("accent", "red");
            foreach (string[] a in Accents) if (a[0] == id) return a[1];
            return Accents[0][1];
        }

        static void ApplyTheme()
        {
            bool d = IsDark();
            string fx = FxLevel;
            bool glass = fx != "off";
            double k = fx == "strong" ? 1.0 : 0.62;
            string[] keys = { "Bg", "Sf", "Sf2", "Bd", "Tx", "Dim", "Hv" };
            string[] pal = d
                ? new string[] { "#0E0F11", "#16171A", "#1E2024", "#2A2D33", "#ECEDEF", "#9498A1", "#272A30" }
                : new string[] { "#F3F4F7", "#FFFFFF", "#EEF0F4", "#DADDE3", "#15171C", "#5B6270", "#E4E7EC" };
            for (int i = 0; i < keys.Length; i++) win.Resources[keys[i]] = new SolidColorBrush(Col(pal[i]));
            Color ac = Col(AccentHex());
            win.Resources["Ac"] = new SolidColorBrush(ac);
            win.Resources["AcSoft"] = new SolidColorBrush(Color.FromArgb((byte)(d ? 46 : 38), ac.R, ac.G, ac.B));
            double lum = (0.299 * ac.R + 0.587 * ac.G + 0.114 * ac.B) / 255.0;
            win.Resources["AcFg"] = new SolidColorBrush(lum > 0.6 ? Col("#14151A") : Colors.White);
            if (glass)
            {
                win.Resources["Sf2"] = new SolidColorBrush(d ? Rgba(255, 255, 255, 0.08 * k + 0.02) : Rgba(0, 0, 0, 0.05));
                win.Resources["Hv"] = new SolidColorBrush(d ? Rgba(255, 255, 255, 0.14 * k + 0.03) : Rgba(0, 0, 0, 0.09));
                win.Resources["Bd"] = new SolidColorBrush(d ? Rgba(255, 255, 255, 0.16 * k + 0.03) : Rgba(0, 0, 0, 0.10));
                win.Resources["CardBg"] = d
                    ? Grad(Rgba(255, 255, 255, 0.17 * k), Rgba(255, 255, 255, 0.05 * k))
                    : Grad(Rgba(255, 255, 255, 0.92 - 0.12 * k), Rgba(255, 255, 255, 0.78 - 0.28 * k));
                win.Resources["CardBd"] = d
                    ? Grad(Rgba(255, 255, 255, 0.42 * k), Rgba(255, 255, 255, 0.08 * k), Rgba(255, 255, 255, 0.22 * k))
                    : Grad(Rgba(255, 255, 255, 0.95), Rgba(0, 0, 0, 0.08), Rgba(255, 255, 255, 0.70));
                win.Resources["SideBg"] = new SolidColorBrush(d ? Rgba(255, 255, 255, 0.045) : Rgba(255, 255, 255, 0.55));
            }
            else
            {
                win.Resources["CardBg"] = new SolidColorBrush(Col(pal[1]));
                win.Resources["CardBd"] = new SolidColorBrush(Col(pal[3]));
                win.Resources["SideBg"] = new SolidColorBrush(Col(pal[1]));
            }
            ApplyCorners();
            ApplyAurora(d, k, glass);
            RefreshPower();
        }

        static void SetTheme(string t) { CS("theme", t); ApplyTheme(); }

        static void SetLang(string l)
        {
            CS("lang", l);
            ApplyLang();
            BuildServices();
            BuildStrategies();
            BuildSwatches();
            BuildChips();
            RefreshHashAsync();
        }

        static void Localize(DependencyObject o)
        {
            FrameworkElement fe = o as FrameworkElement;
            string tag = fe != null ? fe.Tag as string : null;
            if (tag != null && S.ContainsKey(tag))
            {
                TextBlock tb = o as TextBlock;
                if (tb != null) tb.Text = L(tag);
                else
                {
                    ContentControl cc = o as ContentControl;
                    if (cc != null) cc.Content = L(tag);
                }
            }
            foreach (object c in LogicalTreeHelper.GetChildren(o))
            {
                DependencyObject d = c as DependencyObject;
                if (d != null) Localize(d);
            }
        }

        static void ApplyLang()
        {
            Localize(win);
            SetTips();
            if (miOpen != null) { miOpen.Text = L("tray.open"); miExit.Text = L("tray.exit"); }
            RefreshState();
        }

        // =====================================================================
        //  UI refresh
        // =====================================================================
        static void SetPulse(bool on)
        {
            if (on == pulseOn) return;
            pulseOn = on;
            Ellipse pr = Find<Ellipse>("PulseRing");
            if (pr == null) return;
            ScaleTransform st = pr.RenderTransform as ScaleTransform;
            if (st == null) { st = new ScaleTransform(1, 1); pr.RenderTransform = st; pr.RenderTransformOrigin = new Point(0.5, 0.5); }
            if (on)
            {
                Duration dur = new Duration(TimeSpan.FromSeconds(2));
                DoubleAnimation grow = new DoubleAnimation(1.0, 1.38, dur); grow.RepeatBehavior = RepeatBehavior.Forever;
                DoubleAnimation fade = new DoubleAnimation(0.55, 0.0, dur); fade.RepeatBehavior = RepeatBehavior.Forever;
                st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                pr.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            else
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                pr.BeginAnimation(UIElement.OpacityProperty, null);
                pr.Opacity = 0;
            }
        }

        static void RefreshPower()
        {
            if (win == null) return;
            Ellipse ring = Find<Ellipse>("PowerRing"), dot = Find<Ellipse>("SideDot");
            TextBlock glyph = Find<TextBlock>("PowerGlyph");
            if (ring == null) return;
            bool on = state == "on", starting = state == "starting";
            string key = on ? "Ac" : (starting ? "Dim" : "Bd");
            ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, key);
            glyph.SetResourceReference(TextBlock.ForegroundProperty, on ? "Ac" : "Dim");
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, on ? "Ac" : "Dim");
            if (on)
            {
                DropShadowEffect fx = new DropShadowEffect();
                fx.Color = ((SolidColorBrush)win.Resources["Ac"]).Color;
                fx.BlurRadius = 48; fx.ShadowDepth = 0; fx.Opacity = 0.7;
                ring.Effect = fx;
            }
            else ring.Effect = null;
            SetPulse(on);
        }

        static void RefreshState()
        {
            if (win == null) return;
            bool on = state == "on", starting = state == "starting";
            string sk = on ? "st.on" : (starting ? "st.starting" : "st.off");
            string hk = on ? "hint.on" : (starting ? "hint.starting" : "hint.off");
            Find<TextBlock>("StateTitle").Text = L(sk);
            Find<TextBlock>("StateHint").Text = L(hk);
            Find<TextBlock>("SideState").Text = L(sk);
            Find<TextBlock>("StatStrat").Text = SName(CurStrat());
            Find<TextBlock>("StatSvc").Text = SvcCount() + " / " + Svcs.Count;
            Find<TextBlock>("StatAuto").Text = L(Find<ToggleButton>("SwAuto").IsChecked == true ? "w.on" : "w.off");
            Find<TextBlock>("StatUptime").Text = on ? Uptime() : "—";
            if (miToggle != null) miToggle.Text = L(on ? "tray.disconnect" : "tray.connect");
            if (tray != null) tray.Text = "LimixProtocol — " + L(sk);
            RefreshPower();
        }

        static string Uptime()
        {
            TimeSpan ts = DateTime.Now - engStart;
            return ((int)ts.TotalHours).ToString("00") + ts.ToString(@"\:mm\:ss");
        }

        static void ShowPage(string name)
        {
            int idx = Array.IndexOf(PageNames, name);
            int dir = idx >= curPage ? 1 : -1;
            bool anim = CB("anim", true);
            foreach (string p in PageNames)
            {
                UIElement el = Find<UIElement>("Pg" + p);
                if (el == null) continue;
                if (p == name)
                {
                    if (el.Visibility == Visibility.Visible) continue;
                    el.Visibility = Visibility.Visible;
                    if (anim) Enter(el, dir);
                }
                else el.Visibility = Visibility.Collapsed;
            }
            curPage = idx;
            if (anim)
            {
                if (name == "Services") Stagger(Find<Panel>("SvcPanel"));
                else if (name == "Strategies") Stagger(Find<Panel>("StratPanel"));
            }
        }

        static void Enter(UIElement el, int dir)
        {
            TranslateTransform tt = new TranslateTransform(dir * 40, 0);
            el.RenderTransform = tt;
            CubicEase ease = new CubicEase(); ease.EasingMode = EasingMode.EaseOut;
            DoubleAnimation mv = new DoubleAnimation(dir * 40, 0, TimeSpan.FromMilliseconds(420)); mv.EasingFunction = ease;
            tt.BeginAnimation(TranslateTransform.XProperty, mv);
            el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)));
        }

        static void Stagger(Panel p)
        {
            if (p == null) return;
            int i = 0;
            foreach (UIElement c in p.Children)
            {
                TimeSpan delay = TimeSpan.FromMilliseconds(70 + i * 45);
                TranslateTransform tt = new TranslateTransform(0, 22);
                c.RenderTransform = tt;
                c.Opacity = 0;
                CubicEase ease = new CubicEase(); ease.EasingMode = EasingMode.EaseOut;
                DoubleAnimation mv = new DoubleAnimation(22, 0, TimeSpan.FromMilliseconds(420)); mv.BeginTime = delay; mv.EasingFunction = ease;
                tt.BeginAnimation(TranslateTransform.YProperty, mv);
                DoubleAnimation fd = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)); fd.BeginTime = delay;
                c.BeginAnimation(UIElement.OpacityProperty, fd);
                i++;
            }
        }

        static void Burst()
        {
            if (!CB("anim", true)) return;
            Ellipse b = Find<Ellipse>("BurstRing");
            if (b == null) return;
            ScaleTransform st = new ScaleTransform(1, 1);
            b.RenderTransform = st; b.RenderTransformOrigin = new Point(0.5, 0.5);
            CubicEase ease = new CubicEase(); ease.EasingMode = EasingMode.EaseOut;
            DoubleAnimation g = new DoubleAnimation(1, 1.6, TimeSpan.FromMilliseconds(700)); g.EasingFunction = ease;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, g);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, g);
            b.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.85, 0, TimeSpan.FromMilliseconds(700)));
        }

        // =====================================================================
        //  Dynamic UI blocks
        // =====================================================================
        static TextBlock MakeText(string text, bool bold, double size, string brushKey)
        {
            TextBlock t = new TextBlock();
            t.Text = text; t.FontSize = size;
            if (bold) t.FontWeight = FontWeights.SemiBold;
            if (brushKey != null) t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return t;
        }

        static void BuildServices()
        {
            Panel p = Find<Panel>("SvcPanel");
            if (p == null) return;
            p.Children.Clear();
            foreach (Svc s in Svcs)
            {
                Svc sv = s;
                Border b = new Border();
                b.Style = (Style)win.FindResource("Card");
                b.Width = 250; b.Margin = new Thickness(0, 0, 14, 14); b.Padding = new Thickness(14);
                Grid g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                Border badge = new Border();
                badge.Width = 38; badge.Height = 38;
                badge.SetResourceReference(Border.CornerRadiusProperty, "RCtl");
                badge.SetResourceReference(Border.BackgroundProperty, "AcSoft");
                TextBlock ini = MakeText(sv.Name.Substring(0, 1), false, 17, "Ac");
                ini.FontWeight = FontWeights.Bold; ini.HorizontalAlignment = HorizontalAlignment.Center; ini.VerticalAlignment = VerticalAlignment.Center;
                badge.Child = ini;

                StackPanel sp = new StackPanel();
                sp.Margin = new Thickness(12, 0, 8, 0); sp.VerticalAlignment = VerticalAlignment.Center;
                sp.Children.Add(MakeText(sv.Name, true, 13, null));
                sp.Children.Add(MakeText(sv.Dom.Length + " " + L("svc.domains"), false, 12, "Dim"));

                ToggleButton sw = new ToggleButton();
                sw.Style = (Style)win.FindResource("Switch");
                sw.IsChecked = SvcOn(sv.Id); sw.VerticalAlignment = VerticalAlignment.Center;
                sw.Click += delegate { CSB("svc." + sv.Id, sw.IsChecked == true); Touch(); };

                Grid.SetColumn(sp, 1); Grid.SetColumn(sw, 2);
                g.Children.Add(badge); g.Children.Add(sp); g.Children.Add(sw);
                b.Child = g;
                p.Children.Add(b);
            }
        }

        static void SetAllServices(bool on)
        {
            foreach (Svc s in Svcs) cfg["svc." + s.Id] = on ? "1" : "0";
            SaveCfg();
            BuildServices();
            Touch();
        }

        static void BuildStrategies()
        {
            StackPanel p = Find<StackPanel>("StratPanel");
            if (p == null) return;
            p.Children.Clear();
            string cur = CurStrat().Id;
            foreach (Strat s in Strats)
            {
                Strat st = s;
                RadioButton rb = new RadioButton();
                rb.GroupName = "strat";
                rb.Style = (Style)win.FindResource("StratCard");
                rb.IsChecked = (st.Id == cur);
                StackPanel sp = new StackPanel();
                sp.Children.Add(MakeText(SName(st), true, 14, null));
                TextBlock d = MakeText(L(st.DescKey), false, 12, "Dim");
                d.TextWrapping = TextWrapping.Wrap; d.Margin = new Thickness(0, 3, 30, 0);
                sp.Children.Add(d);
                rb.Content = sp;
                rb.Checked += delegate { CS("strategy", st.Id); Touch(); };
                p.Children.Add(rb);
            }
        }

        static void BuildSwatches()
        {
            Panel p = Find<Panel>("SwatchPanel");
            if (p == null) return;
            p.Children.Clear();
            string cur = CG("accent", "red");
            foreach (string[] a in Accents)
            {
                string id = a[0];
                RadioButton rb = new RadioButton();
                rb.GroupName = "acc";
                rb.Style = (Style)win.FindResource("Swatch");
                rb.Background = new SolidColorBrush(Col(a[1]));
                rb.ToolTip = L("acc." + id);
                rb.IsChecked = (id == cur);
                rb.Checked += delegate { CS("accent", id); ApplyTheme(); RefreshState(); };
                p.Children.Add(rb);
            }
        }

        static Action WireDomains(string inName, string addName, string listName, List<string> data)
        {
            TextBox tb = Find<TextBox>(inName);
            Button add = Find<Button>(addName);
            StackPanel list = Find<StackPanel>(listName);
            Action refresh = null;
            refresh = delegate
            {
                list.Children.Clear();
                foreach (string d in data.ToArray())
                {
                    string dom = d;
                    Grid row = new Grid();
                    row.Margin = new Thickness(0, 0, 0, 4);
                    row.SetResourceReference(Grid.BackgroundProperty, "Sf2");
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    TextBlock t = MakeText(dom, false, 13, null);
                    t.Margin = new Thickness(12, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center;
                    Button rm = new Button();
                    rm.Style = (Style)win.FindResource("Mini");
                    Grid.SetColumn(rm, 1);
                    rm.Click += delegate { data.Remove(dom); SaveDomains(); refresh(); Touch(); };
                    row.Children.Add(t); row.Children.Add(rm);
                    list.Children.Add(row);
                }
            };
            Action doAdd = delegate
            {
                string n = NormDomain(tb.Text);
                if (n != null && !data.Contains(n)) { data.Add(n); data.Sort(); SaveDomains(); refresh(); Touch(); }
                tb.Text = ""; tb.Focus();
            };
            add.Click += delegate { doAdd(); };
            tb.KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Enter) doAdd(); };
            refresh();
            return refresh;
        }

        static void BindSwitch(string name, string key, bool def, Action after)
        {
            ToggleButton t = Find<ToggleButton>(name);
            t.IsChecked = CB(key, def);
            t.Click += delegate { CSB(key, t.IsChecked == true); if (after != null) after(); };
        }

        // =====================================================================
        //  Tray / window
        // =====================================================================
        static void ShowWin()
        {
            UI(delegate
            {
                win.Show();
                if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
                win.Activate();
                win.Topmost = true; win.Topmost = false;
            });
        }

        static void ApplyTray() { if (tray != null) tray.Visible = CB("tray", true); }

        static void SetupTray()
        {
            tray = new System.Windows.Forms.NotifyIcon();
            try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }
            tray.Text = "LimixProtocol";
            System.Windows.Forms.ContextMenuStrip menu = new System.Windows.Forms.ContextMenuStrip();
            miOpen = new System.Windows.Forms.ToolStripMenuItem(L("tray.open"));
            miToggle = new System.Windows.Forms.ToolStripMenuItem(L("tray.connect"));
            miExit = new System.Windows.Forms.ToolStripMenuItem(L("tray.exit"));
            miOpen.Click += delegate { ShowWin(); };
            miToggle.Click += delegate { Toggle(); };
            miExit.Click += delegate { UI(Quit); };
            menu.Items.Add(miOpen);
            menu.Items.Add(miToggle);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(miExit);
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowWin(); };
            ApplyTray();
        }

        static void Quit()
        {
            quitting = true;
            try { StopEngine(false); } catch { }
            try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch { }
            app.Shutdown();
        }

        // =====================================================================
        //  UI wiring
        // =====================================================================
        static void SetupUI()
        {
            Find<Button>("BtnMin").Click += delegate { win.WindowState = WindowState.Minimized; };
            Find<Button>("BtnClose").Click += delegate { win.Close(); };
            win.Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (quitting) return;
                if (CB("closetray", true) && tray != null && tray.Visible) { e.Cancel = true; win.Hide(); }
                else { e.Cancel = true; Quit(); }
            };

            foreach (string n in PageNames)
            {
                string page = n;
                Find<RadioButton>("Nav" + page).Checked += delegate { ShowPage(page); };
            }

            Find<Button>("PowerBtn").Click += delegate { Burst(); Toggle(); };

            Find<Button>("SvcAll").Click += delegate { SetAllServices(true); };
            Find<Button>("SvcNone").Click += delegate { SetAllServices(false); };

            Action refCust = WireDomains("CustIn", "CustAdd", "CustList", custom);
            WireDomains("ExclIn", "ExclAdd", "ExclList", excl);
            Find<Button>("DomFolder").Click += delegate { try { Process.Start("explorer.exe", "\"" + ListsDir + "\""); } catch { } };
            Find<Button>("DomImport").Click += delegate
            {
                OpenFileDialog dlg = new OpenFileDialog();
                dlg.Filter = "Text (*.txt)|*.txt|All|*.*";
                if (dlg.ShowDialog(win) != true) return;
                foreach (string l in ReadLines(dlg.FileName))
                {
                    string n = NormDomain(l);
                    if (n != null && !custom.Contains(n)) custom.Add(n);
                }
                custom.Sort(); SaveDomains(); refCust(); Touch();
            };
            Find<Button>("DomExport").Click += delegate
            {
                SaveFileDialog dlg = new SaveFileDialog();
                dlg.Filter = "Text (*.txt)|*.txt"; dlg.FileName = "limix-domains.txt";
                if (dlg.ShowDialog(win) != true) return;
                try { File.WriteAllLines(dlg.FileName, custom.ToArray(), Encoding.UTF8); } catch { }
            };

            Find<Button>("AutoBtn").Click += delegate { RunTest(); };
            Find<Button>("ApplyBest").Click += delegate
            {
                if (bestId == null) return;
                CS("strategy", bestId); BuildStrategies(); Touch();
                Find<Button>("ApplyBest").Visibility = Visibility.Collapsed;
            };
            Find<Button>("LogCopy").Click += delegate { try { Clipboard.SetText(Find<TextBox>("LogBox").Text); } catch { } };
            Find<Button>("LogClear").Click += delegate { lock (logSb) { logSb.Clear(); } Find<TextBox>("LogBox").Clear(); };

            // appearance
            string th = CG("theme", "system");
            Find<RadioButton>("ThSystem").IsChecked = th == "system";
            Find<RadioButton>("ThDark").IsChecked = th == "dark";
            Find<RadioButton>("ThLight").IsChecked = th == "light";
            Find<RadioButton>("ThSystem").Checked += delegate { SetTheme("system"); };
            Find<RadioButton>("ThDark").Checked += delegate { SetTheme("dark"); };
            Find<RadioButton>("ThLight").Checked += delegate { SetTheme("light"); };
            Find<RadioButton>("LgRu").IsChecked = Lang == "ru";
            Find<RadioButton>("LgEn").IsChecked = Lang == "en";
            Find<RadioButton>("LgRu").Checked += delegate { SetLang("ru"); };
            Find<RadioButton>("LgEn").Checked += delegate { SetLang("en"); };

            // switches
            BindSwitch("SwConn", "autoconnect", true, null);
            BindSwitch("SwTray", "tray", true, ApplyTray);
            BindSwitch("SwClose", "closetray", true, null);
            BindSwitch("SwIp", "ip", true, Touch);
            BindSwitch("SwWatch", "watch", true, null);
            BindSwitch("SwVerify", "verify", true, null);
            ToggleButton sa = Find<ToggleButton>("SwAuto");
            sa.Click += delegate
            {
                bool want = sa.IsChecked == true;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    SetAutostart(want);
                    bool real = IsAutostart();
                    UI(delegate { sa.IsChecked = real; RefreshState(); });
                });
            };

            // tools
            Find<Button>("PinBtn").Click += delegate
            {
                ThreadPool.QueueUserWorkItem(delegate { PinHashes(); Log(L("log.pinned")); RefreshHashAsync(); });
            };
            Find<Button>("DiagBtn").Click += delegate { Find<RadioButton>("NavHealth").IsChecked = true; RunHealth(); };
            Find<Button>("DnsBtn").Click += delegate
            {
                ThreadPool.QueueUserWorkItem(delegate { Sh("ipconfig.exe", "/flushdns"); Log(L("diag.dns")); });
            };
            Find<Button>("DrvBtn").Click += delegate
            {
                ThreadPool.QueueUserWorkItem(delegate { StopEngine(false); Log(L("diag.drv")); });
            };
            Find<Button>("FolderBtn").Click += delegate { try { Process.Start("explorer.exe", "\"" + Root + "\""); } catch { } };

            // timers
            applyTimer = new DispatcherTimer();
            applyTimer.Interval = TimeSpan.FromMilliseconds(700);
            applyTimer.Tick += delegate
            {
                applyTimer.Stop();
                if (EngineRunning && !testing)
                    ThreadPool.QueueUserWorkItem(delegate { StopEngine(true); DoConnect(); });
            };
            upTimer = new DispatcherTimer();
            upTimer.Interval = TimeSpan.FromSeconds(1);
            upTimer.Tick += delegate { if (state == "on") Find<TextBlock>("StatUptime").Text = Uptime(); };
            upTimer.Start();

            SystemEvents.UserPreferenceChanged += delegate { if (CG("theme", "system") == "system") UI(ApplyTheme); };
            app.SessionEnding += delegate { Quit(); };

            lock (logSb) { Find<TextBox>("LogBox").Text = logSb.ToString(); }
            Find<ToggleButton>("SwAuto").IsChecked = false;
            BuildServices();
            BuildStrategies();
            BuildSwatches();
            SetupTray();
            SetupExtras();
            ApplyTheme();
            ApplyLang();
            RefreshHashAsync();
        }

        // =====================================================================
        //  Glass, corners, aurora
        // =====================================================================
        static string FxLevel { get { return CG("fx", "strong"); } }

        static Color Rgba(int r, int g, int b, double a)
        {
            return Color.FromArgb((byte)Math.Max(0, Math.Min(255, (int)(a * 255))), (byte)r, (byte)g, (byte)b);
        }

        static LinearGradientBrush Grad(params Color[] c)
        {
            LinearGradientBrush b = new LinearGradientBrush();
            b.StartPoint = new Point(0, 0); b.EndPoint = new Point(1, 1);
            for (int i = 0; i < c.Length; i++) b.GradientStops.Add(new GradientStop(c[i], i / (double)(c.Length - 1)));
            return b;
        }

        static RadialGradientBrush Blob(Color c, double a)
        {
            RadialGradientBrush b = new RadialGradientBrush();
            b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(Math.Min(1.0, a) * 255), c.R, c.G, c.B), 0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            return b;
        }

        static DoubleAnimation Drift(double to, double sec)
        {
            DoubleAnimation a = new DoubleAnimation(0, to, TimeSpan.FromSeconds(sec));
            a.AutoReverse = true; a.RepeatBehavior = RepeatBehavior.Forever;
            SineEase e = new SineEase(); e.EasingMode = EasingMode.EaseInOut;
            a.EasingFunction = e;
            return a;
        }

        static void SetAurora(bool on)
        {
            if (on == auroraOn) return;
            auroraOn = on;
            string[] names = { "Blob1", "Blob2", "Blob3" };
            double[] dx = { 90, -110, 70 }, dy = { 60, -70, -90 }, dur = { 16, 21, 25 };
            for (int i = 0; i < names.Length; i++)
            {
                Ellipse el = Find<Ellipse>(names[i]);
                if (el == null) continue;
                TranslateTransform tt = el.RenderTransform as TranslateTransform;
                if (tt == null) { tt = new TranslateTransform(); el.RenderTransform = tt; }
                if (on)
                {
                    tt.BeginAnimation(TranslateTransform.XProperty, Drift(dx[i], dur[i]));
                    tt.BeginAnimation(TranslateTransform.YProperty, Drift(dy[i], dur[i] * 1.2));
                }
                else
                {
                    tt.BeginAnimation(TranslateTransform.XProperty, null);
                    tt.BeginAnimation(TranslateTransform.YProperty, null);
                }
            }
        }

        static void ApplyAurora(bool d, double k, bool glass)
        {
            Grid layer = Find<Grid>("AuroraLayer");
            if (layer == null) return;
            layer.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
            if (!glass) { SetAurora(false); return; }
            Color ac = Col(AccentHex());
            double a = (d ? 0.55 : 0.45) * k;
            Find<Ellipse>("Blob1").Fill = Blob(ac, a);
            Find<Ellipse>("Blob2").Fill = Blob(ac, a * 0.9);
            Find<Ellipse>("Blob3").Fill = Blob(ac, a * 0.8);
            SetAurora(CB("anim", true));
        }

        static void ApplyCorners()
        {
            string c = CG("corners", "round");
            string[] keys = { "RCard", "RCtl", "RSm", "RTrack", "RThumb", "RBar", "RCap", "RFab" };
            double[] v = c == "sharp" ? new double[] { 0, 0, 0, 0, 0, 0, 0, 0 }
                       : (c == "soft" ? new double[] { 8, 6, 5, 6, 4, 2, 6, 10 } : new double[] { 14, 10, 8, 13, 9, 3, 14, 24 });
            for (int i = 0; i < keys.Length; i++) win.Resources[keys[i]] = new CornerRadius(v[i]);
        }

        // =====================================================================
        //  Links, GitHub promo toast, update check
        // =====================================================================
        const string Ver = "1.1.0";
        const string GH_URL = "https://github.com/metsabor/LimixProtocol";
        const string GH_PATH = "M 8 0 C 3.58 0 0 3.58 0 8 c 0 3.54 2.29 6.53 5.47 7.59 .4 .07 .55 -.17 .55 -.38 0 -.19 -.01 -.82 -.01 -1.49 -2.01 .37 -2.53 -.49 -2.69 -.94 -.09 -.23 -.48 -.94 -.82 -1.13 -.28 -.15 -.68 -.52 -.01 -.53 .63 -.01 1.08 .58 1.23 .82 .72 1.21 1.87 .87 2.33 .66 .07 -.52 .28 -.87 .51 -1.07 -1.78 -.2 -3.64 -.89 -3.64 -3.95 0 -.87 .31 -1.59 .82 -2.15 -.08 -.2 -.36 -1.02 .08 -2.12 0 0 .67 -.21 2.2 .82 .64 -.18 1.32 -.27 2 -.27 .68 0 1.36 .09 2 .27 1.53 -1.04 2.2 -.82 2.2 -.82 .44 1.1 .16 1.92 .08 2.12 .51 .56 .82 1.27 .82 2.15 0 3.07 -1.87 3.75 -3.65 3.95 .29 .25 .54 .73 .54 1.48 0 1.07 -.01 1.93 -.01 2.2 0 .21 .15 .46 .55 .38 A 8.013 8.013 0 0 0 16 8 c 0 -4.42 -3.58 -8 -8 -8 z";

        static void OpenUrl(string url) { try { Process.Start("explorer.exe", url); } catch { } }

        static void MaybePromo()
        {
            if (!promoArm) return;
            promoArm = false;
            if (!CB("promo", true) || win == null || !win.IsVisible) return;
            if ((DateTime.Now - lastPromo).TotalMinutes < 5) return;
            lastPromo = DateTime.Now;
            ShowPromo();
        }

        static void ShowPromo()
        {
            Border t = Find<Border>("Toast");
            if (t == null) return;
            promoShown = true;
            t.Visibility = Visibility.Visible;
            if (CB("anim", true))
            {
                TranslateTransform tt = new TranslateTransform(380, 0);
                t.RenderTransform = tt;
                CubicEase ease = new CubicEase(); ease.EasingMode = EasingMode.EaseOut;
                DoubleAnimation mv = new DoubleAnimation(380, 0, TimeSpan.FromMilliseconds(520)); mv.EasingFunction = ease;
                tt.BeginAnimation(TranslateTransform.XProperty, mv);
                t.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)));
            }
            else { t.BeginAnimation(UIElement.OpacityProperty, null); t.Opacity = 1; t.RenderTransform = null; }
            promoTimer.Stop(); promoTimer.Start();
        }

        static void HidePromo()
        {
            Border t = Find<Border>("Toast");
            if (t == null || !promoShown) return;
            promoShown = false;
            promoTimer.Stop();
            if (!CB("anim", true)) { t.Visibility = Visibility.Collapsed; return; }
            TranslateTransform tt = t.RenderTransform as TranslateTransform;
            if (tt != null)
            {
                CubicEase ease = new CubicEase(); ease.EasingMode = EasingMode.EaseIn;
                DoubleAnimation mv = new DoubleAnimation(0, 380, TimeSpan.FromMilliseconds(380)); mv.EasingFunction = ease;
                tt.BeginAnimation(TranslateTransform.XProperty, mv);
            }
            DoubleAnimation fd = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(380));
            fd.Completed += delegate { if (!promoShown) t.Visibility = Visibility.Collapsed; };
            t.BeginAnimation(UIElement.OpacityProperty, fd);
        }

        static void CheckUpdate()
        {
            Find<TextBlock>("UpdText").Text = L("upd.checking");
            ThreadPool.QueueUserWorkItem(delegate
            {
                string msg = L("upd.fail");
                try
                {
                    HttpWebRequest r = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/metsabor/LimixProtocol/releases/latest");
                    r.UserAgent = "LimixProtocol"; r.Timeout = 8000;
                    using (WebResponse w = r.GetResponse())
                    using (StreamReader sr = new StreamReader(w.GetResponseStream()))
                    {
                        Match m = Regex.Match(sr.ReadToEnd(), "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                        if (!m.Success) msg = L("upd.none");
                        else
                        {
                            Version nv;
                            bool newer = Version.TryParse(m.Groups[1].Value.TrimStart('v', 'V'), out nv) && nv > new Version(Ver);
                            msg = newer ? string.Format(L("upd.new"), m.Groups[1].Value) : L("upd.latest");
                        }
                    }
                }
                catch (WebException ex)
                {
                    HttpWebResponse hr = ex.Response as HttpWebResponse;
                    if (hr != null && hr.StatusCode == HttpStatusCode.NotFound) msg = L("upd.none");
                }
                catch { }
                UI(delegate { Find<TextBlock>("UpdText").Text = msg; });
            });
        }

        static void SetTips()
        {
            Button b = Find<Button>("BtnPin"); if (b != null) b.ToolTip = L("tip.pin");
            b = Find<Button>("BtnMin"); if (b != null) b.ToolTip = L("tip.min");
            b = Find<Button>("BtnClose"); if (b != null) b.ToolTip = L("tip.close");
            b = Find<Button>("GhBtn"); if (b != null) b.ToolTip = L("tip.gh");
        }

        static void UpdatePin()
        {
            Button b = Find<Button>("BtnPin");
            if (b != null) b.SetResourceReference(Control.ForegroundProperty, win.Topmost ? "Ac" : "Tx");
        }

        // =====================================================================
        //  Service status chips on the home page
        // =====================================================================
        static Dictionary<string, int> chipState = new Dictionary<string, int>();
        static volatile bool chipBusy;

        static void BuildChips()
        {
            Panel p = Find<Panel>("SvcChips");
            if (p == null) return;
            p.Children.Clear();
            if (state != "on")
            {
                TextBlock h = MakeText(L("home.chips.off"), false, 12, "Dim");
                h.TextWrapping = TextWrapping.Wrap;
                p.Children.Add(h);
                return;
            }
            foreach (Svc s in Svcs)
            {
                if (!SvcOn(s.Id)) continue;
                int st;
                lock (chipState) { chipState.TryGetValue(s.Id, out st); }
                Border b = new Border();
                b.SetResourceReference(Border.CornerRadiusProperty, "RCtl");
                b.SetResourceReference(Border.BackgroundProperty, "Sf2");
                b.SetResourceReference(Border.BorderBrushProperty, "Bd");
                b.BorderThickness = new Thickness(1);
                b.Padding = new Thickness(11, 6, 12, 6); b.Margin = new Thickness(0, 0, 8, 8);
                StackPanel sp = new StackPanel(); sp.Orientation = Orientation.Horizontal;
                Ellipse dot = new Ellipse();
                dot.Width = 8; dot.Height = 8; dot.Margin = new Thickness(0, 0, 8, 0); dot.VerticalAlignment = VerticalAlignment.Center;
                dot.Fill = new SolidColorBrush(Col(st == 1 ? "#30A46C" : (st == 2 ? "#E5484D" : "#9498A1")));
                sp.Children.Add(dot); sp.Children.Add(MakeText(s.Name, true, 12, null));
                b.Child = sp;
                p.Children.Add(b);
            }
        }

        static void CheckChips()
        {
            if (chipBusy) return;
            List<Svc> en = Svcs.Where(x => SvcOn(x.Id) && x.Test != null).ToList();
            if (en.Count == 0) return;
            chipBusy = true;
            UI(delegate { Button cb = Find<Button>("ChipsBtn"); if (cb != null) cb.IsEnabled = false; });
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(900);
                Dictionary<string, int> res = new Dictionary<string, int>();
                List<Thread> ths = new List<Thread>();
                foreach (Svc s in en)
                {
                    Svc sv = s;
                    Thread th = new Thread(() => { bool ok = Reach(sv.Test); lock (res) { res[sv.Id] = ok ? 1 : 2; } });
                    th.IsBackground = true; th.Start(); ths.Add(th);
                }
                foreach (Thread th2 in ths) th2.Join(9000);
                lock (res) { lock (chipState) { chipState.Clear(); foreach (KeyValuePair<string, int> kv in res) chipState[kv.Key] = kv.Value; } }
                chipBusy = false;
                UI(delegate { Button cb2 = Find<Button>("ChipsBtn"); if (cb2 != null) cb2.IsEnabled = true; BuildChips(); });
            });
        }

        // =====================================================================
        //  Error explanations, self-heal, health check
        // =====================================================================
        static List<string> engOut = new List<string>();

        [DllImport("kernel32.dll", EntryPoint = "DeleteFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool DeleteFileW(string name);

        static void EngLine(string l)
        {
            lock (engOut) { engOut.Add(l); if (engOut.Count > 40) engOut.RemoveAt(0); }
            Log("[engine] " + l);
        }

        static bool IsAdmin()
        {
            try
            {
                return new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // Removes the "downloaded from the internet" mark (Mark of the Web) from engine files.
        static int UnblockFiles()
        {
            int n = 0;
            try
            {
                List<string> files = new List<string>();
                if (BinDir != null) files.AddRange(Directory.GetFiles(BinDir));
                files.Add(Assembly.GetExecutingAssembly().Location);
                foreach (string f in files) if (DeleteFileW(f + ":Zone.Identifier")) n++;
            }
            catch { }
            return n;
        }

        static string Explain(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string t = text.ToLowerInvariant();
            Match m = Regex.Match(t, @"(?:error|code|err)[^0-9]{0,6}(\d{1,5})");
            int code = m.Success ? int.Parse(m.Groups[1].Value) : -1;
            if (code == 5 || t.Contains("access is denied") || t.Contains("отказано в доступе")) return L("ex.admin");
            if (code == 1275 || t.Contains("blocked")) return L("ex.blocked");
            if (code == 577 || t.Contains("digital signature")) return L("ex.sign");
            if (code == 2 || code == 3 || t.Contains("cannot find") || t.Contains("not found")) return L("ex.missing");
            if (code == 1058) return L("ex.disabled");
            if (code == 1060 || code == 1072 || code == 1073) return L("ex.pending");
            if (code == 87) return L("ex.args");
            if (code == 126 || code == 193 || t.Contains("cygwin")) return L("ex.dll");
            return null;
        }

        // Starts the engine and verifies that it stays alive; on failure returns null and a readable reason.
        static Process TryStart(Strat s, out string err)
        {
            lock (engOut) { engOut.Clear(); }
            Process p = StartWinws(s, out err);
            if (p == null)
            {
                string h0 = Explain(err);
                err = (err ?? "") + (h0 != null ? "\n" + h0 : "");
                return null;
            }
            Thread.Sleep(1600);
            if (!p.HasExited) return p;
            string tail;
            lock (engOut) { tail = string.Join("\n", engOut.ToArray()); }
            string hint = Explain(tail);
            err = L("err.exit") + " " + p.ExitCode + (hint != null ? "\n" + hint : "") + (tail.Length > 0 ? "\n" + tail.Split('\n')[0] : "");
            return null;
        }

        static void QuickHeal()
        {
            KillAllWinws();
            ResetDriver();
            UnblockFiles();
            Thread.Sleep(500);
        }

        class HItem { public int Lv; public string Title, Msg; public bool Fixed; }
        static List<HItem> health = new List<HItem>();
        static volatile bool healthBusy;

        static void AddHealthRow(HItem h)
        {
            StackPanel list = Find<StackPanel>("HealthList");
            if (list == null) return;
            Border b = new Border();
            b.Style = (Style)win.FindResource("Card");
            b.Padding = new Thickness(14, 10, 14, 10); b.Margin = new Thickness(0, 0, 0, 8);
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            TextBlock ic = new TextBlock();
            ic.FontFamily = new FontFamily("Segoe MDL2 Assets"); ic.FontSize = 17;
            ic.VerticalAlignment = VerticalAlignment.Top; ic.Margin = new Thickness(0, 2, 0, 0);
            ic.Text = h.Lv == 0 ? "\uE73E" : (h.Lv == 1 ? "\uE7BA" : "\uE711");
            ic.Foreground = new SolidColorBrush(Col(h.Lv == 0 ? "#30A46C" : (h.Lv == 1 ? "#F5A524" : "#E5484D")));
            StackPanel sp = new StackPanel();
            StackPanel head = new StackPanel(); head.Orientation = Orientation.Horizontal;
            head.Children.Add(MakeText(h.Title, true, 13, null));
            if (h.Fixed) head.Children.Add(MakeText("   ✔ " + L("h.fixed"), true, 12, "Ac"));
            sp.Children.Add(head);
            if (!string.IsNullOrEmpty(h.Msg))
            {
                TextBlock m = MakeText(h.Msg, false, 12, "Dim");
                m.TextWrapping = TextWrapping.Wrap; m.Margin = new Thickness(0, 3, 0, 0);
                sp.Children.Add(m);
            }
            Grid.SetColumn(sp, 1);
            g.Children.Add(ic); g.Children.Add(sp);
            b.Child = g;
            list.Children.Add(b);
            if (CB("anim", true)) { b.Opacity = 0; b.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320))); }
        }

        static void HAdd(int lv, string title, string msg, bool fixedIt)
        {
            HItem h = new HItem();
            h.Lv = lv; h.Title = title; h.Msg = msg; h.Fixed = fixedIt;
            lock (health) { health.Add(h); }
            UI(delegate { AddHealthRow(h); });
        }

        static bool DnsOk()
        {
            try { return Dns.GetHostAddresses("www.google.com").Length > 0; } catch { return false; }
        }

        static void StopQuiet(Process p)
        {
            try { if (!p.HasExited) p.Kill(); p.WaitForExit(2000); } catch { }
        }

        static void HealthChecks()
        {
            bool admin = IsAdmin();
            HAdd(admin ? 0 : 2, L("h.admin"), admin ? L("h.ok") : L("ex.admin"), false);
            bool x64 = Environment.Is64BitOperatingSystem;
            HAdd(x64 ? 0 : 2, L("h.os"), Environment.OSVersion.VersionString + (x64 ? " x64" : " x86"), false);

            if (BinDir == null)
            {
                HAdd(2, L("h.engine"), L("err.nobin") + "\n" + Root, false);
            }
            else
            {
                List<string> miss = new List<string>();
                foreach (string f in Guard) if (!File.Exists(Path.Combine(BinDir, f))) miss.Add(f);
                foreach (string f2 in new string[] { "tls_clienthello_www_google_com.bin", "quic_initial_www_google_com.bin" })
                    if (!File.Exists(Path.Combine(BinDir, f2))) miss.Add(f2);
                if (miss.Count == 0) HAdd(0, L("h.engine"), BinDir, false);
                else HAdd(2, L("h.engine"), L("h.missing") + " " + string.Join(", ", miss.ToArray()) + "\n" + L("h.av"), false);

                int ub = UnblockFiles();
                HAdd(0, L("h.unblock"), ub > 0 ? L("h.unblocked") + " " + ub : L("h.ok"), ub > 0);

                int hs = HashState();
                if (hs == 2) HAdd(1, L("h.hash"), L("err.hash"), false);
                else HAdd(0, L("h.hash"), L(hs == 1 ? "hash.ok" : "hash.none"), false);
            }

            Process[] ws = Process.GetProcessesByName("winws");
            if (ws.Length > 0 && !EngineRunning) { KillAllWinws(); HAdd(0, L("h.conflict"), L("log.killed"), true); }
            else if (Sh("sc.exe", "query GoodbyeDPI").Contains("RUNNING")) { Sh("sc.exe", "stop GoodbyeDPI"); HAdd(0, L("h.conflict"), "GoodbyeDPI — " + L("h.stopped"), true); }
            else HAdd(0, L("h.conflict"), L("h.ok"), false);

            if (!EngineRunning)
            {
                if (Sh("sc.exe", "query WinDivert").Contains("RUNNING")) { ResetDriver(); HAdd(0, L("h.driver"), L("diag.drv"), true); }
                else HAdd(0, L("h.driver"), L("h.ok"), false);
            }

            if (SvcCount() == 0 && custom.Count == 0)
            {
                cfg["svc.youtube"] = "1"; cfg["svc.discord"] = "1"; SaveCfg();
                UI(BuildServices);
                HAdd(0, L("h.lists"), L("h.defaults"), true);
            }
            else
            {
                bool okl = BuildLists();
                HAdd(okl ? 0 : 1, L("h.lists"), SvcCount() + " / " + Svcs.Count + " " + L("home.services").ToLowerInvariant(), false);
            }

            bool dns = DnsOk();
            if (!dns) { Sh("ipconfig.exe", "/flushdns"); dns = DnsOk(); HAdd(dns ? 0 : 2, L("h.dns"), dns ? L("diag.dns") : L("h.dnsfail"), dns); }
            else HAdd(0, L("h.dns"), L("h.ok"), false);
            bool net = Reach("https://www.cloudflare.com/cdn-cgi/trace") || Reach("https://www.google.com/generate_204");
            HAdd(net ? 0 : 1, L("h.net"), net ? L("h.ok") : L("h.nonet"), false);

            bool hv = false;
            try
            {
                object v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0);
                hv = v is int && (int)v == 1;
            }
            catch { }
            HAdd(hv ? 1 : 0, L("h.hvci"), hv ? L("ex.blocked") : L("h.ok"), false);

            if (BinDir != null)
            {
                if (EngineRunning) HAdd(0, L("h.start"), L("st.on") + " — " + SName(CurStrat()), false);
                else
                {
                    BuildLists();
                    Strat cur = CurStrat();
                    string err;
                    Process p = TryStart(cur, out err);
                    if (p != null) { StopQuiet(p); HAdd(0, L("h.start"), SName(cur), false); }
                    else
                    {
                        QuickHeal();
                        string err2;
                        p = TryStart(cur, out err2);
                        Strat used = null;
                        if (p == null)
                            foreach (Strat alt in new List<Strat>(Strats))
                            {
                                if (alt.Id == cur.Id) continue;
                                string e3;
                                p = TryStart(alt, out e3);
                                if (p != null) { used = alt; break; }
                            }
                        if (p != null)
                        {
                            StopQuiet(p);
                            if (used != null)
                            {
                                CS("strategy", used.Id);
                                UI(delegate { BuildStrategies(); RefreshState(); });
                                HAdd(0, L("h.start"), L("heal.switched") + " " + SName(used), true);
                            }
                            else HAdd(0, L("h.start"), L("h.healed"), true);
                        }
                        else HAdd(2, L("h.start"), err, false);
                    }
                }
            }
        }

        static void RunHealth()
        {
            if (healthBusy) return;
            healthBusy = true;
            lock (health) { health.Clear(); }
            UI(delegate
            {
                Find<StackPanel>("HealthList").Children.Clear();
                Find<Button>("HealthRun").IsEnabled = false;
                Find<TextBlock>("HealthStatus").Text = L("health.running");
            });
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { HealthChecks(); }
                catch (Exception ex) { HAdd(2, "Health", ex.Message, false); }
                finally
                {
                    int bad, warn;
                    lock (health) { bad = health.Count(x => x.Lv == 2); warn = health.Count(x => x.Lv == 1); }
                    UI(delegate
                    {
                        Find<TextBlock>("HealthStatus").Text = string.Format(L("health.done"), bad, warn);
                        Find<Button>("HealthRun").IsEnabled = true;
                    });
                    healthBusy = false;
                }
            });
        }

        static string BuildReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("LimixProtocol " + Ver);
            sb.AppendLine("OS: " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " x64" : " x86"));
            sb.AppendLine("Admin: " + IsAdmin() + " | Engine found: " + (BinDir != null) + " | Strategy: " + SName(CurStrat()) + " | State: " + state);
            sb.AppendLine("Services: " + string.Join(", ", Svcs.Where(x => SvcOn(x.Id)).Select(x => x.Name).ToArray()));
            sb.AppendLine("Self-repair: " + CB("heal", true) + " | Glass: " + FxLevel + " | Corners: " + CG("corners", "round"));
            sb.AppendLine();
            sb.AppendLine("== Checks ==");
            lock (health)
            {
                foreach (HItem h in health)
                    sb.AppendLine("[" + (h.Lv == 0 ? "OK" : (h.Lv == 1 ? "WARN" : "FAIL")) + "] " + h.Title + ": " + (h.Msg ?? "").Replace("\n", " ") + (h.Fixed ? " (fixed)" : ""));
            }
            sb.AppendLine();
            sb.AppendLine("== Engine output ==");
            lock (engOut) { foreach (string l in engOut) sb.AppendLine(l); }
            sb.AppendLine();
            sb.AppendLine("== Log (tail) ==");
            string all;
            lock (logSb) { all = logSb.ToString(); }
            string[] lines = all.Split('\n');
            for (int i = Math.Max(0, lines.Length - 40); i < lines.Length; i++) sb.AppendLine(lines[i].TrimEnd());
            string r = sb.ToString();
            string up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(up)) r = r.Replace(up, "%USERPROFILE%");
            return r;
        }

        static void AddDefenderExclusion()
        {
            if (MessageBox.Show(win, L("av.confirm") + "\n\n" + Root, "LimixProtocol", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                int c;
                string path = Root.Replace("'", "''");
                string o = Sh("powershell.exe", "-NoProfile -Command \"Add-MpPreference -ExclusionPath '" + path + "'\"", out c);
                Log(c == 0 ? L("av.done") : "Defender: " + o.Trim());
            });
        }

        // =====================================================================
        //  Extra UI wiring (effects, corners, promo, health, about)
        // =====================================================================
        static void SetFx(string v) { CS("fx", v); ApplyTheme(); }
        static void SetCorners(string v) { CS("corners", v); ApplyTheme(); }

        static void SetupExtras()
        {
            try { ghGeo = Geometry.Parse(GH_PATH); ghGeo.Freeze(); } catch { ghGeo = null; }
            foreach (string n in new string[] { "GhLogo", "ToastLogo" })
            {
                System.Windows.Shapes.Path pt = Find<System.Windows.Shapes.Path>(n);
                if (pt != null && ghGeo != null) pt.Data = ghGeo;
            }
            if (ghGeo == null) Find<Button>("GhBtn").Content = "GH";
            Find<Button>("GhBtn").Click += delegate { OpenUrl(GH_URL); };

            promoTimer = new DispatcherTimer();
            promoTimer.Interval = TimeSpan.FromSeconds(11);
            promoTimer.Tick += delegate { HidePromo(); };
            Find<Button>("ToastOpen").Click += delegate { OpenUrl(GH_URL); HidePromo(); };
            Find<Button>("ToastClose").Click += delegate { HidePromo(); };
            Find<Button>("ToastOff").Click += delegate
            {
                CSB("promo", false);
                Find<ToggleButton>("SwPromo").IsChecked = false;
                HidePromo();
            };

            win.Topmost = CB("ontop", false);
            UpdatePin();
            Find<Button>("BtnPin").Click += delegate { win.Topmost = !win.Topmost; CSB("ontop", win.Topmost); UpdatePin(); };

            string fx = FxLevel, cr = CG("corners", "round");
            Find<RadioButton>("FxOff").IsChecked = fx == "off";
            Find<RadioButton>("FxSoft").IsChecked = fx == "soft";
            Find<RadioButton>("FxStrong").IsChecked = fx == "strong";
            Find<RadioButton>("FxOff").Checked += delegate { SetFx("off"); };
            Find<RadioButton>("FxSoft").Checked += delegate { SetFx("soft"); };
            Find<RadioButton>("FxStrong").Checked += delegate { SetFx("strong"); };
            Find<RadioButton>("CrSharp").IsChecked = cr == "sharp";
            Find<RadioButton>("CrSoft").IsChecked = cr == "soft";
            Find<RadioButton>("CrRound").IsChecked = cr != "sharp" && cr != "soft";
            Find<RadioButton>("CrSharp").Checked += delegate { SetCorners("sharp"); };
            Find<RadioButton>("CrSoft").Checked += delegate { SetCorners("soft"); };
            Find<RadioButton>("CrRound").Checked += delegate { SetCorners("round"); };

            BindSwitch("SwAnim", "anim", true, ApplyTheme);
            BindSwitch("SwPromo", "promo", true, null);
            BindSwitch("SwHeal", "heal", true, null);

            // health / about
            Find<Button>("HealthRun").Click += delegate { RunHealth(); };
            Find<Button>("HealthCopy").Click += delegate
            {
                try { Clipboard.SetText(BuildReport()); Find<TextBlock>("HealthStatus").Text = L("health.copied"); } catch { }
            };
            Find<Button>("HealthIssue").Click += delegate
            {
                try { Clipboard.SetText(BuildReport()); Find<TextBlock>("HealthStatus").Text = L("health.copied"); } catch { }
                OpenUrl(GH_URL + "/issues/new");
            };
            Find<Button>("HealthAv").Click += delegate { AddDefenderExclusion(); };
            Find<Button>("AboutGh").Click += delegate { OpenUrl(GH_URL); };
            Find<Button>("AboutRel").Click += delegate { OpenUrl(GH_URL + "/releases"); };
            Find<Button>("UpdBtn").Click += delegate { CheckUpdate(); };
            Find<Button>("ChipsBtn").Click += delegate { if (state == "on") CheckChips(); };
            BuildChips();
        }

        // =====================================================================
        //  Entry point
        // =====================================================================
        [STAThread]
        static int Main(string[] args)
        {
            silent = args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
            bool created;
            mtx = new Mutex(true, "LimixProtocol.Single", out created);
            if (!created)
            {
                if (!silent) { try { EventWaitHandle.OpenExisting("LimixProtocol.Show").Set(); } catch { } }
                return 0;
            }
            showEv = new EventWaitHandle(false, EventResetMode.AutoReset, "LimixProtocol.Show");
            Thread listener = new Thread(() => { while (true) { showEv.WaitOne(); ShowWin(); } });
            listener.IsBackground = true; listener.Start();

            // .NET 4.0-compatible apps do not enable TLS 1.2 by default
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768 | (SecurityProtocolType)192;
            ServicePointManager.DefaultConnectionLimit = 64;
            ServicePointManager.Expect100Continue = false;

            try
            {
                InitPaths(); LoadCfg(); InitStrings(); InitSvcs(); InitStrats(); LoadDomainFiles();
                app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                app.DispatcherUnhandledException += (s, e) => { Log("ERROR: " + e.Exception.Message); e.Handled = true; };
                using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui.xaml"))
                    win = (Window)XamlReader.Load(st);
                SetupUI();
                if (!silent) win.Show();
                if (CB("autoconnect", true)) ThreadPool.QueueUserWorkItem(delegate { DoConnect(); });
                ThreadPool.QueueUserWorkItem(delegate
                {
                    bool a = IsAutostart();
                    UI(delegate { Find<ToggleButton>("SwAuto").IsChecked = a; RefreshState(); });
                });
                app.Run();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "LimixProtocol - fatal error");
                return 1;
            }
            return 0;
        }
    }
}
