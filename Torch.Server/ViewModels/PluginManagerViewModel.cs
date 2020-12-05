using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using NLog;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.Collections;
using Torch.Managers;
using Torch.Server.Views.Commands;

namespace Torch.Server.ViewModels
{
    public class PluginManagerViewModel : ViewModel
    {
        public static readonly Logger _log = LogManager.GetCurrentClassLogger();
        public MtObservableList<PluginViewModel> Plugins { get; } = new MtObservableList<PluginViewModel>();
        private Dispatcher _dispatcher;
        private IPluginManager _pluginManager;
        private PluginViewModel _selectedPlugin;
        public PluginViewModel SelectedPlugin
        {
            get => _selectedPlugin;
            set
            {
                _selectedPlugin = value;
                try
                {
                    OnPropertyChanged(nameof(SelectedPlugin));
                }
                catch (Exception ex)
                {
                    _log.Error(ex);
                }

                PluginCommand.RaiseGlobalEnable();
            }
        }

        public PluginManagerViewModel() { }

        public PluginManagerViewModel(IPluginManager pluginManager)
        {
            _pluginManager = pluginManager;
            foreach (var plugin in pluginManager)
                Plugins.Add(new PluginViewModel(plugin));
            pluginManager.PluginsLoaded += PluginManager_PluginsLoaded;
            _dispatcher = Dispatcher.CurrentDispatcher;
        }

        private void PluginManager_PluginsLoaded(IReadOnlyCollection<ITorchPlugin> obj)
        {
            //_log.Error($"Updating plugin list {Plugins.Count}");

            Plugins.Clear();
            _dispatcher.Invoke(() =>
                               {
                                   foreach (var plugin in obj)
                                   {
                                       try
                                       {
                                           Plugins.Add(new PluginViewModel(plugin));
                                       }
                                       catch (Exception ex)
                                       {
                                           _log.Fatal(ex);
                                       }
                                   }

                                   //_log.Error(Plugins.Count);
                               });


            //OnPropertyChanged(nameof(Plugins));
        }

        public ICommand EnableItem => new EnablePluginCommand();

        public ICommand DisableItem => new DisablePluginCommand();

        public ICommand UninstallItem => new UninstallPluginCommand();
    }
}
