using JsonFx.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Xml;
using UnityEngine;

namespace ModStatistics
{
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    internal class ModStatistics : MonoBehaviour
    {
        // The implementation with the highest version number will be allowed to run.
        private const int version = 2;
        private static int _version = version;

        private static readonly string folder = KSPUtil.ApplicationRootPath + "GameData" + Path.DirectorySeparatorChar + "ModStatistics" + Path.DirectorySeparatorChar;

        public void Start()
        {
            // Compatible types are identified by the type name and version field name.
            int highest =
                getAllTypes()
                .Where(t => t.Name == typeof(ModStatistics).Name)
                .Select(t => t.GetField("_version", BindingFlags.Static | BindingFlags.NonPublic))
                .Where(f => f != null)
                .Where(f => f.FieldType == typeof(int))
                .Max(f => (int)f.GetValue(null));

            // Let the latest version execute.
            if (version != highest) { return; }

            Debug.Log(String.Format("[ModStatistics] Running version {0}", _version));

            // Other checkers will see this version and not run.
            // This accomplishes the same as an explicit "ran" flag with fewer moving parts.
            _version = int.MaxValue;

            Directory.CreateDirectory(folder);

            var configpath = folder + "settings.cfg";
            var node = ConfigNode.Load(configpath);

            if (node == null)
            {
                createConfig(configpath);
            }
            else
            {
                var disabledString = node.GetValue("disabled");
                bool disabled;
                if (disabledString != null && bool.TryParse(disabledString, out disabled) && disabled)
                {
                    Debug.Log("[ModStatistics] Disabled in configuration file");
                    return;
                }

                var idString = node.GetValue("id");
                try
                {
                    id = new Guid(idString);
                }
                catch
                {
                    Debug.LogWarning("[ModStatistics] Could not parse ID");
                    createConfig(configpath);
                }
            }

            running = true;
            DontDestroyOnLoad(this);

            if (File.Exists(folder + "checkpoint.json"))
            {
                File.Move(folder + "checkpoint.json", createReportPath());
            }

            sendReports();
        }

        private void createConfig(string configpath)
        {
            id = Guid.NewGuid();
            Debug.Log("[ModStatistics] Creating new configuration file");
            var text = String.Format("// To disable ModStatistics, uncomment the line below." + Environment.NewLine + "// disabled = true" + Environment.NewLine + "id = {0:N}" + Environment.NewLine, id);
            File.WriteAllText(configpath, text);
        }

        private static IEnumerable<Type> getAllTypes()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (Exception)
                {
                    types = Type.EmptyTypes;
                }

                foreach (var type in types)
                {
                    yield return type;
                }
            }
        }

        private bool running = false;

        private Guid id;
        private GameScenes? scene = null;
        private DateTime started = DateTime.UtcNow;
        private DateTime sceneStarted = DateTime.UtcNow;
        private Dictionary<GameScenes, TimeSpan> sceneTimes = new Dictionary<GameScenes, TimeSpan>();
        private DateTime nextSave = DateTime.MinValue;

        public void FixedUpdate()
        {
            if (!running) { return; }

            if (scene != HighLogic.LoadedScene)
            {
                updateSceneTimes();
            }

            var now = DateTime.UtcNow;
            if (nextSave < now)
            {
                nextSave = now.AddSeconds(15);

                var report = prepareReport(true);
                File.WriteAllText(folder + "checkpoint.json", report);
            }
        }

        public void OnDestroy()
        {
            if (!running) { return; }

            Debug.Log("[ModStatistics] Saving report");
            File.WriteAllText(createReportPath(), prepareReport(false));

            File.Delete(folder + "checkpoint.json");
            sendReports();
        }

        private static string createReportPath()
        {
            int i = 0;
            string path;
            do
            {
                path = folder + "report-" + i + ".json";
                i++;
            } while (File.Exists(path));
            return path;
        }

        private void sendReports()
        {
            var files = Directory.GetFiles(folder, "report-*.json");
            using (var client = new WebClient())
            {
                client.Headers.Add(HttpRequestHeader.UserAgent, String.Format("ModStatistics/{0} ({1})", getInformationalVersion(Assembly.GetExecutingAssembly()), version));
                client.Headers.Add(HttpRequestHeader.ContentType, "application/json");
                foreach (var file in files)
                {
                    try
                    {
                        client.UploadString(@"http://stats.majiir.net/submit_report", File.ReadAllText(file));
                        Debug.Log("[ModStatistics] " + Path.GetFileName(file) + " sent successfully");
                        File.Delete(file);
                    }
                    catch (WebException e)
                    {
                        Debug.LogError(String.Format("[ModStatistics] Could not upload {0}:\n{1}", Path.GetFileName(file), e));
                        return;
                    }
                }
            }
        }

        private void updateSceneTimes()
        {
            Debug.Log("[ModStatistics] Updating scene times");

            var lastScene = scene;
            var lastStarted = sceneStarted;
            scene = HighLogic.LoadedScene;
            sceneStarted = DateTime.UtcNow;

            if (lastScene == null) { return; }

            if (!sceneTimes.ContainsKey(lastScene.Value))
            {
                sceneTimes[lastScene.Value] = TimeSpan.Zero;
            }

            sceneTimes[lastScene.Value] += (sceneStarted - lastStarted);
        }

        private string prepareReport(bool crashed)
        {
            updateSceneTimes();

            var report = new
            {
                started = started,
                finished = sceneStarted,
                crashed = crashed,
                statisticsVersion = version,
                id = id.ToString("N"),
                gameVersion = new
                {
                    build = Versioning.BuildID,
                    major = Versioning.version_major,
                    minor = Versioning.version_minor,
                    revision = Versioning.Revision,
                    experimental = Versioning.Experimental,
                    isBeta = Versioning.isBeta,
                    isSteam = Versioning.IsSteam,
                },
                scenes = sceneTimes.OrderBy(p => p.Key).ToDictionary(p => p.Key.ToString().ToLower(), p => p.Value.TotalMilliseconds),
                assemblies = from assembly in AssemblyLoader.loadedAssemblies.Skip(1)
                             let fileVersion = assembly.assembly.GetName().Version
                             select new
                             {
                                 dllName = assembly.dllName,
                                 name = assembly.name,
                                 url = assembly.url,
                                 kspVersionMajor = assembly.versionMajor,
                                 kspVersionMinor = assembly.versionMinor,
                                 fileVersion = new
                                 {
                                     major = fileVersion.Major,
                                     minor = fileVersion.Minor,
                                     revision = fileVersion.Revision,
                                     build = fileVersion.Build,
                                 },
                                 informationalVersion = getInformationalVersion(assembly.assembly),
                             },
            };

            return new JsonWriter().Write(report);
        }

        private static string getInformationalVersion(Assembly assembly)
        {
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion;
        }
    }
}