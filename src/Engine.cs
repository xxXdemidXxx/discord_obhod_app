using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ZapretDesktop {
    public class EngineSetupException : Exception {
        public EngineSetupException(string message,Exception inner=null):base(message,inner){}
    }
    public sealed class EngineDriverException : EngineSetupException {
        public EngineDriverException(string message):base(message){}
        internal static bool IsFailure(string line) {
            return line.IndexOf("windivert: error opening filter:",StringComparison.OrdinalIgnoreCase)>=0 &&
                line.IndexOf("parameter is incorrect",StringComparison.OrdinalIgnoreCase)<0 &&
                line.IndexOf("параметр задан неверно",StringComparison.OrdinalIgnoreCase)<0;
        }
    }
    public sealed class EngineStartException : Exception {
        public EngineStartException(string message):base(message){}
    }
    public sealed class Settings {
        public string Engine = "1.10.3";
        public string PreviousEngine = "";
        public string Strategy = "general.bat";
        public string WorkingStrategy = "";
        public string WorkingStrategyEngine = "";
        public bool CheckUpdates = true;
        public bool AutoAppUpdates = true;
        public bool AutoInstallShell = false;
        public bool AutoInstallCore = false; public bool StartWithWindows = false;
        public string VisualTheme = "graphite";
    }

    public sealed class Store {
        public readonly string Root;
        public Settings Settings;
        public Store(string root) {
            Root = Path.GetFullPath(root);
            Directory.CreateDirectory(Path.Combine(Root, "data"));
            var file = Path.Combine(Root, "data", "settings.json");
            Settings = File.Exists(file) ? new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(file)) : new Settings();
            if (Settings == null) throw new InvalidDataException("Повреждены настройки приложения.");
            EnginePath(Settings.Engine);
        }
        public string EnginePath(string version) {
            if (String.IsNullOrEmpty(version) || !Regex.IsMatch(version, @"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$") || version.Contains(".."))
                throw new InvalidDataException("Недопустимое имя версии ядра.");
            return Path.Combine(Root, "data", "engines", version);
        }
        public string Current { get { return EnginePath(Settings.Engine); } }
        public void Save() {
            var file = Path.Combine(Root, "data", "settings.json");
            var temp = file + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(Settings), new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(temp, file, file + ".bak"); else File.Move(temp, file);
        }
        public string[] Strategies() {
            if (!Directory.Exists(Current)) return new string[0];
            return Directory.GetFiles(Current, "general*.bat").Select(Path.GetFileName)
                .OrderBy(x => x == "general.bat" ? "" : Regex.Replace(x, @"\d+", m => m.Value.PadLeft(5, '0'))).ToArray();
        }
        public static void EnsureUserLists(string engine) {
            var lists = Path.Combine(engine, "lists");
            Directory.CreateDirectory(lists);
            foreach (var name in new[] { "list-general-user.txt", "list-exclude-user.txt", "ipset-exclude-user.txt" }) {
                var path = Path.Combine(lists, name);
                if (!File.Exists(path)) File.WriteAllText(path, name.StartsWith("ipset") ? "203.0.113.113/32\r\n" : "domain.example.abc\r\n");
            }
        }
    }

    public static class Strategy {
        public static List<string> Split(string text) {
            var result = new List<string>(); var word = new StringBuilder(); bool quoted = false;
            foreach (char c in text) {
                if (c == '"') { quoted = !quoted; continue; }
                if (Char.IsWhiteSpace(c) && !quoted) { if (word.Length > 0) { result.Add(word.ToString()); word.Clear(); } }
                else word.Append(c);
            }
            if (quoted) throw new InvalidDataException("Незакрытая кавычка в стратегии.");
            if (word.Length > 0) result.Add(word.ToString());
            return result;
        }
        public static string Quote(string value) {
            if (value.Length > 0 && !value.Any(c => Char.IsWhiteSpace(c) || c == '"')) return value;
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (var c in value) {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); result.Append(c); slashes = 0;
            }
            result.Append('\\', slashes * 2); return result.Append('"').ToString();
        }
        public static string Arguments(string engine, string strategy) {
            if (Path.GetFileName(strategy) != strategy || !strategy.StartsWith("general", StringComparison.OrdinalIgnoreCase) || !strategy.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Недопустимое имя стратегии.");
            var text = File.ReadAllText(Path.Combine(engine, strategy), Encoding.UTF8);
            var match = Regex.Match(text, @"(?im)^start\s+[^\r\n]*?winws\.exe""?\s+([^\r\n]*(?:\r?\n[\s\S]*)?)$");
            if (!match.Success) throw new InvalidDataException("Не найдена команда winws.exe в стратегии.");
            // Capture only the continued winws command, never execute the batch file.
            var lines = match.Groups[1].Value.Replace("\r", "").Split('\n'); var command = new StringBuilder();
            foreach (var raw in lines) {
                var line = raw.Trim(); bool continued = line.EndsWith("^");
                command.Append(continued ? line.Substring(0, line.Length - 1) : line).Append(' ');
                if (!continued) break;
            }
            string tcp, udp; GamePorts(engine, out tcp, out udp);
            var expanded = command.ToString().Replace("%BIN%", Path.Combine(engine, "bin") + Path.DirectorySeparatorChar)
                .Replace("%LISTS%", Path.Combine(engine, "lists") + Path.DirectorySeparatorChar)
                .Replace("%GameFilterTCP%", tcp).Replace("%GameFilterUDP%", udp).Replace("%GameFilter%", tcp == "12" ? udp : tcp)
                .Replace("^!", "!");
            if (expanded.Contains("%") || expanded.Contains("^")) throw new InvalidDataException("Новая стратегия содержит неподдерживаемые переменные. Обновите оболочку.");
            var tokens = Split(expanded);
            if (tokens.Count == 0 || tokens.Any(x => !x.StartsWith("--", StringComparison.Ordinal)))
                throw new InvalidDataException("Неподдерживаемый формат параметров стратегии.");
            return String.Join(" ", tokens.Select(Quote));
        }
        public static void GamePorts(string engine, out string tcp, out string udp) {
            tcp = udp = "12";
            var file = Path.Combine(engine, "utils", "game_filter.enabled");
            if (!File.Exists(file)) return;
            var lines = File.ReadAllLines(file).Where(x => !String.IsNullOrWhiteSpace(x)).ToArray();
            var mode = "disabled"; var tcpPorts = "1024-65535"; var udpPorts = "1024-65535";
            foreach (var line in lines) {
                var parts = line.Trim().ToLowerInvariant().Split(new[] {'='}, 2);
                if (parts.Length == 1 && (parts[0] == "all" || parts[0] == "tcp" || parts[0] == "udp")) mode = parts[0];
                if (parts.Length == 2) {
                    if (parts[0] == "mode") mode = parts[1];
                    if (parts[0] == "tcp") tcpPorts = parts[1];
                    if (parts[0] == "udp") udpPorts = parts[1];
                }
            }
            if (!ValidPorts(tcpPorts) || !ValidPorts(udpPorts)) throw new InvalidDataException("Некорректные порты GameFilter.");
            if (mode == "all" || mode == "tcp") tcp = tcpPorts;
            if (mode == "all" || mode == "udp") udp = udpPorts;
        }
        public static bool ValidPorts(string value) {
            if (!Regex.IsMatch(value, @"^\d+(-\d+)?(,\d+(-\d+)?)*$")) return false;
            foreach (var part in value.Split(',')) {
                var range = part.Split('-'); int start, end;
                if (!Int32.TryParse(range[0], out start) || start < 1 || start > 65535) return false;
                if (range.Length == 2 && (!Int32.TryParse(range[1], out end) || end < start || end > 65535)) return false;
            }
            return true;
        }
        public static bool NeedsTcpTimestamps(string arguments) { return Regex.IsMatch(arguments, @"--dpi-desync-fooling=[^\s]*\bts\b"); }
        public static void ValidateEngine(string engine) {
            foreach (var name in new[] { "bin/winws.exe", "bin/WinDivert.dll", "bin/WinDivert64.sys", "service.bat", "lists/list-general.txt", "lists/list-google.txt", "lists/list-exclude.txt", "lists/ipset-all.txt", "lists/ipset-exclude.txt" })
                if (!File.Exists(Path.Combine(engine, name))) throw new InvalidDataException("В архиве отсутствует " + name);
            Store.EnsureUserLists(engine);
            var profiles = Directory.GetFiles(engine, "general*.bat");
            if (profiles.Length == 0) throw new InvalidDataException("В архиве нет стратегий.");
            foreach (var file in profiles) {
                var args = Split(Arguments(engine, Path.GetFileName(file)));
                foreach (var arg in args) {
                    var eq = arg.IndexOf('=');
                    if (eq < 0) continue;
                    var value = arg.Substring(eq + 1);
                    if (Path.IsPathRooted(value) && !File.Exists(value)) throw new InvalidDataException("Стратегия ссылается на отсутствующий файл: " + value);
                }
            }
        }
    }

    public sealed class DirectEngineRunner : IDisposable {
#if TESTING
        readonly bool testStub;
        public DirectEngineRunner(bool testStub=false){this.testStub=testStub;}
#endif
        private Process process;
        private OwnedJob job;
        bool timestampsEnabled;
        public event Action<string> Log;
        public bool Running { get { return process != null && !process.HasExited; } }
        public bool Conflict { get { return Process.GetProcessesByName("winws").Any(p => { using(p) { return process == null || p.Id != process.Id; } }); } }
        private void Write(string line) { if (Log != null && line != null) Log(line); }
        public void Start(string engine, string strategy) {
            if (Running) return;
            if (Conflict) throw new InvalidOperationException("Уже запущен другой winws.exe или служба zapret. Остановите её через исходный service.bat перед запуском здесь.");
            Stop();
#if TESTING
            if(!testStub)Trust.VerifyEngine(engine);
#else
            Trust.VerifyEngine(engine);
#endif
            Strategy.ValidateEngine(engine);
            var arguments = Strategy.Arguments(engine, strategy);
            if (!timestampsEnabled && Strategy.NeedsTcpTimestamps(arguments)) {
                // Equivalent to service.bat's tcp_enable, required for timestamp strategies.
                using (var netsh = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe"), "interface tcp set global timestamps=enabled") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                })) {
                    if (!netsh.WaitForExit(5000)) { netsh.Kill(); throw new IOException("Не удалось включить TCP timestamps за 5 секунд."); }
                    if (netsh.ExitCode != 0) throw new IOException("Не удалось включить TCP timestamps: " + netsh.StandardOutput.ReadToEnd() + netsh.StandardError.ReadToEnd());
                    timestampsEnabled=true; Write("TCP timestamps включены для выбранной стратегии, как в исходном service.bat.");
                }
            }
            var start = new ProcessStartInfo(Path.Combine(engine, "bin", "winws.exe"), arguments) {
                WorkingDirectory = Path.Combine(engine, "bin"), UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (s,e) => Write(e.Data);
            process.ErrorDataReceived += (s,e) => Write(e.Data);
            process.Exited += (s,e) => Write("Ядро остановлено. Если запуск не удался, проверьте сообщения выше.");
            process.Start();
            try { job = new OwnedJob(process); } catch { Stop(); throw; }
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            Write("Запуск: " + strategy);
        }
        public void Stop() {
            if (Running) { process.Kill(); if (!process.WaitForExit(5000)) throw new IOException("Ядро не завершилось. Повторите остановку."); }
            if (process != null) { process.Dispose(); process = null; }
            if (job != null) {job.Dispose(); job = null;}
        }
        public void Dispose() { Stop(); }
    }
}
