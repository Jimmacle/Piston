using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Torch.Server.Views
{
    public static class Extensions
    {
        public static readonly DependencyProperty ScrollContainerProperty = DependencyProperty.RegisterAttached("ScrollContainer", typeof(bool), typeof(Extensions), new PropertyMetadata(true));

        public static bool GetScrollContainer(this UIElement ui)
        {
            var val = ui?.GetValue(ScrollContainerProperty);
            if (val == null)
                return true;
            return (bool)val;
        }

        public static void SetScrollContainer(this UIElement ui, bool value)
        {
            ui.SetValue(ScrollContainerProperty, value);
        }
    }
}
