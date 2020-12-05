using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Newtonsoft.Json;
using NLog;
using Torch.API.Managers;
using Torch.API.WebAPI;
using Torch.Collections;
using Torch.Managers;
using Torch.Server.Annotations;
using Path = System.IO.Path;

namespace Torch.Server.Views
{
    /// <summary>
    /// Interaction logic for PluginBrowser.xaml
    /// </summary>
    public partial class PluginBrowser : Window, INotifyPropertyChanged
    {
        private static Logger Log = LogManager.GetCurrentClassLogger();

        public MtObservableList<PluginItem> Plugins { get; set; } = new MtObservableList<PluginItem>();
        public PluginItem CurrentItem { get; set; }

        private string _description = "Loading data from server, please wait..";
        public string CurrentDescription
        {
            get { return _description; }
            set
            {
                _description = value;
                OnPropertyChanged();
            }
        }

        public PluginBrowser()
        {
            InitializeComponent();

            Task.Run(async () =>
                     {
                         var res = await PluginQuery.Instance.QueryAll();
                         if (res == null)
                             return;
                         foreach (var item in res.Plugins)
                             Plugins.Add(item);
                         PluginsList.Dispatcher.Invoke(() => PluginsList.SelectedIndex = 0);
                     });

            MarkdownFlow.CommandBindings.Add(new CommandBinding(NavigationCommands.GoToPage, (sender, e) => OpenUri((string)e.Parameter)));
        }

        public static bool IsValidUri(string uri)
        {
            if (!Uri.IsWellFormedUriString(uri, UriKind.Absolute))
                return false;
            Uri tmp;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out tmp))
                return false;
            return tmp.Scheme == Uri.UriSchemeHttp || tmp.Scheme == Uri.UriSchemeHttps;
        }

        public static bool OpenUri(string uri)
        {
            if (!IsValidUri(uri))
                return false;
            System.Diagnostics.Process.Start(uri);
            return true;
        }

        private void PluginsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CurrentItem = (PluginItem)PluginsList.SelectedItem;
            CurrentDescription = CurrentItem.Description;
            DownloadButton.IsEnabled = !string.IsNullOrEmpty(CurrentItem.LatestVersion);
        }

        private void DownloadButton_OnClick(object sender, RoutedEventArgs e)
        {
            var item = CurrentItem;
            TorchBase.Instance.Config.Plugins.Add(new Guid(item.ID));
            TorchBase.Instance.Config.Save();
            Task.Run(async () =>
                     {
                         var result = await PluginQuery.Instance.DownloadPlugin(item.ID);
                         MessageBox.Show(result.Item1 ? "Plugin downloaded successfully! Please restart the server to load changes."
                                                : "Plugin failed to download! See log for details.", 
                                                "Plugin Downloader",
                                                MessageBoxButton.OK);
                         var man = TorchBase.Instance.Managers.GetManager<PluginManager>();
                         man?.InsertDummy(item.Name, new Guid(item.ID));
                     });
        }

        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

}
