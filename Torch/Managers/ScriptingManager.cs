﻿//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;
//using Sandbox.ModAPI;
//using Torch.API;
//using Torch.API.Managers;
//using Torch.API.ModAPI;
//using Torch.API.ModAPI.Ingame;
//using VRage.Scripting;
//
//namespace Torch.Managers
//{
//    public class ScriptingManager : IManager
//    {
//        private MyScriptWhitelist _whitelist;
//
//        public void Attach()
//        {
//            _whitelist = MyScriptCompiler.Static.Whitelist;
//            MyScriptCompiler.Static.AddConditionalCompilationSymbols("TORCH");
//            MyScriptCompiler.Static.AddReferencedAssemblies(typeof(ITorchBase).Assembly.Location);
//            MyScriptCompiler.Static.AddImplicitIngameNamespacesFromTypes(typeof(GridExtensions));
//
//            using (var whitelist = _whitelist.OpenBatch())
//            {
//                whitelist.AllowNamespaceOfTypes(MyWhitelistTarget.ModApi, typeof(TorchAPI));
//                whitelist.AllowNamespaceOfTypes(MyWhitelistTarget.Both, typeof(GridExtensions));
//            }
//
//            /*
//            //dump whitelist
//            var whitelist = new StringBuilder();
//            foreach (var pair in MyScriptCompiler.Static.Whitelist.GetWhitelist())
//            {
//                var split = pair.Key.Split(',');
//                whitelist.AppendLine("|-");
//                whitelist.AppendLine($"|{pair.Value} || {split[0]} || {split[1]}");
//            }
//            Log.Info(whitelist);*/
//        }
//
//        public void Detach()
//        {
//            // TODO unregister whitelist patches
//        }
//
//        public void UnwhitelistType(Type t)
//        {
//            throw new NotImplementedException();
//        }
//    }
//}
//