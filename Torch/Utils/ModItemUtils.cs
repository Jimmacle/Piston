using System.Collections.Generic;
using System.Threading.Tasks;
using Sandbox.Engine.Networking;
using Torch.API;
using VRage.Game;

namespace Torch.Utils
{
    public static class ModItemUtils
    {
        public static MyObjectBuilder_Checkpoint.ModItem Create(ulong modId, string serviceType = null)
        {
            return new MyObjectBuilder_Checkpoint.ModItem(modId, serviceType ?? GetDefaultServiceName());
        }

        public static MyObjectBuilder_Checkpoint.ModItem Create(string str)
        {
            var arr = str.Split('-');
            return new MyObjectBuilder_Checkpoint.ModItem(ulong.Parse(arr[0]), arr[1]);
        }

        //because KEEEN! 
        public static string GetDefaultServiceName()
        {
            try
            {
                return MyGameService.GetDefaultUGC().ServiceName;
            }
            catch
            {
                return TorchBase.Instance.Config.UgcServiceType.ToString();
            }
        }
    }
}