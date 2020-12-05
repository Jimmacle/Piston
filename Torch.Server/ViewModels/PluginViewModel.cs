using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using NLog;
using Torch.API;
using Torch.API.Plugins;
using Torch.Server.Views;
using Torch.Server.Views.Commands;

namespace Torch.Server.ViewModels
{
    public class PluginViewModel : ViewModel
    {
        public UserControl Control { get; }

        public string Name
        {
            get
            {
                if (State != PluginState.Enabled)
                    return $"* {_name}";
                return _name;
            }
            private set { _name = value; }
        }

        public ITorchPlugin Plugin { get; }
        public PluginState State => Plugin.State;

        private static Logger _log = LogManager.GetCurrentClassLogger();
        private string _name;

        public PluginViewModel(ITorchPlugin plugin)
        {
            Plugin = plugin;

            if (Plugin is IWpfPlugin p)
            {
                try
                {
                    Control = p.GetControl();
                }
                catch (Exception ex)
                {
                    _log.Error(ex, $"Exception loading interface for plugin {Plugin.Name}! Plugin interface will not be available!");
                    Control = null;
                }
            }
            
            Name = $"{plugin.Name} ({plugin.Version})";

            ThemeControl.UpdateDynamicControls += UpdateResourceDict;
            UpdateResourceDict(ThemeControl.currentTheme);

            ((TorchPluginBase)plugin).StateChanged += PluginViewModel_StateChanged;
        }

        private void PluginViewModel_StateChanged(PluginState arg1, PluginState arg2)
        {
            OnPropertyChanged(nameof(TorchPluginBase.State));
            OnPropertyChanged(nameof(Color));
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(ToolTip));
            OnPropertyChanged(nameof(Name));
            PluginCommand.RaiseGlobalEnable();
        }

        public void UpdateResourceDict(ResourceDictionary dictionary)
        {
            if (this.Control == null)
                return;

            this.Control.Resources.MergedDictionaries.Clear();
            this.Control.Resources.MergedDictionaries.Add(dictionary);
        }

        public string Color => BColor.ToString();

        public Brush BColor
        {
            get {
                //_log.Warn($"{Name} C: {State}");
                switch (Plugin.State)
                {
                    case PluginState.NotInitialized:
                    case PluginState.MissingDependency:
                    case PluginState.DisabledError:
                        return Brushes.Red;
                    case PluginState.UpdateRequired:
                        return Brushes.DodgerBlue;
                    case PluginState.UninstallRequested:
                        return Brushes.Gold;
                    case PluginState.NotInstalled:
                    case PluginState.DisableRequested:
                    case PluginState.DisabledUser:
                    case PluginState.EnableRequested:
                        return Brushes.Gray;
                    case PluginState.Enabled:
                        return Brushes.Transparent;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        public string ToolTip
        {
            get {
                //_log.Warn($"{Name} TT: {State}");
                switch (Plugin.State)
                {
                    case PluginState.NotInitialized:
                        return "Error during load.";
                    case PluginState.DisabledError:
                        return "Disabled due to error on load.";
                    case PluginState.DisabledUser:
                        return "Disabled.";
                    case PluginState.UpdateRequired:
                        return "Update required.";
                    case PluginState.UninstallRequested:
                        return "Marked for uninstall.";
                    case PluginState.NotInstalled:
                        return "Not installed. Click 'Enable'";
                    case PluginState.Enabled:
                        return Name;
                    case PluginState.MissingDependency:
                        return "Dependency missing. Check the log.";
                    case PluginState.DisableRequested:
                        return "Will be disabled on restart.";
                    case PluginState.EnableRequested:
                        return "Will be enabled on restart";
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }
    }
}
