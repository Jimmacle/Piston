using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Havok;
using NLog;
using Sandbox;
using Sandbox.Engine.Networking;
using Sandbox.Engine.Utils;
using Sandbox.Game;
using Sandbox.Game.Gui;
using Torch.API;
using Torch.API.Managers;
using Torch.Collections;
using Torch.Managers;
using Torch.Mod;
using Torch.Server.ViewModels;
using Torch.Utils;
using VRage;
using VRage.FileSystem;
using VRage.Game;
using VRage.Game.ObjectBuilder;
using VRage.ObjectBuilders;
using VRage.Plugins;

namespace Torch.Server.Managers
{
    public class InstanceManager : Manager
    {
        private const string CONFIG_NAME = "SpaceEngineers-Dedicated.cfg";

        public event Action<ConfigDedicatedViewModel> InstanceLoaded;
        public ConfigDedicatedViewModel DedicatedConfig { get; set; }
        private static readonly Logger Log = LogManager.GetLogger(nameof(InstanceManager));
        [Dependency]
        private FilesystemManager _filesystemManager;

        public InstanceManager(ITorchBase torchInstance) : base(torchInstance)
        {
            
        }
        
        public void LoadInstance(string path, bool validate = true)
        {
            Log.Info($"Loading instance {path}");

            if (validate)
                ValidateInstance(path);

            MyFileSystem.Reset();
            MyFileSystem.Init("Content", path);
            //Initializes saves path. Why this isn't in Init() we may never know.
            MyFileSystem.InitUserSpecific(null);

            // why?....
            // var configPath = Path.Combine(path, CONFIG_NAME);
            // if (!File.Exists(configPath))
            // {
            //     Log.Error($"Failed to load dedicated config at {path}");
            //     return;
            // }

            
            // var config = new MyConfigDedicated<MyObjectBuilder_SessionSettings>(configPath);
            // config.Load(configPath);

            DedicatedConfig = new ConfigDedicatedViewModel((MyConfigDedicated<MyObjectBuilder_SessionSettings>) MySandboxGame.ConfigDedicated);

            var worldFolders = Directory.EnumerateDirectories(Path.Combine(Torch.Config.InstancePath, "Saves"));

            foreach (var f in worldFolders)
            {
                try
                {
                    if (!string.IsNullOrEmpty(f) && File.Exists(Path.Combine(f, "Sandbox.sbc")))
                        DedicatedConfig.Worlds.Add(new WorldViewModel(f));
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to load world at path: " + f);
                    continue;
                }
            }

            if (DedicatedConfig.Worlds.Count == 0)
            {
                Log.Warn($"No worlds found in the current instance {path}.");
                return;
            }

            SelectWorld(DedicatedConfig.LoadWorld ?? DedicatedConfig.Worlds.First().WorldPath, false);

            InstanceLoaded?.Invoke(DedicatedConfig);
        }

        public void SelectWorld(string worldPath, bool modsOnly = true)
        {
            DedicatedConfig.LoadWorld = worldPath;
            
            var worldInfo = DedicatedConfig.Worlds.FirstOrDefault(x => x.WorldPath == worldPath);
            try
            {
                if (worldInfo?.Checkpoint == null)
                {
                    worldInfo = new WorldViewModel(worldPath);
                    DedicatedConfig.Worlds.Add(worldInfo);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Failed to load world at path: " + worldPath);
                DedicatedConfig.LoadWorld = null;
                return;
            }

            DedicatedConfig.SelectedWorld = worldInfo;
            if (DedicatedConfig.SelectedWorld?.Checkpoint != null)
            {
                DedicatedConfig.Mods.Clear();
                //remove the Torch mod to avoid running multiple copies of it
                DedicatedConfig.SelectedWorld.WorldConfiguration.Mods.RemoveAll(m => m.PublishedFileId == TorchModCore.MOD_ID);
                foreach (var m in DedicatedConfig.SelectedWorld.WorldConfiguration.Mods)
                    DedicatedConfig.Mods.Add(new ModItemInfo(m));
                Task.Run(() => DedicatedConfig.UpdateAllModInfosAsync());
            }
        }

        public void SelectWorld(WorldViewModel world, bool modsOnly = true)
        {
            DedicatedConfig.LoadWorld = world.WorldPath;
            DedicatedConfig.SelectedWorld = world;
            if (DedicatedConfig.SelectedWorld?.Checkpoint != null)
            {
                DedicatedConfig.Mods.Clear();
                //remove the Torch mod to avoid running multiple copies of it
                DedicatedConfig.SelectedWorld.WorldConfiguration.Mods.RemoveAll(m => m.PublishedFileId == TorchModCore.MOD_ID);
                foreach (var m in DedicatedConfig.SelectedWorld.WorldConfiguration.Mods)
                    DedicatedConfig.Mods.Add(new ModItemInfo(m));
                Task.Run(() => DedicatedConfig.UpdateAllModInfosAsync());
            }
        }

        public void ImportSelectedWorldConfig()
        {
            ImportWorldConfig(DedicatedConfig.SelectedWorld, false);
        }

        private void ImportWorldConfig(WorldViewModel world, bool modsOnly = true)
        {
            var mods = new MtObservableList<ModItemInfo>();
            foreach (var mod in world.WorldConfiguration.Mods)
                mods.Add(new ModItemInfo(mod));
            DedicatedConfig.Mods = mods;


            Log.Debug("Loaded mod list from world");

            if (!modsOnly)
                DedicatedConfig.SessionSettings = world.WorldConfiguration.Settings;
        }

        private void ImportWorldConfig(bool modsOnly = true)
        {
            if (string.IsNullOrEmpty(DedicatedConfig.LoadWorld))
                return;

            var sandboxPath = Path.Combine(DedicatedConfig.LoadWorld, "Sandbox.sbc");

            if (!File.Exists(sandboxPath))
                return;

            try
            {
                MyObjectBuilderSerializer.DeserializeXML(sandboxPath, out MyObjectBuilder_Checkpoint checkpoint, out ulong sizeInBytes);
                if (checkpoint == null)
                {
                    Log.Error($"Failed to load {DedicatedConfig.LoadWorld}, checkpoint null ({sizeInBytes} bytes, instance {Torch.Config.InstancePath})");
                    return;
                }

                var mods = new MtObservableList<ModItemInfo>();
                foreach (var mod in checkpoint.Mods)
                    mods.Add(new ModItemInfo(mod));
                DedicatedConfig.Mods = mods;

                Log.Debug("Loaded mod list from world");

                if (!modsOnly)
                    DedicatedConfig.SessionSettings = new SessionSettingsViewModel(checkpoint.Settings);
            }
            catch (Exception e)
            {
                Log.Error($"Error loading mod list from world, verify that your mod list is accurate. '{DedicatedConfig.LoadWorld}'.");
                Log.Error(e);
            }
        }

        public void SaveConfig()
        {
            if (((TorchServer)Torch).HasRun)
            {
                Log.Warn("Checkpoint cache is stale, not saving dedicated config.");
                return;
            }
            
            DedicatedConfig.Save(Path.Combine(Torch.Config.InstancePath, CONFIG_NAME));
            Log.Info("Saved dedicated config.");

            try
            {
                var world = DedicatedConfig.Worlds.FirstOrDefault(x => x.WorldPath == DedicatedConfig.LoadWorld) ?? new WorldViewModel(DedicatedConfig.LoadWorld);

                world.Checkpoint.SessionName = DedicatedConfig.WorldName;
                world.WorldConfiguration.Settings = DedicatedConfig.SessionSettings;
                world.WorldConfiguration.Mods.Clear();

                foreach (var mod in DedicatedConfig.Mods)
                {
                    var savedMod = ModItemUtils.Create(mod.PublishedFileId);
                    savedMod.IsDependency = mod.IsDependency;
                    savedMod.Name = mod.Name;
                    savedMod.FriendlyName = mod.FriendlyName;
                    
                    world.WorldConfiguration.Mods.Add(savedMod);
                }
                Task.Run(() => DedicatedConfig.UpdateAllModInfosAsync());

                world.SaveSandbox();

                Log.Info("Saved world config.");
            }
            catch (Exception e)
            {
                Log.Error("Failed to write sandbox config, changes will not appear on server");
                Log.Error(e);
            }
        }

        /// <summary>
        /// Ensures that the given path is a valid server instance.
        /// </summary>
        private void ValidateInstance(string path)
        {
            Directory.CreateDirectory(Path.Combine(path, "Saves"));
            Directory.CreateDirectory(Path.Combine(path, "Mods"));
            var configPath = Path.Combine(path, CONFIG_NAME);
            if (File.Exists(configPath))
                return;

            var config = new MyConfigDedicated<MyObjectBuilder_SessionSettings>(configPath);
            config.Save(configPath);
        }
    }

    public class WorldViewModel : ViewModel
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public string FolderName { get; set; }
        public string WorldPath { get; }
        public long WorldSizeKB { get; }
        private string _checkpointPath;
        private string _worldConfigPath;
        public CheckpointViewModel Checkpoint { get; private set; }
        
        public WorldConfigurationViewModel WorldConfiguration { get; private set; }

        public WorldViewModel(string worldPath)
        {
            try
            {
                WorldPath = worldPath;
                WorldSizeKB = new DirectoryInfo(worldPath).GetFiles().Sum(x => x.Length) / 1024;
                _checkpointPath = Path.Combine(WorldPath, "Sandbox.sbc");
                _worldConfigPath = Path.Combine(WorldPath, "Sandbox_config.sbc");
                FolderName = Path.GetFileName(worldPath);
                LoadSandbox();
            }
            catch (ArgumentException ex)
            {
                Log.Error($"World view model failed to load the path: {worldPath} Please ensure this is a valid path.");
                throw; //rethrow to be handled further up the stack
            }
        }

        public void SaveSandbox()
        {
            using (var f = File.Open(_checkpointPath, FileMode.Create))
                MyObjectBuilderSerializer.SerializeXML(f, Checkpoint);

            using (var f = File.Open(_worldConfigPath, FileMode.Create))
                MyObjectBuilderSerializer.SerializeXML(f, WorldConfiguration);
        }

        private void LoadSandbox()
        {
            MyObjectBuilderSerializer.DeserializeXML(_checkpointPath, out MyObjectBuilder_Checkpoint checkpoint);
            Checkpoint = new CheckpointViewModel(checkpoint);
            
            // migrate old saves
            if (File.Exists(_worldConfigPath))
            {
                MyObjectBuilderSerializer.DeserializeXML(_worldConfigPath, out MyObjectBuilder_WorldConfiguration worldConfig);
                WorldConfiguration = new WorldConfigurationViewModel(worldConfig);
            }
            else
            {
                WorldConfiguration = new WorldConfigurationViewModel(new MyObjectBuilder_WorldConfiguration
                {
                    Mods = checkpoint.Mods,
                    Settings = checkpoint.Settings
                });

                checkpoint.Mods = null;
                checkpoint.Settings = null;
            }
            
            OnPropertyChanged(nameof(Checkpoint));
            OnPropertyChanged(nameof(WorldConfiguration));
        }
    }
}
