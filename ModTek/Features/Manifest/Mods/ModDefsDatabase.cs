﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ModTek.Logging;
using ModTek.Misc;
using ModTek.UI;
using ModTek.Util;

namespace ModTek.Features.Manifest.Mods
{
    internal static class ModDefsDatabase
    {
        internal static List<string> ModLoadOrder;
        internal static Dictionary<string, ModDefEx> ModDefs = new();
        internal static Dictionary<string, ModDefEx> allModDefs = new();
        internal static HashSet<string> FailedToLoadMods { get; } = new();

        // TODO is this needed?
        internal static IEnumerator<ProgressReport> GatherDependencyTreeLoop()
        {
            yield return new ProgressReport(0, "Gathering dependencies trees", "");
            if (allModDefs.Count == 0)
            {
                yield break;
            }

            var progress = 0;
            foreach (var mod in allModDefs)
            {
                ++progress;
                foreach (var depname in mod.Value.DependsOn)
                {
                    if (allModDefs.ContainsKey(depname))
                    {
                        if (allModDefs[depname].DependsOnMe.Contains(mod.Value) == false)
                        {
                            allModDefs[depname].DependsOnMe.Add(mod.Value);
                        }
                    }
                }
            }

            yield return new ProgressReport(1 / 3f, "Gather depends on me", string.Empty, true);
            progress = 0;
            foreach (var mod in allModDefs)
            {
                ++progress;
                mod.Value.GatherAffectingOfflineRec();
            }

            yield return new ProgressReport(2 / 3f, "Gather disable influence tree", string.Empty, true);
            progress = 0;
            foreach (var mod in allModDefs)
            {
                ++progress;
                mod.Value.GatherAffectingOnline();
            }

            yield return new ProgressReport(1, "Gather enable influence tree", string.Empty, true);
            Logger.Log("FAIL LIST:");
            foreach (var mod in allModDefs.Values)
            {
                if (mod.Enabled == false)
                {
                    continue;
                }

                ;
                if (mod.LoadFail == false)
                {
                    continue;
                }

                Logger.Log($"\t{mod.Name} fail {mod.FailReason}");
                foreach (var dmod in mod.AffectingOnline)
                {
                    var state = dmod.Key.Enabled && dmod.Key.LoadFail == false;
                    if (state != dmod.Value)
                    {
                        Logger.Log($"\t\tdepends on {dmod.Key.Name} should be loaded:{dmod.Value} but it is not cause enabled:{dmod.Key.Enabled} and fail:{dmod.Key.LoadFail} due to {dmod.Key.FailReason}");
                    }
                }

                foreach (var deps in mod.DependsOn)
                {
                    if (allModDefs.ContainsKey(deps) == false)
                    {
                        Logger.Log($"\t\tdepends on {deps} but abcent");
                    }
                }
            }
        }

        internal static List<ModDefEx> ModsInLoadOrder()
        {
            return ModLoadOrder
                .Where(name => ModDefs.ContainsKey(name))
                .Select(name => ModDefs[name])
                .ToList();
        }

        internal static IEnumerator<ProgressReport> InitModsLoop()
        {
            yield return new ProgressReport(1, "Initializing Mods", "");

            // find all sub-directories that have a mod.json file
            var modJsons = Directory.GetFiles(FilePaths.ModsDirectory, FilePaths.MOD_JSON_NAME, SearchOption.AllDirectories);

            if (modJsons.Length == 0)
            {
                Logger.Log("No ModTek-compatible mods found.");
                yield break;
            }

            CreateModDefs(modJsons);
            SetupModLoadOrderAndRemoveUnloadableMods();

            // try loading each mod
            var numModsLoaded = 0;
            Logger.Log("");
            foreach (var modName in ModLoadOrder)
            {
                var modDef = ModDefs[modName];

                if (modDef.DependsOn.Intersect(FailedToLoadMods).Any())
                {
                    ModDefs.Remove(modName);
                    if (!modDef.IgnoreLoadFailure)
                    {
                        Logger.Log($"Warning: Skipping load of {modName} because one of its dependencies failed to load.");
                        if (allModDefs.ContainsKey(modName))
                        {
                            allModDefs[modName].LoadFail = true;
                            allModDefs[modName].FailReason = $"Warning: Skipping load of {modName} because one of its dependencies failed to load.";
                        }

                        FailedToLoadMods.Add(modName);
                    }

                    continue;
                }

                yield return new ProgressReport(numModsLoaded++ / (float) ModLoadOrder.Count, "Initializing Mods", $"{modDef.Name} {modDef.Version}", true);

                try
                {
                    if (!ModDefExLoading.LoadMod(modDef, out var reason))
                    {
                        ModDefs.Remove(modName);

                        if (!modDef.IgnoreLoadFailure)
                        {
                            FailedToLoadMods.Add(modName);
                            if (allModDefs.ContainsKey(modName))
                            {
                                allModDefs[modName].LoadFail = true;
                                allModDefs[modName].FailReason = reason;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    ModDefs.Remove(modName);

                    if (modDef.IgnoreLoadFailure)
                    {
                        continue;
                    }

                    Logger.Log($"Error: Tried to load mod: {modDef.Name}, but something went wrong. Make sure all of your JSON is correct!", e);
                    FailedToLoadMods.Add(modName);
                    if (allModDefs.ContainsKey(modName))
                    {
                        allModDefs[modName].LoadFail = true;
                        allModDefs[modName].FailReason = "Error: Tried to load mod: " + modDef.Name + ", but something went wrong. Make sure all of your JSON is correct!" + e;
                    }
                }
            }
        }

        private static void CreateModDefs(string[] modJsons)
        {
            // create ModDef objects for each mod.json file
            foreach (var modDefPath in modJsons)
            {
                ModDefEx modDef;
                try
                {
                    modDef = ModDefEx.CreateFromPath(modDefPath);
                    if (modDef.Name == ModTek.MODTEK_DEF_NAME)
                    {
                        modDef = ModTek.SettingsDef;
                    }
                }
                catch (Exception e)
                {
                    var modDir = Path.GetDirectoryName(modDefPath);
                    var modDirRelative = FileUtils.GetRelativePath(FilePaths.ModsDirectory, modDir);
                    FailedToLoadMods.Add(modDirRelative);
                    Logger.Log($"Error: Caught exception while parsing {modDefPath}", e);
                    continue;
                }

                if (allModDefs.ContainsKey(modDef.Name) == false)
                {
                    allModDefs.Add(modDef.Name, modDef);
                }
                else
                {
                    var counter = 0;
                    var tmpname = modDef.Name;
                    do
                    {
                        ++counter;
                        tmpname = modDef.Name + "{dublicate " + counter + "}";
                    }
                    while (allModDefs.ContainsKey(tmpname));

                    modDef.Name = tmpname;
                    modDef.Enabled = false;
                    modDef.LoadFail = true;
                    modDef.FailReason = "dublicate";
                    allModDefs.Add(modDef.Name, modDef);
                    continue;
                }

                if (!modDef.ShouldTryLoad(ModDefs.Keys.ToList(), out var reason, out _))
                {
                    Logger.Log($"Not loading {modDef.Name} because {reason}");
                    if (!modDef.IgnoreLoadFailure)
                    {
                        FailedToLoadMods.Add(modDef.Name);
                        if (allModDefs.ContainsKey(modDef.Name))
                        {
                            allModDefs[modDef.Name].LoadFail = true;
                            modDef.FailReason = reason;
                        }
                    }

                    continue;
                }

                ModDefs.Add(modDef.Name, modDef);
            }
        }

        private static void SetupModLoadOrderAndRemoveUnloadableMods()
        {
            // get a load order and remove mods that won't be loaded
            ModLoadOrder = LoadOrder.CreateLoadOrder(ModDefs, out var notLoaded, LoadOrder.FromFile(FilePaths.LoadOrderPath));
            foreach (var modName in notLoaded)
            {
                var modDef = ModDefs[modName];
                ModDefs.Remove(modName);
                if (modDef.IgnoreLoadFailure)
                {
                    continue;
                }

                if (allModDefs.ContainsKey(modName))
                {
                    allModDefs[modName].LoadFail = true;
                    allModDefs[modName].FailReason = $"Warning: Will not load {modName} because it's lacking a dependency or has a conflict.";
                }

                Logger.Log($"Warning: Will not load {modName} because it's lacking a dependency or has a conflict.");
                FailedToLoadMods.Add(modName);
            }
        }

        internal static void FinishedLoadingMods()
        {
            if (ModLoadOrder == null || ModLoadOrder.Count <= 0)
            {
                return;
            }

            {
                Logger.Log("\nCalling FinishedLoading:");
                foreach (var modDef in ModLoadOrder
                    .Where(name => ModDefs.ContainsKey(name) && ModDefs[name].Assembly != null)
                    .Select(assemblyMod => ModDefs[assemblyMod])
                )
                {
                    ModDefExLoading.FinishedLoading(modDef, ModLoadOrder);
                }
            }
            HarmonyUtils.PrintHarmonySummary(FilePaths.HarmonySummaryPath);
            LoadOrder.ToFile(ModLoadOrder, FilePaths.LoadOrderPath);
        }
    }
}