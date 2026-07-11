using System.Windows;
using System.Windows.Controls;

namespace ScreenClipTool.UI;

/// <summary>Petites boîtes de dialogue construites en code, au thème de l'app.</summary>
public static class Dialogs
{
    /// <summary>Saisie d'une ligne de texte ; null si annulé.</summary>
    public static string? Prompt(Window owner, string title, string label, string initial)
    {
        var window = MakeShell(owner, title, 460);
        string? result = null;

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8) });
        var input = new TextBox { Text = initial };
        panel.Children.Add(input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 84 };
        ok.Click += (_, _) => { result = input.Text.Trim(); window.DialogResult = true; };
        var cancel = new Button { Content = "Annuler", IsCancel = true, MinWidth = 84, Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        window.Content = panel;
        input.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true && !string.IsNullOrWhiteSpace(result) ? result : null;
    }

    /// <summary>Affichage d'informations (texte multi-lignes sélectionnable).</summary>
    public static void Info(Window owner, string title, string text)
    {
        var window = MakeShell(owner, title, 520);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBox
        {
            Text = text,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            TextWrapping = TextWrapping.Wrap,
        });
        var close = new Button
        {
            Content = "Fermer",
            IsCancel = true,
            IsDefault = true,
            MinWidth = 84,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        panel.Children.Add(close);
        window.Content = panel;
        window.ShowDialog();
    }

    private static Window MakeShell(Window owner, string title, double width)
    {
        var window = new Window
        {
            Title = title,
            Owner = owner,
            Width = width,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#17181D")!,
            Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#E9EAEE")!,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 13,
        };
        window.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/UI/Theme.xaml"),
        });
        window.SourceInitialized += (_, _) => DarkTitleBar.Apply(window);
        return window;
    }
}
