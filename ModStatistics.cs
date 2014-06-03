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
        private const int version = 1;
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
        private Dictionary<GameScenes, TimeSpan> sceneTimes = new Dictionary<GameScenes,TimeSpan>();

        public void FixedUpdate()
        {
            if (!running) { return; }

            if (scene != HighLogic.LoadedScene)
            {
                updateSceneTimes();
            }
        }

        public void OnDestroy()
        {
            if (!running) { return; }

            updateSceneTimes();

            var report = prepareReport();

            int i = 0;
            string path;
            do
            {
                path = folder + "report-" + i + ".xml";
                i++;
            } while (File.Exists(path));

            Debug.Log("[ModStatistics] Saving report");

            File.WriteAllText(path, report);

            sendReports();
        }

        private void sendReports()
        {
            var files = Directory.GetFiles(folder, "report-*.xml");
            using (var client = new WebClient())
            {
                client.Headers.Add(HttpRequestHeader.UserAgent, "ModStatistics/" + version);
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

        private string prepareReport()
        {
            var doc = new XmlDocument();
            var root = doc.DocumentElement;

            var declaration = doc.CreateXmlDeclaration("1.0", "UTF-8", null);
            doc.InsertBefore(declaration, root);

            var reportEl = doc.CreateElement("report");
            doc.AppendChild(reportEl);

            var el = doc.CreateElement("started");
            var text = doc.CreateTextNode(started.ToString("o"));
            el.AppendChild(text);
            reportEl.AppendChild(el);

            el = doc.CreateElement("finished");
            text = doc.CreateTextNode(DateTime.UtcNow.ToString("o"));
            el.AppendChild(text);
            reportEl.AppendChild(el);

            el = doc.CreateElement("statisticsVersion");
            text = doc.CreateTextNode(version.ToString());
            el.AppendChild(text);
            reportEl.AppendChild(el);

            el = doc.CreateElement("id");
            text = doc.CreateTextNode(id.ToString("N"));
            el.AppendChild(text);
            reportEl.AppendChild(el);

            var versionEl = doc.CreateElement("gameVersion");
            reportEl.AppendChild(versionEl);

            el = doc.CreateElement("major");
            text = doc.CreateTextNode(Versioning.version_major.ToString());
            el.AppendChild(text);
            versionEl.AppendChild(el);

            el = doc.CreateElement("minor");
            text = doc.CreateTextNode(Versioning.version_minor.ToString());
            el.AppendChild(text);
            versionEl.AppendChild(el);

            el = doc.CreateElement("revision");
            text = doc.CreateTextNode(Versioning.Revision.ToString());
            el.AppendChild(text);
            versionEl.AppendChild(el);

            el = doc.CreateElement("experimental");
            text = doc.CreateTextNode(Versioning.Experimental.ToString());
            el.AppendChild(text);
            versionEl.AppendChild(el);

            el = doc.CreateElement("build");
            text = doc.CreateTextNode(Versioning.BuildID.ToString());
            el.AppendChild(text);
            versionEl.AppendChild(el);

            el = doc.CreateElement("isBeta");
            text = doc.CreateTextNode(Versioning.isBeta.ToString());
            el.AppendChild(text);
            versionEl.AppendChild(el);

            el = doc.CreateElement("isSteam");
            text = doc.CreateTextNode(Versioning.IsSteam.ToString());
            el.AppendChild(text);
            versionEl.AppendChild(el);

            var scenesEl = doc.CreateElement("scenes");
            reportEl.AppendChild(scenesEl);

            foreach (var pair in sceneTimes)
            {
                el = doc.CreateElement(pair.Key.ToString().ToLower());
                text = doc.CreateTextNode(XmlConvert.ToString(pair.Value));
                el.AppendChild(text);
                scenesEl.AppendChild(el);
            }

            var assembliesEl = doc.CreateElement("assemblies");
            reportEl.AppendChild(assembliesEl);

            for (int i = 1; i < AssemblyLoader.loadedAssemblies.Count; i++)
            {
                var assembly = AssemblyLoader.loadedAssemblies[i];

                var assemblyEl = doc.CreateElement("assembly");
                assembliesEl.AppendChild(assemblyEl);

                el = doc.CreateElement("dllName");
                text = doc.CreateTextNode(assembly.dllName);
                el.AppendChild(text);
                assemblyEl.AppendChild(el);

                el = doc.CreateElement("name");
                text = doc.CreateTextNode(assembly.name);
                el.AppendChild(text);
                assemblyEl.AppendChild(el);

                el = doc.CreateElement("url");
                text = doc.CreateTextNode(assembly.url);
                el.AppendChild(text);
                assemblyEl.AppendChild(el);

                el = doc.CreateElement("versionMajor");
                text = doc.CreateTextNode(assembly.versionMajor.ToString());
                el.AppendChild(text);
                assemblyEl.AppendChild(el);

                el = doc.CreateElement("versionMinor");
                text = doc.CreateTextNode(assembly.versionMinor.ToString());
                el.AppendChild(text);
                assemblyEl.AppendChild(el);

                el = doc.CreateElement("fileVersion");
                text = doc.CreateTextNode(assembly.assembly.GetName().Version.ToString(4));
                el.AppendChild(text);
                assemblyEl.AppendChild(el);

                el = doc.CreateElement("informationalVersion");
                text = doc.CreateTextNode(System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.assembly.Location).ProductVersion);
                el.AppendChild(text);
                assemblyEl.AppendChild(el);
            }

            return doc.OuterXml;
        }
    }
}