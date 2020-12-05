using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using NLog;
using Torch.API.Plugins;
using Torch.Server.ViewModels;

namespace Torch.Server.Views.Commands
{
    public abstract class PluginCommand : ICommand
    {
        public abstract void Execute(object arg);
        public event EventHandler CanExecuteChanged;

        public static event Action GlobalChange;
        public abstract bool CanExecute(object arg);

        public static void RaiseGlobalEnable()
        {
            //Log.Info("Global enable hit");
            GlobalChange?.Invoke();
        }

        protected PluginCommand()
        {
            GlobalChange += RaiseChanged;
            //Log.Info($"ctor hit: {this.GetType().Name}");
        }

        protected static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public bool IsEnabled(object obj, params PluginState[] undesiredStates)
        {
            var plugin = obj as PluginViewModel;
            if (plugin == null)
            {
                return true;
            }

            var val = !undesiredStates.Contains(plugin.State);
            return val;
        }

        private void RaiseChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public class EnablePluginCommand : PluginCommand
    {
        public override bool CanExecute(object arg)
        {
            return IsEnabled(arg, PluginState.Enabled, PluginState.EnableRequested);
        }

        public override void Execute(object arg)
        {
            var plugin = arg as PluginViewModel;
            if (plugin == null)
            {
                //Log.Error($"Null??? {arg?.GetType().Name ?? "???????"}");
                return;
            }
            ((TorchPluginBase)plugin.Plugin).State = PluginState.EnableRequested;
        }
    }

    public class DisablePluginCommand : PluginCommand
    {
        public override bool CanExecute(object arg)
        {
            return IsEnabled(arg, PluginState.DisabledUser, PluginState.DisabledError, PluginState.DisableRequested);
        }

        public override void Execute(object arg)
        {
            var plugin = arg as PluginViewModel;
            if (plugin == null)
            {
                //Log.Error($"Null??? {arg?.GetType().Name ?? "???????"}");
                return;
            }
            ((TorchPluginBase)plugin.Plugin).State = PluginState.DisableRequested;
        }
    }

    public class UninstallPluginCommand : PluginCommand
    {
        public override bool CanExecute(object arg)
        {
            return IsEnabled(arg, PluginState.UninstallRequested);
        }

        public override void Execute(object arg)
        {
            var plugin = arg as PluginViewModel;
            if (plugin == null)
            {
                //Log.Error($"Null??? {arg?.GetType().Name ?? "???????"}");
                return;
            }
            ((TorchPluginBase)plugin.Plugin).State = PluginState.UninstallRequested;
            
        }
    }
}
