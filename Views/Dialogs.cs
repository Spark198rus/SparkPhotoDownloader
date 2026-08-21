using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SparkPhotoDownloader.Views;

/// <summary>A message box with either one button or a confirm/cancel pair.</summary>
public sealed class MessageDialog : Window
{
    private bool? _result;

    private MessageDialog(string title, string message, bool okOnly, string confirmText)
    {
        Title = title;
        SizeToContent = SizeToContent.Height;
        Width = 460;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = this.FindResource("SurfaceBrush") as IBrush ?? Brushes.Black;

        var confirm = new Button
        {
            Content = confirmText,
            IsDefault = true,
            MinWidth = 92,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        confirm.Classes.Add("Primary");
        confirm.Click += (_, _) =>
        {
            _result = true;
            Close();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        if (!okOnly)
        {
            var cancel = new Button
            {
                Content = "Cancel",
                IsCancel = true,
                MinWidth = 92,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            cancel.Click += (_, _) =>
            {
                _result = false;
                Close();
            };
            buttons.Children.Add(cancel);
        }

        buttons.Children.Add(confirm);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 15,
                    FontWeight = FontWeight.SemiBold,
                },
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.85,
                },
                buttons,
            },
        };
    }

    /// <summary>Returns true for confirm, false for cancel, null when closed another way.</summary>
    public static async Task<bool?> ShowAsync(
        Window owner, string title, string message, bool okOnly, string confirmText = "OK")
    {
        var dialog = new MessageDialog(title, message, okOnly, confirmText);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}

/// <summary>A single-line text prompt, used for Job Codes and preset names.</summary>
public sealed class TextPromptDialog : Window
{
    private string? _result;
    private readonly TextBox _input;

    private TextPromptDialog(string title, string message, string? initialValue)
    {
        Title = title;
        SizeToContent = SizeToContent.Height;
        Width = 460;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = this.FindResource("SurfaceBrush") as IBrush ?? Brushes.Black;

        _input = new TextBox { Text = initialValue ?? string.Empty, PlaceholderText = "…" };

        var ok = new Button
        {
            Content = "OK",
            IsDefault = true,
            MinWidth = 92,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ok.Classes.Add("Primary");
        ok.Click += (_, _) =>
        {
            _result = _input.Text;
            Close();
        };

        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 92,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 },
                _input,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok },
                },
            },
        };

        Opened += (_, _) => _input.Focus();
    }

    public static async Task<string?> ShowAsync(
        Window owner, string title, string message, string? initialValue = null)
    {
        var dialog = new TextPromptDialog(title, message, initialValue);
        await dialog.ShowDialog(owner);
        return string.IsNullOrWhiteSpace(dialog._result) ? null : dialog._result.Trim();
    }
}
