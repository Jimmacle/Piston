using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;

namespace Torch.API.WebAPI
{
    public class PluginQuery
    {
        private const string ALL_QUERY = "https://torchapi.net/api/plugins";
        private const string PLUGIN_QUERY = "https://torchapi.net/api/plugins/{0}";
        private readonly HttpClient _client;
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private static PluginQuery _instance;
        public static PluginQuery Instance => _instance ?? (_instance = new PluginQuery());

        private static string _pluginPath;

        private PluginQuery()
        {
            _client = new HttpClient();
        }

        public static void SetPluginPath(string path)
        {
            _pluginPath = path;
        }

        public async Task<PluginResponse> QueryAll()
        {
            PluginResponse response;
            string res;
            try
            {
                var h = await _client.GetAsync(ALL_QUERY);
                if (!h.IsSuccessStatusCode)
                {
                    Log.Error($"Plugin query returned response {h.StatusCode}");
                    return null;
                }

                res = await h.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to download plugin data!");
                return null;
            }

            try
            {
                response = JsonConvert.DeserializeObject<PluginResponse>(res);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to deserialize plugin query response!");
                return null;
            }
            return response;
        }

        public async Task<PluginFullItem> QueryOne(Guid guid)
        {
            return await QueryOne(guid.ToString());
        }

        public async Task<PluginFullItem> QueryOne(string guid)
        {
            PluginFullItem response;
            string res;
            try
            {
                var h = await _client.GetAsync(string.Format(PLUGIN_QUERY, guid));
                if (!h.IsSuccessStatusCode)
                {
                    Log.Error($"Plugin query returned response {h.StatusCode}");
                    return null;
                }

                res = await h.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to download plugin data!");
                return null;
            }

            try
            {
                response = JsonConvert.DeserializeObject<PluginFullItem>(res);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to deserialize plugin query response!");
                return null;
            }
            return response;
        }

        public async Task<(bool, string)> DownloadPlugin(Guid guid, bool beta = false, string path = null)
        {
            return await DownloadPlugin(guid.ToString(), beta, path);
        }

        public async Task<(bool, string)> DownloadPlugin(string guid, bool beta = false, string path = null)
        {
            var item = await QueryOne(guid);
            return await DownloadPlugin(item, beta, path);
        }

        public async Task<(bool, string)> DownloadPlugin(PluginFullItem item, bool beta = false, string path = null)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                    path = Path.Combine(_pluginPath, $"{item.Name} - {item.ID}");
                
                var h = await _client.GetAsync(string.Format(PLUGIN_QUERY, item.ID));
                string res = await h.Content.ReadAsStringAsync();
                var response = JsonConvert.DeserializeObject<PluginFullItem>(res);
                if (response.Versions.Length == 0)
                {
                    Log.Error($"Selected plugin {item.Name} does not have any versions to download!");
                    return (false, path);
                }

                //find latest version, skipping betas unless the user wants them
                var version = response.Versions.FirstOrDefault(v => !v.IsBeta || beta);
                if (version == null)
                {
                    Log.Error($"Could not find latest version for selected plugin {item.Name}");
                    return (false, path);
                }
                var s = await _client.GetStreamAsync(version.URL);

                Directory.CreateDirectory(path);
                using (var gz = new ZipArchive(s, ZipArchiveMode.Read))
                {
                    foreach (var entry in gz.Entries)
                    {
                        string filePath = Path.Combine(path, entry.FullName);
                        Log.Debug(filePath);
                        File.Delete(filePath);
                        using (var es = entry.Open())
                        using (var fs = File.Create(filePath))
                            await es.CopyToAsync(fs);
                            
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to download plugin!");
            }

            return (true, path);
        }

    }

    public class PluginResponse
    {
        public PluginItem[] Plugins;
        public int Count;
    }

    public class PluginItem
    {
        public string ID;
        public string Name;
        public string Author;
        public string Description;
        public string LatestVersion;

        public override string ToString()
        {
            return Name;
        }
    }

    public class PluginFullItem : PluginItem
    {
        public VersionItem[] Versions;
    }

    public class VersionItem
    {
        public string Version;
        public string Note;
        public bool IsBeta;
        public string URL;
    }
}
