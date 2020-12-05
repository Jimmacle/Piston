using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ProtoBuf;
using Sandbox.ModAPI;

namespace Torch.Mod.Messages
{
    // Dialogs are structured as follows
    // 
    // _____________________________________
    // |            Title                   |
    // --------------------------------------
    // |          Prefix Subtitle           |
    // --------------------------------------
    // |  ________________________________  |
    // |  |         Content               | |
    // |  --------------------------------- |
    // |            ____________            |
    // |           | ButtonText |           |
    // |           --------------           |
    // --------------------------------------
    // 
    // Button has a callback on click option, 
    // but can't serialize that, so ¯\_(ツ)_/¯
    [ProtoContract]
    public class DialogMessage : MessageBase
    {
        [ProtoMember(201)]
        public string Title;
        [ProtoMember(202)]
        public string Subtitle;
        [ProtoMember(203)]
        public string Prefix;
        [ProtoMember(204)]
        public string Content;
        [ProtoMember(205)]
        public string ButtonText;

        public DialogMessage()
        { }

        public DialogMessage(string title, string subtitle, string content)
        {
            Title = title;
            Subtitle = subtitle;
            Content = content;
            Prefix = String.Empty;
        }

        public DialogMessage(string title = null, string prefix = null, string subtitle = null, string content = null, string buttonText = null)
        {
            Title = title;
            Subtitle = subtitle;
            Prefix = prefix ?? String.Empty;
            Content = content;
            ButtonText = buttonText;
        }

        public override void ProcessClient()
        {
            MyAPIGateway.Utilities.ShowMissionScreen(Title, Prefix, Subtitle, Content, null, ButtonText);
        }

        public override void ProcessServer()
        {
            throw new Exception();
        }
    }
}
