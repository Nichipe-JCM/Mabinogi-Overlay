using System.Windows;
using System.Windows.Input;
using TestOverlay.App.Services;

namespace TestOverlay.App;

public partial class ProfileNameDialog : Window
{
    public ProfileNameDialog(string initialName)
    {
        InitializeComponent();
        ProfileName = initialName?.Trim() ?? string.Empty;
        ProfileNameBox.Text = ProfileName;
        ProfileNameBox.SelectAll();
        Loaded += (_, _) => ProfileNameBox.Focus();
    }

    public string ProfileName { get; private set; }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var name = ProfileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorText.Text = L.T("Enter a profile name.");
            return;
        }

        ProfileName = name;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            CancelButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
