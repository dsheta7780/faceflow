using System.Windows;
using System.Windows.Controls;

namespace FaceFlow.App.ViewModels;

/// <summary>Minimal themed text-input dialog (WPF has no built-in InputBox).</summary>
public static class Prompt
{
    public static string? Ask(string message, string title, string initial = "")
    {
        var win = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)Application.Current.Resources["B.Bg"],
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["B.Text"]
        };

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            Margin = new Thickness(0, 0, 0, 12),
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["B.TextDim"]
        });

        var box = new TextBox
        {
            Text = initial,
            Style = (Style)Application.Current.Resources["Input"],
            Margin = new Thickness(0, 0, 0, 18)
        };
        panel.Children.Add(box);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", Width = 100, Margin = new Thickness(0, 0, 10, 0),
                                  Style = (Style)Application.Current.Resources["Btn.Ghost"] };
        var ok = new Button { Content = "OK", Width = 100, IsDefault = true,
                              Style = (Style)Application.Current.Resources["Btn.Primary"] };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        string? result = null;
        ok.Click += (_, _) => { result = box.Text; win.Close(); };
        cancel.Click += (_, _) => win.Close();

        win.Content = panel;
        box.Focus();
        box.SelectAll();
        win.ShowDialog();
        return result;
    }
}
