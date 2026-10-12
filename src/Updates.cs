using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ZapretDesktop {
    public sealed class Release {
        public string Version, Url, Digest, Notes, Proof;
        public long Size;
    }
    public sealed class Updater : IDisposable {
        private readonly HttpClient client;
        private readonly Store store;
        public Updater(Store store) {
            this.store = store;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ByPass/"+ShellUpdates.Current);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }
        public async Task<Release> Check() {
            var json = await GitHubMetadata.Get(client,"https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest");
            var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            var version = Convert.ToString(data["tag_name"]); store.EnginePath(version);
            var assets = (ArrayList)data["assets"];
            foreach (Dictionary<string, object> asset in assets) {
                if (Convert.ToString(asset["name"]) != "zapret-discord-youtube-" + version + ".zip") continue;
                var release = new Release { Version = version, Url = Convert.ToString(asset["browser_download_url"]),
                    Digest = asset.ContainsKey("digest") ? Convert.ToString(asset["digest"]) : "",
                    Size = Convert.ToInt64(asset["size"]), Notes = Convert.ToString(data["body"]) };
                if (release.Url != "https://github.com/Flowseal/zapret-discord-youtube/releases/download/" + version + "/zapret-discord-youtube-" + version + ".zip")
                    throw new InvalidDataException("GitHub вернул неожиданный адрес архива.");
                if (!Regex.IsMatch(release.Digest ?? "", @"^sha256:[a-fA-F0-9]{64}$"))
                    throw new InvalidDataException("Для релиза нет SHA-256. Автоустановка отключена; откройте страницу релизов.");
                if (release.Size < 1 || release.Size > 64 * 1024 * 1024) throw new InvalidDataException("Неожиданный размер архива.");
                Trust.Archive(release.Version,release.Digest);
                return release;
            }
            throw new InvalidDataException("В релизе не найден официальный ZIP-архив.");
        }
        public static void Extract(string zip, string destination) {
            Directory.CreateDirectory(destination);
            var prefix = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            long expanded = 0;
            using (var archive = ZipFile.OpenRead(zip)) {
                if (archive.Entries.Count > 2000) throw new InvalidDataException("Слишком много файлов в архиве.");
                foreach (var entry in archive.Entries) {
                    var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    if (relative.Contains(":") || Path.IsPathRooted(relative)) throw new InvalidDataException("Недопустимый путь в архиве.");
                    var path = Path.GetFullPath(Path.Combine(destination, relative));
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Путь выходит за пределы архива.");
                    if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Символьные ссылки в архиве не поддерживаются.");
                    expanded += entry.Length;
                    if (expanded > 200 * 1024 * 1024) throw new InvalidDataException("Слишком большой распакованный архив.");
                    if (entry.Name.Length == 0) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var input = entry.Open()) using (var output = new FileStream(path, FileMode.CreateNew)) input.CopyTo(output);
                }
            }
        }
        public static string FindEngine(string folder) {
            var found = new[] { folder }.Concat(Directory.GetDirectories(folder)).Where(x => File.Exists(Path.Combine(x, "bin", "winws.exe"))).ToArray();
            if (found.Length != 1) throw new InvalidDataException("Неоднозначная структура архива ядра.");
            return found[0];
        }
        public static void Preserve(string oldEngine, string newEngine) {
            foreach (var file in Directory.GetFiles(Path.Combine(oldEngine, "lists"), "*-user.txt"))
                File.Copy(file, Path.Combine(newEngine, "lists", Path.GetFileName(file)), true);
            // Preserve locally edited IP sets; use fresh upstream list if unchanged.
            var manifest = Path.Combine(oldEngine, ".upstream-ipset.sha256");
            var ipset = Path.Combine(oldEngine, "lists", "ipset-all.txt");
            var isCustom = !File.Exists(manifest) || Hash(ipset) != File.ReadAllText(manifest).Trim();
            if (isCustom) File.Copy(ipset, Path.Combine(newEngine, "lists", "ipset-all.txt"), true);
            foreach (var relative in new[] { "lists/ipset-all.txt.backup", "utils/game_filter.enabled", "bin/ACTIVE_GAME_UDP.bin", "bin/ACTIVE_DISCORD_UDP.bin" }) {
                var source = Path.Combine(oldEngine, relative); var target = Path.Combine(newEngine, relative);
                if (File.Exists(source)) { Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(source, target, true); }
                else if (relative == "utils/game_filter.enabled" && File.Exists(target)) File.Delete(target);
            }
        }
        public static string Hash(string path) {
            using (var sha = SHA256.Create()) using (var file = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }
        public async Task Install(Release release, Action<string> progress) {
            Trust.Archive(release.Version,release.Digest);
            var staging = Path.Combine(store.Root, "data", "staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try {
                var zip = Path.Combine(staging, "release.zip");
                progress("Загрузка " + release.Version + "…");
                using (var response = await client.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead)) {
                    response.EnsureSuccessStatusCode();
                    using (var input = await response.Content.ReadAsStreamAsync()) using (var output = File.Create(zip)) {
                        var buffer = new byte[81920]; long received = 0; int count;
                        while ((count = await input.ReadAsync(buffer, 0, buffer.Length)) > 0) {
                            received += count;
                            if (received > release.Size) throw new InvalidDataException("Архив больше заявленного размера.");
                            await output.WriteAsync(buffer, 0, count);
                        }
                        if (received != release.Size) throw new InvalidDataException("Архив загружен не полностью.");
                    }
                }
                if (!String.Equals("sha256:" + Hash(zip), release.Digest, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 архива не совпадает. Обновление отменено.");
                progress("Проверка файлов и сохранение ваших настроек…");
                var extracted = Path.Combine(staging, "unpacked");
                await Task.Run(() => Extract(zip, extracted));
                var engine = FindEngine(extracted); Trust.VerifyEngine(engine);Strategy.ValidateEngine(engine);
                File.WriteAllText(Path.Combine(engine, ".upstream-ipset.sha256"), Hash(Path.Combine(engine, "lists", "ipset-all.txt")));
                Preserve(store.Current, engine);Trust.VerifyEngine(engine); Strategy.ValidateEngine(engine);
                var versionFolder = release.Version + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                Directory.Move(engine, store.EnginePath(versionFolder));
                var old = store.Settings.Engine; var previous = store.Settings.PreviousEngine;
                store.Settings.PreviousEngine = old; store.Settings.Engine = versionFolder;
                try { store.Save(); }
                catch { store.Settings.Engine = old; store.Settings.PreviousEngine = previous; throw; }
                progress("Установлено ядро " + release.Version + ". Можно включать обход.");
            } finally { try { Directory.Delete(staging, true); } catch { } }
        }
        public void Rollback() {
            var previous = store.Settings.PreviousEngine;
            if (String.IsNullOrEmpty(previous)) throw new InvalidOperationException("Нет предыдущей версии.");
            Trust.VerifyEngine(store.EnginePath(previous));
            Strategy.ValidateEngine(store.EnginePath(previous));
            var current = store.Settings.Engine;
            // Stage the rollback so a failed copy cannot damage the saved previous engine.
            var restored = "rollback-" + Guid.NewGuid().ToString("N");
            var destination = store.EnginePath(restored);
            CopyTree(store.EnginePath(previous), destination);
            Preserve(store.Current, destination);Trust.VerifyEngine(destination); Strategy.ValidateEngine(destination);
            store.Settings.Engine = restored; store.Settings.PreviousEngine = current;
            try { store.Save(); } catch { store.Settings.Engine = current; store.Settings.PreviousEngine = previous; throw; }
        }
        static void CopyTree(string source, string target) {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            foreach (var folder in Directory.GetDirectories(source)) CopyTree(folder, Path.Combine(target, Path.GetFileName(folder)));
        }
        public static string Version(string engine) {
            var file = Path.Combine(engine, "service.bat");
            if (!File.Exists(file)) return "не установлено";
            var m = Regex.Match(File.ReadAllText(file), "LOCAL_VERSION=([^\"\\r\\n]+)");
            return m.Success ? m.Groups[1].Value : "неизвестно";
        }
        public void Dispose() { client.Dispose(); }
    }
}
