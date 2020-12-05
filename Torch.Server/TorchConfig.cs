using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Xml.Serialization;
using Newtonsoft.Json;
using NLog;
using VRage.Game;

namespace Torch.Server
{
    // TODO: redesign this gerbage
    public class TorchConfig : CommandLine, ITorchConfig
    {
        private static Logger _log = LogManager.GetLogger("Config");

        public bool ShouldUpdatePlugins => (GetPluginUpdates && !NoUpdate) || ForceUpdate;
        public bool ShouldUpdateTorch => (GetTorchUpdates && !NoUpdate) || ForceUpdate;

        /// <inheritdoc />
        [Arg("instancename", "The name of the Torch instance.")]
        public string InstanceName { get; set; }


        private string _instancePath;

        /// <inheritdoc />
        [Arg("instancepath", "Server data folder where saves and mods are stored.")]
        public string InstancePath
        {
            get => _instancePath;
            set
            {
                if(String.IsNullOrEmpty(value))
                {
                    _instancePath = value;
                    return;
                }
                try
                {
                    if(value.Contains("\""))
                        throw new InvalidOperationException();

                    var s = Path.GetFullPath(value);
                    Console.WriteLine(s); //prevent compiler opitmization - just in case
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Invalid path assigned to InstancePath! Please report this immediately! Value: " + value);
                    //throw;
                }

                _instancePath = value;
            }
        }

        /// <inheritdoc />
        [XmlIgnore, Arg("noupdate", "Disable automatically downloading game and plugin updates.")]
        public bool NoUpdate { get; set; }

        /// <inheritdoc />
        [XmlIgnore, Arg("forceupdate", "Manually check for and install updates.")]
        public bool ForceUpdate { get; set; }

        /// <summary>
        /// Permanent flag to ALWAYS automatically start the server
        /// </summary>
        public bool Autostart { get; set; }

        /// <summary>
        /// Temporary flag to automatically start the server only on the next run
        /// </summary>
        [Arg("autostart", "Start the server immediately.")]
        [XmlIgnore]
        public bool TempAutostart { get; set; }

        /// <inheritdoc />
        [Arg("restartoncrash", "Automatically restart the server if it crashes.")]
        public bool RestartOnCrash { get; set; }

        /// <inheritdoc />
        [Arg("nogui", "Do not show the Torch UI.")]
        public bool NoGui { get; set; }

        /// <inheritdoc />
        [XmlIgnore, Arg("waitforpid", "Makes Torch wait for another process to exit.")]
        public string WaitForPID { get; set; }

        /// <inheritdoc />
        public bool GetTorchUpdates { get; set; } = true;

        /// <inheritdoc />
        public bool GetPluginUpdates { get; set; } = true;

        /// <inheritdoc />
        public int TickTimeout { get; set; } = 60;

        /// <inheritdoc />
        [Arg("plugins", "Starts Torch with the given plugin GUIDs (space delimited).")]
        public List<Guid> Plugins { get; set; } = new List<Guid>();

        public List<Guid> DisabledPlugins { get; set; } = new List<Guid>();

        [Arg("localplugins", "Loads all plugins from disk, ignores the plugins defined in config.")]
        public bool LocalPlugins { get; set; }
        
        public bool DownloadDependencies { get; set; } = true;

        public bool BetaPlugins { get; set; }

        [Arg("disconnect", "When server restarts, all clients are rejected to main menu to prevent auto rejoin")]
        public bool DisconnectOnRestart { get; set; }

        public string ChatName { get; set; } = "Server";

        public string ChatColor { get; set; } = "Red";

        public bool EnableWhitelist { get; set; } = false;
        public HashSet<ulong> Whitelist { get; set; } = new HashSet<ulong>();

        public Point WindowSize { get; set; } = new Point(800, 600);
        public Point WindowPosition { get; set; } = new Point();

        public string LastUsedTheme { get; set; } = "Torch Theme";

        //Prevent reserved players being written to disk, but allow it to be read
        //remove this when ReservedPlayers is removed
        private bool ShouldSerializeReservedPlayers() => false;

        [Arg("console", "Keeps a separate console window open after the main UI loads.")]
        public bool IndependentConsole { get; set; } = false;

        [XmlIgnore]
        [Arg("testplugin", "Path to a plugin to debug. For development use only.")]
        public string TestPlugin { get; set; }

        [Arg("asserts", "Enable Keen's assert logging.")]
        public bool EnableAsserts { get; set; } = false;

        [XmlIgnore]
        private string _path;

        public TorchConfig() : this("Torch") { }

        public TorchConfig(string instanceName = "Torch", string instancePath = null)
        {
            InstanceName = instanceName;
            InstancePath = instancePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpaceEngineersDedicated");
        }

        public static TorchConfig LoadFrom(string path)
        {
            try
            {
                var ser = new XmlSerializer(typeof(TorchConfig));
                using (var f = File.OpenRead(path))
                {
                    var config = (TorchConfig)ser.Deserialize(f);
                    config._path = path;
                    return config;
                }
            }
            catch (Exception e)
            {
                _log.Error(e);
                return null;
            }
        }

        public bool Save(string path = null)
        {
            if (path == null)
                path = _path;
            else
                _path = path;

            try
            {
                var ser = new XmlSerializer(typeof(TorchConfig));
                using (var f = File.Create(path))
                    ser.Serialize(f, this);
                return true;
            }
            catch (Exception e)
            {
                _log.Error(e);
                return false;
            }
        }
    }
}
