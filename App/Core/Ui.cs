// Copyright (c) Mouse Without Borders contributors. Licensed under the MIT license.
using System.Globalization;
using System.Resources;
using System.Windows.Forms;

namespace MouseWithoutBorders.Core;

internal static class Ui
{
    private static readonly ResourceManager Resources = new("MouseWithoutBorders.Properties.UiStrings", typeof(Ui).Assembly);
    internal static string Text(string english) => Resources.GetString(english, CultureInfo.CurrentUICulture) ?? english;
    internal static string Format(string english, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Text(english), arguments);

    internal static void Apply(Control control, ToolTip tips = null)
    {
        // Editable values and selection tokens are data, not presentation strings.
        if (control is Label or ButtonBase or GroupBox or TabPage) control.Text = Text(control.Text);
        if (tips != null) tips.SetToolTip(control, Text(tips.GetToolTip(control)));
        foreach (Control child in control.Controls) Apply(child, tips);
    }
}
