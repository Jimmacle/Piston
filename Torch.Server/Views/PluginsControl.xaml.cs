using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Torch.API;
using Torch.API.Managers;
using Torch.Managers;
using Torch.Server.ViewModels;
using Torch.Views;

namespace Torch.Server.Views
{
    /// <summary>
    /// Interaction logic for PluginsControl.xaml
    /// </summary>
    public partial class PluginsControl : UserControl
    {
        private ITorchServer _server;
        private PluginManager _plugins;

        public PluginsControl()
        {
            InitializeComponent();
        }

        private void PluginManagerOnPropertyChanged(object sender, PropertyChangedEventArgs propertyChangedEventArgs)
        {
            if (propertyChangedEventArgs.PropertyName == nameof(PluginManagerViewModel.SelectedPlugin))
            {
                var plugin = ((PluginManagerViewModel)DataContext).SelectedPlugin;
               
                if (plugin.Control is PropertyGrid || !plugin.Control.GetScrollContainer())
                    PScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                else
                    PScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
        }
        
        public void BindServer(ITorchServer server)
        {
            _server = server;
            _server.Initialized += Server_Initialized;
        }

        private void Server_Initialized(ITorchServer obj)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _plugins = _server.Managers.GetManager<PluginManager>();
                var pluginManager = new PluginManagerViewModel(_plugins);
                DataContext = pluginManager;
                pluginManager.PropertyChanged += PluginManagerOnPropertyChanged;
            });

        }

        private void OpenFolder_OnClick(object sender, RoutedEventArgs e)
        {
            if (_plugins?.PluginDir != null)
                Process.Start(_plugins.PluginDir);
        }

        private void BrowsPlugins_OnClick(object sender, RoutedEventArgs e)
        {
            var browser = new PluginBrowser();
            browser.Show();
        }

       
    }
}
