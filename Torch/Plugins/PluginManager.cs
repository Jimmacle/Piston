using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using NLog;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.API.Session;
using Torch.API.WebAPI;
using Torch.Collections;
using Torch.Commands;
using Torch.Utils;

namespace Torch.Managers
{
    /// <inheritdoc />
    public class PluginManager : Manager, IPluginManager
    {
        private class PluginItem
        {
            public string Path { get; set; }
            public PluginManifest Manifest { get; set; }
            public List<PluginItem> ResolvedDependencies { get; set; }
            public PluginState State { get; set; }

            public override int GetHashCode()
            {
                return Manifest.Guid.GetHashCode();
            }
        }
        
        private static Logger _log = LogManager.GetCurrentClassLogger();
        
        private const string MANIFEST_NAME = "manifest.xml";
        
        public readonly string PluginDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins");
        private readonly MtObservableSortedDictionary<Guid, ITorchPlugin> _plugins = new MtObservableSortedDictionary<Guid, ITorchPlugin>();
        private CommandManager _mgr;
        
#pragma warning disable 649
        [Dependency]
        private ITorchSessionManager _sessionManager;
#pragma warning restore 649
        
        /// <inheritdoc />
        public IReadOnlyDictionary<Guid, ITorchPlugin> Plugins => _plugins.AsReadOnlyObservable();

        public event Action<IReadOnlyCollection<ITorchPlugin>> PluginsLoaded;
        
        public PluginManager(ITorchBase torchInstance) : base(torchInstance)
        {
            if (!Directory.Exists(PluginDir))
                Directory.CreateDirectory(PluginDir);
            PluginQuery.SetPluginPath(PluginDir);
        }

        /// <summary>
        /// Updates loaded plugins in parallel.
        /// </summary>
        public void UpdatePlugins()
        {
            foreach (var plugin in _plugins.Values)
            {
                try
                {
                    if(plugin.State == PluginState.Enabled || plugin.State == PluginState.UninstallRequested)
                        plugin.Update();
                }
                catch (Exception ex)
                {
                    _log.Error(ex, $"Plugin {plugin.Name} threw an exception during update!");
                }
            }
        }
        
        /// <inheritdoc/>
        public override void Attach()
        {
            base.Attach();
            _sessionManager.SessionStateChanged += SessionManagerOnSessionStateChanged;
        }

        public void InsertDummy(string pluginName, Guid guid)
        {
            var manifest = new PluginManifest()
                           {
                               Guid = guid,
                               Name = pluginName
                           };
            var item = new PluginItem()
                       {
                           Manifest = manifest,
                           State = PluginState.NotInstalled
                       };

            RegisterEmpty(item);

            PluginsLoaded?.Invoke(_plugins.Values.AsReadOnly());
        }

        private void SessionManagerOnSessionStateChanged(ITorchSession session, TorchSessionState newState)
        {
            _mgr = session.Managers.GetManager<CommandManager>();
            if (_mgr == null)
                return;
            switch (newState)
            {
                case TorchSessionState.Loaded:
                    foreach (ITorchPlugin plugin in _plugins.Values)
                        _mgr.RegisterPluginCommands(plugin);
                    return;
                case TorchSessionState.Unloading:
                    foreach (ITorchPlugin plugin in _plugins.Values)
                        _mgr.UnregisterPluginCommands(plugin);
                    return;
                case TorchSessionState.Loading:
                case TorchSessionState.Unloaded:
                default:
                    return;
            }
        }
        
        /// <summary>
        /// Unloads all plugins.
        /// </summary>
        public override void Detach()
        {
            _sessionManager.SessionStateChanged -= SessionManagerOnSessionStateChanged;
            foreach (var plugin in _plugins.Values)
                plugin.Dispose();

            _plugins.Clear();
        }

        public void LoadPlugins()
        {
            _log.Info("Loading plugins...");

            Torch.Config.Plugins = Torch.Config.Plugins.Distinct().ToList();
            Torch.Config.DisabledPlugins = Torch.Config.DisabledPlugins.Distinct().ToList();

            if (!string.IsNullOrEmpty(Torch.Config.TestPlugin))
            {
                _log.Info($"Loading plugin for debug at {Torch.Config.TestPlugin}");

                foreach (var item in GetLocalPlugins(Torch.Config.TestPlugin, true))
                {
                    _log.Info(item.Path);
                    LoadPlugin(item);
                }

                foreach (var plugin in _plugins.Values)
                {
                    plugin.Init(Torch);
                }
                _log.Info($"Loaded {_plugins.Count} plugins.");
                PluginsLoaded?.Invoke(_plugins.Values.AsReadOnly());
                return;
            }

            //first scan for plugins on disk
            var pluginItems = GetLocalPlugins(PluginDir);

            //update what we have before resolving dependencies
            bool updateSuccess = Torch.Config.ShouldUpdatePlugins && DownloadPluginUpdates(pluginItems, Torch.Config.BetaPlugins);

            var pluginsToLoad = new List<PluginItem>();
            bool downloadDependencies = Torch.Config.DownloadDependencies;

            if(!downloadDependencies)
                _log.Warn("Dependency download is disabled. Some plugins may fail to load without dependencies!");

            //resolve dependencies for all plugins. Will download dependencies if enabled by config
            Task.WaitAll(pluginItems.Select( async item =>
                                          {
                                              try
                                              {
                                                  var missing = await ResolveDependencies(pluginItems, item, downloadDependencies);

                                                  //found some dependencies we couldn't resolve
                                                  //only happens if user sets DownloadDependencies false, or a plugin depends
                                                  //on a plugin not published on Torch website (or plugin author fat-fingered dependency ID)
                                                  foreach (var missingPlugin in missing)
                                                      _log.Warn($"{item.Manifest.Name} is missing dependency {missingPlugin}. Skipping plugin.");
                                                  if (missing.Any())
                                                      item.State = PluginState.MissingDependency;
                                              }
                                              catch (Exception ex)
                                              {
                                                  _log.Error(ex, $"Error resolving plugin dependencies! Failed item: {item.Manifest.Name}");
                                                  item.State = PluginState.DisabledError;
                                              }
                                          }).ToArray());

            //sort plugins by load order in torch config
            for(int i = 0; i < Torch.Config.Plugins.Count; i++)
            {
                var id = Torch.Config.Plugins[i];
                int idx = pluginItems.FindIndex(p => p.Manifest.Guid == id);
                if (idx != -1)
                {
                    pluginsToLoad.Add(pluginItems[idx]);
                    pluginItems.RemoveAtFast(idx);
                }
            }
            
            //add any plugins not listed in config (dependencies)
            pluginsToLoad.AddList(pluginItems);

            // Sort based on dependencies.
            //inserts in top-down order
            try
            {
                pluginsToLoad = DependencySort(pluginsToLoad);
            }
            catch (Exception e)
            {
                // This will happen on cyclic dependencies.
                _log.Error(e);
            }

            // Actually load the plugins now.
            for(int i = 0; i < pluginsToLoad.Count; i++)
            {
                LoadPlugin(pluginsToLoad[i]);
            }
            
            foreach (var plugin in _plugins.Values)
            {
                if(plugin.State == PluginState.Enabled)
                    plugin.Init(Torch);
            }
            _log.Info($"Loaded {_plugins.Count} plugins.");
            PluginsLoaded?.Invoke(_plugins.Values.AsReadOnly());
        }
        
        //debug flag is set when the user asks us to run with a specific plugin for plugin development debug
        //please do not change references to this arg unless you are very sure you know what you're doing
        private List<PluginItem> GetLocalPlugins(string pluginDir, bool debug = false)
        {
            var firstLoad = Torch.Config.Plugins.Count + Torch.Config.DisabledPlugins.Count == 0;
            
            //unzip legacy plugins because we don't support loading straight from zip anymore
            var zips = Directory.EnumerateFiles(pluginDir, "*.zip");
            foreach(var zip in zips)
                UnzipLegacyPlugin(zip);

            var pluginItems = Directory.EnumerateDirectories(pluginDir);
            if (debug)
                pluginItems = pluginItems.Union(new List<string> {pluginDir});
            var results = new ConcurrentQueue<PluginItem>();

            foreach (var path in pluginItems)
            {
                var manifest = GetManifestFromDirectory(path);

                if (manifest == null)
                {
                    if (!debug)
                    {
                        _log.Warn($"Item '{path}' is missing a manifest, skipping.");
                        continue;
                    }
                    manifest = new PluginManifest()
                               {
                                   Guid = new Guid(),
                                   Version = "0",
                                   Name = "TEST"
                               };
                }

                var duplicatePlugin = results.FirstOrDefault(r => r.Manifest.Guid == manifest.Guid);
                if (duplicatePlugin != null)
                {
                    _log.Warn($"The GUID provided by {manifest.Name} ({path}) is already in use by {duplicatePlugin.Manifest.Name}.");
                    continue;
                }
                
                if (!Torch.Config.LocalPlugins && !debug)
                {
                    if (!Torch.Config.Plugins.Contains(manifest.Guid) && !Torch.Config.DisabledPlugins.Contains(manifest.Guid))
                    {
                        if (!firstLoad)
                        {
                            _log.Warn($"Plugin {manifest.Name} ({path}) exists in the plugin directory, but is not listed in torch.cfg. Skipping load!");
                            continue;
                        }
                        _log.Info($"First-time load: Plugin {manifest.Name} added to torch.cfg.");
                        Torch.Config.Plugins.Add(manifest.Guid);
                    }
                }
                
                results.Enqueue(new PluginItem
                {
                    Manifest = manifest,
                    Path = path
                });
            }
            
            //foreach (var req in Torch.Config.Plugins)
            Task.WaitAll(Torch.Config.Plugins.Select( async req =>
            {
                if (!results.Any(p => p.Manifest.Guid == req))
                {
                    if (Torch.Config.ShouldUpdatePlugins)
                    {
                        (bool success, string path) = await PluginQuery.Instance.DownloadPlugin(req, Torch.Config.BetaPlugins);

                        if(!success)
                            _log.Error($"Failed to download plugin {req}! Either Torch website is unavaible, or this plugin is not published on Torch website");
                        else
                            results.Enqueue(new PluginItem()
                                            {
                                                Manifest = GetManifestFromDirectory(path),
                                                Path = path
                                            });
                    }
                    else
                    {
                        _log.Error($"Plugin {req} is listed in Torch config, but is not on disk! Skipping load!");
                    }
                }
            }).ToArray());

            if (!Torch.Config.LocalPlugins && firstLoad)
                Torch.Config.Save();
            
            return results.ToList();
        }

        private void UnzipLegacyPlugin(string path)
        {
            try
            {
                using (var zip = ZipFile.OpenRead(path))
                {
                    PluginManifest manifest = null;
                    foreach (var entry in zip.Entries)
                    {
                        if (!entry.Name.Equals(MANIFEST_NAME, StringComparison.CurrentCultureIgnoreCase))
                            continue;

                        using (var stream = new StreamReader(entry.Open()))
                        {
                            manifest = PluginManifest.Load(stream);
                            break;
                        }
                    }

                    if (manifest == null)
                    {
                        _log.Warn($"Zip file {path} does not contain a manifest!");
                        return;
                    }

                    zip.ExtractToDirectory(Path.Combine(PluginDir, $"{manifest.Name} - {manifest.Guid}"));
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to unzip plugin at {path}. Zip file is likely corrupt!");
                _log.Error(ex);
                return;
            }

            File.Delete(path);
        }

        private bool DownloadPluginUpdates(List<PluginItem> plugins, bool beta)
        {
            int count = 0;
            Task.WaitAll(plugins.Select(async item =>
                                        {
                                            bool res = await DownloadPluginUpdate(item, beta);
                                            if (res)
                                                Interlocked.Increment(ref count);
                                        }).ToArray());

            _log.Info($"Updated {count} plugins");

            return count > 0;
        }

        private async Task<bool> DownloadPluginUpdate(PluginItem item, bool beta)
        {
            _log.Info("Checking for plugin updates...");
            try
            {
                item.Manifest.Version.TryExtractVersion(out Version currentVersion);
                var pgi = await PluginQuery.Instance.QueryOne(item.Manifest.Guid);

                if (pgi?.Versions == null)
                {
                    _log.Warn($"Plugin {item.Manifest.Name} does not have any releases on torchapi.net. Cannot update.");
                    return false;
                }

                var latest = pgi.Versions.FirstOrDefault(v => !v.IsBeta || beta);

                if (latest == null)
                {
                    _log.Warn($"Could not find latest release for plugin {item.Manifest.Name}");
                    return false;
                }
                
                latest.Version.TryExtractVersion(out Version newVersion);

                if (currentVersion == null || newVersion == null)
                {
                    _log.Error($"Error parsing version from manifest or website for plugin '{item.Manifest.Name}.'");
                    return false;
                }

                if (newVersion == currentVersion)
                {
                    _log.Debug($"{item.Manifest.Name} {item.Manifest.Version} is up to date.");
                    return false;
                }

                _log.Info($"Updating plugin '{item.Manifest.Name}' from {currentVersion} to {newVersion}.");
                var res = await PluginQuery.Instance.DownloadPlugin(pgi, Torch.Config.BetaPlugins);

                return res.Item1;
            }
            catch (Exception e)
            {
                _log.Warn($"An error occurred updating the plugin {item.Manifest.Name}.");
                _log.Warn(e);
                return false;
            }
        }

        private void LoadPlugin(PluginItem item)
        {
            if (item.State != PluginState.NotInitialized)
            {
                RegisterEmpty(item);
                return;
            }

            if (Torch.Config.DisabledPlugins.Contains(item.Manifest.Guid))
            {
                item.State = PluginState.DisabledUser;
                RegisterEmpty(item);
                return;
            }
            
            var assemblies = new List<Assembly>();

            var files = Directory.EnumerateFiles(item.Path, "*.*", SearchOption.AllDirectories).ToList();

            foreach (var file in files)
            {
                if (!file.EndsWith(".dll", StringComparison.CurrentCultureIgnoreCase))
                    continue;
                
                assemblies.Add(Assembly.LoadFrom(file));
            }

            RegisterAllAssemblies(assemblies);
            InstantiatePlugin(item, assemblies);
        }

        private void RegisterAllAssemblies(IReadOnlyCollection<Assembly> assemblies)
        {
            Assembly ResolveDependentAssembly(object sender, ResolveEventArgs args)
            {
                var requiredAssemblyName = new AssemblyName(args.Name);
                foreach (Assembly asm in assemblies)
                {
                    if (IsAssemblyCompatible(requiredAssemblyName, asm.GetName()))
                        return asm;
                }
                if (requiredAssemblyName.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                    return null;
                foreach (var asm in assemblies)
                    if (asm == args.RequestingAssembly)
                    {
                        _log.Warn($"Couldn't find dependency! {args.RequestingAssembly} depends on {requiredAssemblyName}.");
                        break;
                    }
                return null;
            }

            AppDomain.CurrentDomain.AssemblyResolve += ResolveDependentAssembly;
            foreach (Assembly asm in assemblies)
            {
                TorchBase.RegisterAuxAssembly(asm);
            }
        }
        
        private static bool IsAssemblyCompatible(AssemblyName a, AssemblyName b)
        {
            return a.Name == b.Name && a.Version.Major == b.Version.Major && a.Version.Minor == b.Version.Minor;
        }
        
        private void InstantiatePlugin(PluginItem item, IEnumerable<Assembly> assemblies)
        {
            Type pluginType = null;
            bool mult = false;
            var manifest = item.Manifest;
            
            foreach (var asm in assemblies)
            {
                foreach (var type in asm.GetExportedTypes())
                {
                    if (!type.GetInterfaces().Contains(typeof(ITorchPlugin)))
                        continue;

                    if (type.IsAbstract)
                        continue;

                    _log.Info($"Loading plugin at {type.FullName}");

                    if (pluginType != null)
                    {
                        //_log.Error($"The plugin '{manifest.Name}' has multiple implementations of {nameof(ITorchPlugin)}, not loading.");
                        //return;
                        mult = true;
                        continue;
                    }

                    pluginType = type;
                }
            }

            if (mult)
            {
                _log.Error($"The plugin '{manifest.Name}' has multiple implementations of {nameof(ITorchPlugin)}, not loading.");
                item.State = PluginState.DisabledError;
                RegisterEmpty(item);
                return;
            }

            if (pluginType == null)
            {
                _log.Error($"The plugin '{manifest.Name}' does not have an implementation of {nameof(ITorchPlugin)}, not loading.");
                item.State = PluginState.DisabledError;
                RegisterEmpty(item);
                return;
            }

            // Backwards compatibility for PluginAttribute.
            var pluginAttr = pluginType.GetCustomAttribute<PluginAttribute>();
            if (pluginAttr != null)
            {
                _log.Warn($"Plugin '{manifest.Name}' is using the obsolete {nameof(PluginAttribute)}, using info from attribute if necessary.");
                manifest.Version = manifest.Version ?? pluginAttr.Version.ToString();
                manifest.Name = manifest.Name ?? pluginAttr.Name;
                if (manifest.Guid == default(Guid))
                    manifest.Guid = pluginAttr.Guid;
            }

            _log.Info($"Loading plugin '{manifest.Name}' ({manifest.Version})");

            TorchPluginBase plugin;
            try
            {
                plugin = (TorchPluginBase)Activator.CreateInstance(pluginType);
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"Plugin {manifest.Name} threw an exception during instantiation! Not loading!");
                item.State = PluginState.DisabledError;
                RegisterEmpty(item);
                return;
            }

            item.State = PluginState.Enabled;
            plugin.Manifest = manifest;
            plugin.State = PluginState.Enabled;
            plugin.StoragePath = Torch.Config.InstancePath;
            plugin.Torch = Torch;
            _plugins.Add(manifest.Guid, plugin);
        }

        private void RegisterEmpty(PluginItem item)
        {
            var plugin = new EmptyPlugin {Manifest = item.Manifest, State = item.State, Torch = Torch};
            _plugins.Add(item.Manifest.Guid, plugin);
        }
       
        private async Task<List<Guid>> ResolveDependencies(List<PluginItem> items, PluginItem item, bool downloadMissing)
        {
            var dependencies = new List<PluginItem>();
            var missingDependencies = new List<Guid>();
            
            foreach (var pluginDependency in item.Manifest.Dependencies)
            {
                //top-level search through plugins already on disk
                var dependency = items.FirstOrDefault(pi => pi?.Manifest.Guid == pluginDependency.Plugin);
                //search through already-resolved dependencies
                if (dependency == null)
                    dependency = RecurseDependencies(items, pluginDependency.Plugin);
                //either acquire the dependency or fail
                if (dependency == null)
                {
                    if (downloadMissing && !pluginDependency.Optional)
                    {
                        _log.Info($"Downloading dependency {pluginDependency.Plugin}");
                        (bool success, string path) = await PluginQuery.Instance.DownloadPlugin(pluginDependency.Plugin, Torch.Config.BetaPlugins);

                        if (success)
                        {
                            dependency = new PluginItem()
                                      {
                                          Path = path,
                                          Manifest = GetManifestFromDirectory(path)
                                      };

                            //resolve dependencies recursively
                            var missing = await ResolveDependencies(items, dependency, downloadMissing);
                            missingDependencies.AddList(missing);
                            dependencies.Add(dependency);
                        }
                        else
                        {
                            _log.Error($"Failed to download dependency {pluginDependency.Plugin}. Either Torch website is unavailable, or this dependency is not published on Torch website.");
                            missingDependencies.Add(pluginDependency.Plugin);
                            continue;
                        }
                    }
                    else if (pluginDependency.Optional)
                    {
                        missingDependencies.Add(pluginDependency.Plugin);
                        _log.Warn($"Dependency {pluginDependency.Plugin} is marked as optional, but is not installed. Some features of plugin {item.Manifest.Name} may not work correctly!");
                        continue;
                    }
                    else
                    {
                        _log.Warn($"Plugin {item.Manifest.Name} depends on plugin {pluginDependency.Plugin}, but it is not installed, and dependency downloading is disabled. Some features may not work correctly!");
                        missingDependencies.Add(pluginDependency.Plugin);
                        continue;
                    }
                }

                if (!string.IsNullOrEmpty(pluginDependency.MinVersion)
                    && dependency.Manifest.Version.TryExtractVersion(out var dependencyVersion)
                    && pluginDependency.MinVersion.TryExtractVersion(out var minVersion))
                {
                    // really only care about version if it is defined.
                    if (dependencyVersion < minVersion)
                    {
                        // If dependency version is too low, we can try to update. Otherwise
                        // it's a missing dependency.

                        //this should only hit if we aren't downloading deps anyway. Just leave the message here
                        _log.Warn($"{dependency.Manifest.Name} is below the requested version for {item.Manifest.Name}."
                        + Environment.NewLine
                        + $" Desired version: {pluginDependency.MinVersion}, Available version: {dependency.Manifest.Version}");
                        missingDependencies.Add(pluginDependency.Plugin);
                        continue;
                    }
                }

                dependencies.Add(dependency);
            }

            item.ResolvedDependencies = dependencies;
            return missingDependencies;
        }

        private PluginItem RecurseDependencies(List<PluginItem> items, Guid search)
        {
            foreach (var item in items)
            {
                if (item.ResolvedDependencies == null)
                    continue;

                foreach (var dep in item.ResolvedDependencies)
                {
                    if (dep.Manifest.Guid == search)
                        return dep;
                }

                var found = RecurseDependencies(item.ResolvedDependencies, search);
                if (found != null)
                    return found;
            }

            return null;
        }

        private List<PluginItem> DependencySort(List<PluginItem> source, bool silent = false)
        {
            var sorted = new List<PluginItem>(source.Count * 2);
            var visited = new HashSet<PluginItem>();

            foreach (var item in source)
                Visit(item);

            return sorted;

            void Visit(PluginItem item)
            {
                if (visited.Add(item))
                {
                    if (item.ResolvedDependencies != null)
                    {
                        foreach (var dep in item.ResolvedDependencies)
                            Visit(dep);
                    }

                    sorted.Add(item);
                }
                else
                {
                    if(!silent && !sorted.Contains(item))
                        throw new Exception($"Cyclic dependency detected! Failing plugin: {item.Manifest.Name} - {item.Manifest.Guid}");
                }
            }
        }



        private PluginManifest GetManifestFromDirectory(string directory)
        {
            var path = Path.Combine(directory, MANIFEST_NAME);
            var ret = !File.Exists(path) ? null : PluginManifest.Load(path);
            if (ret == null) 
                _log.Error($"NullManifest: {path}");
            return ret;
        }

        /// <inheritdoc cref="IEnumerable.GetEnumerator"/>
        public IEnumerator<ITorchPlugin> GetEnumerator()
        {
            return _plugins.Values.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        private class EmptyPlugin : TorchPluginBase { }
    }
}
