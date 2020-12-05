using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace Torch.API.Plugins
{
    public interface ITorchPlugin : IDisposable
    {
        /// <summary>
        /// A unique ID for the plugin.
        /// </summary>
        Guid Id { get; }

        /// <summary>
        /// The version of the plugin.
        /// </summary>
        string Version { get; }

        /// <summary>
        /// The name of the plugin.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// This is called before the game loop is started.
        /// </summary>
        /// <param name="torchBase">Torch instance</param>
        void Init(ITorchBase torchBase);

        /// <summary>
        /// This is called on the game thread after each tick.
        /// </summary>
        void Update();

        /// <summary>
        /// Plugin's enabled state. Mainly for UI niceness
        /// </summary>
        PluginState State { get; }
    }

    public enum PluginState
    {
         NotInitialized,
         DisabledError,
         DisabledUser,
         DisableRequested,
         EnableRequested,
         UpdateRequired,
         UninstallRequested,
         NotInstalled,
         MissingDependency,
         Enabled
    }
}
