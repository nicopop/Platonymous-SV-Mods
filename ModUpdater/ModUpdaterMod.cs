﻿using HarmonyLib;
using StardewModdingAPI;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using Octokit;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Collections;

namespace ModUpdater
{
    public class ModUpdaterMod : Mod
    {
        internal static Config config;
        internal static List<ModUpdateManifest> updated;
        internal static GitHubClient github;
        internal static bool loggedNextUpdateCheck = false;
        internal static Dictionary<string, IReadOnlyList<RepositoryContent>> repoContents;
        internal static int update = 0;
        internal static bool shouldUpdate = false;
        private static IModHelper modHelper;
        private static IMonitor monitor;

        public static int ModUpdate()
        {
            updated = new List<ModUpdateManifest>();
            repoContents = new Dictionary<string, IReadOnlyList<RepositoryContent>>();
            string modsPath = (string)typeof(Constants).GetProperty("ModsPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);

            github = github ?? new GitHubClient(new ProductHeaderValue("Platonymous.ModUpdater", "1.0.0"));
            monitor.Log("Starting", LogLevel.Info );
            if (config.GitHubUser != "")
            {
                var basicAuth = new Credentials(config.GitHubUser, config.GitHubPassword);
                github.Credentials = basicAuth;
            }

            if ((DateTime.Now - config.LastUpdateCheck).TotalMinutes >= config.Interval)
            {
                config.LastUpdateCheck = DateTime.Now;
                shouldUpdate = true;
                modHelper.WriteConfig(config);
            }

            if (!shouldUpdate)
            {
                monitor.Log($"Will only check for update in {(DateTime.Now - config.LastUpdateCheck).TotalMinutes:F0} minutes", LogLevel.Info);
                return 0;
            }

            int result = 0;
            foreach (var manifestFile in Directory.GetFiles(modsPath, "manifest.json", SearchOption.AllDirectories)) {

                string dir = Path.GetDirectoryName(manifestFile);

                string short_dir = dir.Remove(0, modsPath.Length + 1);

                if (short_dir.StartsWith("."))
                    continue;

                if (new DirectoryInfo(dir).Name.StartsWith('.'))
                    continue;

                bool disabled = false;
                foreach (string folder in short_dir.Split('\\')){
                    if (folder.StartsWith('.'))
                    {
                        disabled = true;
                        break;
                    }
                }
                if (disabled)
                    continue;
                ModUpdateManifest mod = Newtonsoft.Json.JsonConvert.DeserializeObject<ModUpdateManifest>(File.ReadAllText(manifestFile));
                try
                {

                    if (config.Exclude.Contains(mod.UniqueID))
                        continue;

                    if (mod.ModUpdater.ModFolder == "" && !string.IsNullOrEmpty(mod.EntryDll))
                        mod.ModUpdater.ModFolder = mod.EntryDll.Substring(0, mod.EntryDll.Length - 4);

                    if (mod.ModUpdater.Repository != "")
                        result += CheckMod(modsPath, dir, mod);
                    else if (mod.Author == "Platonymous" && !string.IsNullOrEmpty(mod.EntryDll))
                    {
                        mod.ModUpdater = new PyModUpdateInformation(mod.EntryDll.Substring(0, mod.EntryDll.Length - 4));
                        result += CheckMod(modsPath, dir, mod);
                    }
                }
                catch (RateLimitExceededException e)
                {
                    monitor.Log("[" + mod.UniqueID + "] Updater failed: " + "API Rate Limit exceeded. Please try again later.", LogLevel.Error);
                    monitor.Log(e.Limit.ToString(), LogLevel.Error);
                    continue;
                }
                catch (Exception e)
                {
                    monitor.Log("[" + mod.UniqueID + "] Updater failed. Please try again later.", LogLevel.Error);
                    monitor.Log(e.StackTrace, LogLevel.Error);
                    continue;
                }
            }


            if (result > 0)
                patchModLoad();

            string tempFolder = Path.Combine(modHelper.DirectoryPath, "Temp");

            if (Directory.Exists(tempFolder))
                new DirectoryInfo(tempFolder).Delete(true);
            return result;
        }

        public static int CheckMod(string modsPath, string modFolder, ModUpdateManifest mod)
        {
            if (!shouldUpdate && !mod.ModUpdater.Install)
            {
                if (!loggedNextUpdateCheck)
                {
                    monitor.Log("Next update check: " + (config.LastUpdateCheck.AddMinutes(config.Interval).ToString("s")), LogLevel.Info);
                    loggedNextUpdateCheck = true;
                }
                    return 0;
            }

            string tempFolder = Path.Combine(modHelper.DirectoryPath, "Temp");
            var currentVersion = mod.Version;


            if (!Directory.Exists(tempFolder))
                Directory.CreateDirectory(tempFolder);

            var rkey = mod.ModUpdater.User + ">" + mod.ModUpdater.Repository + ">" + mod.ModUpdater.ModFolder;
            IReadOnlyList<RepositoryContent> rContent = null;
            if (repoContents.ContainsKey(rkey))
                rContent = repoContents[rkey];

            Repository repo = null;

            if (rContent == null)
            {
                var repoRequest = github.Repository.Get(mod.ModUpdater.User, mod.ModUpdater.Repository);
                repoRequest.Wait();
                repo = repoRequest.Result;
            }

            monitor.Log("Checking for updates: " + mod.Name, LogLevel.Info);
            monitor.Log("Current version: " + currentVersion, LogLevel.Info);


            if (rContent != null || repo is Repository)
            {
                if (rContent == null)
                {
                    var fileRequest = github.Repository.Content.GetAllContents(repo.Id, mod.ModUpdater.Directory);
                    fileRequest.Wait();
                    var files = fileRequest.Result;
                    rContent = files;
                    repoContents.Add(rkey, rContent);
                }

                var selector = mod.ModUpdater.FileSelector.Replace("{ModFolder}", mod.ModUpdater.ModFolder.Replace("[",@"\[").Replace("]", @"\]"));
                Regex findFile = new Regex(selector, RegexOptions.IgnoreCase | RegexOptions.Singleline);

                var filesFound = rContent.Where(f =>
                {
                   var m = findFile.Match(Path.GetFileNameWithoutExtension(f.Path));
                    return m.Success && m.Groups.Count == 2;
                });
                if (filesFound.Count() == 0)
                {
                    monitor.Log("File not found:" + selector, LogLevel.Trace);

                    return 0;
                }

                foreach (RepositoryContent file in filesFound)
                {
                    var fileName = Path.GetFileNameWithoutExtension(file.Path);
                    var match = findFile.Match(fileName);
                    string newVersion = match.Groups[1].Value;
                    if (SemanticVersion.TryParse(newVersion, out ISemanticVersion version)
                        && (mod.ModUpdater.Install || (shouldUpdate && SemanticVersion.TryParse(currentVersion, out ISemanticVersion current) && version.IsNewerThan(current))))
                    {
                        if (version.IsPrerelease() && !config.LoadPrereleases)
                            continue;

                        var url = file.DownloadUrl;
                        var tempFile = Path.Combine(tempFolder, Path.GetFileName(file.Path));

                        using (var client = new System.Net.Http.HttpClient())
                        {
                            using var stream = client.GetStreamAsync(url).Result;
                            using var fs = new FileStream(tempFile, System.IO.FileMode.Create);
                            stream.CopyToAsync(fs).Wait();
                        }

                        ModUpdateManifest updateManifest = null;

                        using (ZipArchive zip1 = ZipFile.OpenRead(tempFile))
                        {
                            if (zip1.Entries.FirstOrDefault(entry => entry.Name.Equals("manifest.json", StringComparison.InvariantCultureIgnoreCase)) is ZipArchiveEntry manifestEntry)
                            {
                                using (StreamReader sr = new StreamReader(manifestEntry.Open(), System.Text.Encoding.UTF8))
                                {
                                    if (Newtonsoft.Json.JsonConvert.DeserializeObject<ModUpdateManifest>(sr.ReadToEnd()) is ModUpdateManifest um)
                                        updateManifest = um;

                                    if (updateManifest is ModUpdateManifest
                                        && SemanticVersion.TryParse(updateManifest.MinimumApiVersion, out ISemanticVersion updateApiVersion)
                                        && Constants.ApiVersion.IsOlderThan(updateApiVersion))
                                    {
                                        monitor.Log("[ModUpdater] [" + updateManifest.UniqueID + "]" + "Could not update to version" + updateManifest.Version + ". Need at least SMAPI " + updateManifest.MinimumApiVersion, LogLevel.Error);
                                        continue;
                                    }
                                }
                            }else
                                continue;
                            foreach (ZipArchiveEntry e in zip1.Entries)
                            {
                                string filePath = e.FullName.Replace('/', '\\');
                                List<string> filePathParts = filePath.Split('\\').ToList();
                                filePathParts.RemoveAt(0);
                                filePath = Path.Combine(filePathParts.ToArray());
                                var tPath = Path.Combine(modFolder, filePath);
                                var tDirectory = Path.Combine(modFolder, Path.GetDirectoryName(filePath));
                                if (!Directory.Exists(tDirectory))
                                    Directory.CreateDirectory(tDirectory);

                                monitor.Log("[ModUpdater] " + " [" + mod.UniqueID + "] " + "Updating file: " + tPath, LogLevel.Info);

                                if (File.Exists(tPath) && updateManifest.ModUpdater.DoNotReplace.Contains(Path.GetFileName(tPath)))
                                    continue;

                                foreach (string dFile in updateManifest.ModUpdater.DeleteFiles)
                                {
                                    string dfilePath = Path.Combine(modFolder, Path.Combine(dFile.Replace('/', '\\').Split('\\')));
                                    monitor.Log("[ModUpdater] " + " [" + mod.UniqueID + "] " + "Deleting file: " + dFile, LogLevel.Info);

                                    if (File.Exists(dfilePath))
                                        File.Delete(dfilePath);
                                }

                                foreach (string dFolder in updateManifest.ModUpdater.DeleteFolders)
                                {
                                    string dFolderPath = Path.Combine(modFolder, Path.Combine(dFolder.Replace('/', '\\').Split('\\')));
                                    monitor.Log("[ModUpdater] " + " [" + mod.UniqueID + "] " + "Deleting folder: " + dFolder);

                                    if (Directory.Exists(dFolderPath))
                                        Directory.Delete(dFolderPath, true);
                                }
                                try
                                {
                                    e.ExtractToFile(tPath, true);
                                } catch(IOException ex)
                                {
                                    monitor.Log("[ModUpdater] [" + mod.UniqueID + "] Updating "+ Path.GetFileName(tempFile) + " failed. Please try again later or do it manually", LogLevel.Warn);
                                    monitor.Log("[ModUpdater] [" + mod.UniqueID + "] The update files moved to '" + Path.Combine(modHelper.DirectoryPath, "Manual") + "'", LogLevel.Warn);
                                    monitor.Log("[ModUpdater] [" + mod.UniqueID + "] Caused by: "+ ex.Message, LogLevel.Warn);
                                    string ManualPath = Path.Combine(tempFolder, "..\\Manual");
                                    string ManualFile = Path.Combine(ManualPath, Path.GetFileName(tempFile));
                                    if (!Directory.Exists(ManualPath))
                                        Directory.CreateDirectory(ManualPath);
                                    File.Copy(tempFile, ManualFile, true);
                                    return 0;
                                }
                            }
                        }

                        monitor.Log("[ModUpdater]  [" + mod.UniqueID + "] " + mod.Name + " was successfully updated to version " + version, LogLevel.Info);

                        if (updateManifest is ModUpdateManifest)
                            updated.Add(updateManifest);

                        return 1;
                    }
                }
            }

            return 0;
        }

        public static void patchModLoad()
        {
            Harmony harmony = new Harmony("Platonymous.ModUpdater");
            harmony.Patch(
                original:Type.GetType("StardewModdingAPI.Framework.SCore, StardewModdingAPI").GetMethod("CheckForUpdatesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance),
                prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ModUpdaterMod),nameof(CheckForUpdatesAsync)))
                );

            harmony.Patch(
                original: Type.GetType("StardewModdingAPI.Framework.SCore, StardewModdingAPI").GetMethod("TryLoadMod", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance),
                prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(ModUpdaterMod), nameof(TryLoadMod)))
                );
        }

        public static bool CheckForUpdatesAsync(object __instance, ref IModInfo[] mods)
        {
            if (update > 0 && config.AutoRestart && Constants.TargetPlatform == GamePlatform.Windows)
            {
                var smapi = Process.GetCurrentProcess();
                Process.Start(smapi.MainModule.FileName, string.IsNullOrEmpty(config.ExecutionArgs) ? null : config.ExecutionArgs);
                Environment.Exit(-1);
                return false;
            }

            return true;
        }

        public static void TryLoadMod(ref IModInfo mod)
        {
            if (update > 0)
                foreach (var u in updated)
                    if (SemanticVersion.TryParse(u.Version, out ISemanticVersion version) && u.UniqueID == mod.Manifest.UniqueID)
                        mod.Manifest.GetType().GetProperty("Version").SetValue(mod.Manifest, version);
        }

        public override void Entry(IModHelper helper)
        {
            modHelper = helper;
            monitor = Monitor;
            config = helper.ReadConfig<Config>();
            update = ModUpdate();
            helper.WriteConfig<Config>(config);

            helper.Events.GameLoop.GameLaunched += (s,e) =>
            {
                if (update > 0)
                    Monitor.Log(update + " Mod" + (update == 1 ? " was" : "s were") + " updated. A restart is recommended.", LogLevel.Warn);
            };
        }
    }
}
