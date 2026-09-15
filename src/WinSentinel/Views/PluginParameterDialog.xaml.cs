using System.Windows;

namespace WinSentinel.Views;

/// <summary>Small modal prompt used when a plugin command declares a required parameter.</summary>
public partial class PluginParameterDialog : Window
{
    public PluginParameterDialog(string commandTitle, string label, string placeholder)
    {
        InitializeComponent();
        HeaderText.Text = commandTitle;
        LabelText.Text = label;
        ValueBox.Text = placeholder;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    /// <summary>The text the user entered (only meaningful when the dialog returns true).</summary>
    public string Value => ValueBox.Text.Trim();

    private void OnRun(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
