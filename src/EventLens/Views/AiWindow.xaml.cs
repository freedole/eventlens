using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EventLens.Services;
using Wpf.Ui.Controls;

namespace EventLens.Views;

/// <summary>Chat window: sends event context + question to the configured AI provider.</summary>
public partial class AiWindow : FluentWindow
{
    private readonly List<ChatMessage> _history = [];
    private readonly string _context;
    private bool _busy;

    private AiWindow(string title, string context)
    {
        InitializeComponent();
        _context = context;
        HeaderText.Text = title;
        ContextBox.Text = context;
        Bar.Title = "Ask AI - EventLens";
        UpdateProvider();
    }

    public static void Open(string title, string context, string question)
    {
        var w = new AiWindow(title, context) { Owner = Application.Current.MainWindow };
        w.Show();
        if (AiService.IsConfigured) _ = w.SendAsync(question, true);
        else w.ShowSetup();
    }

    private void UpdateProvider()
    {
        var p = AiService.CurrentProvider;
        var model = string.IsNullOrWhiteSpace(AppSettings.Current.AiModel) ? p.Model : AppSettings.Current.AiModel;
        ProviderText.Text = $"{p.Name} · {model} · answers in {AiService.AnswerLanguage}";
    }

    private void ShowSetup()
    {
        SetupPanel.Visibility = Visibility.Visible;
        Scroller.Visibility = Visibility.Collapsed;
    }

    private async Task SendAsync(string question, bool first)
    {
        if (_busy || string.IsNullOrWhiteSpace(question)) return;
        if (!AiService.IsConfigured) { ShowSetup(); return; }

        SetupPanel.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Visible;
        UpdateProvider();

        _busy = true;
        SendButton.IsEnabled = false;
        AddBubble(question, true);

        var content = first ? $"{question}\n\n---\n{_context}" : question;
        _history.Add(new ChatMessage("user", content));
        var thinking = AddBubble("Thinking…", false, muted: true);

        try
        {
            var answer = await AiService.ChatAsync(_history);
            _history.Add(new ChatMessage("assistant", answer));
            thinking.Text = answer;
            thinking.Opacity = 1;
        }
        catch (Exception ex)
        {
            _history.RemoveAt(_history.Count - 1);
            thinking.Text = "⚠ " + ex.Message;
            thinking.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5F));
            thinking.Opacity = 1;
        }
        finally
        {
            _busy = false;
            SendButton.IsEnabled = true;
            Scroller.ScrollToEnd();
        }
    }

    private System.Windows.Controls.TextBox AddBubble(string text, bool user, bool muted = false)
    {
        var box = new System.Windows.Controls.TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 14,
            Opacity = muted ? 0.6 : 1,
            Padding = new Thickness(0),
        };
        if (user) box.FontWeight = FontWeights.SemiBold;

        var border = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = user ? new Thickness(80, 6, 0, 6) : new Thickness(0, 6, 40, 6),
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            Child = box,
        };
        border.SetResourceReference(Border.BackgroundProperty,
            user ? "CardBackgroundFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        border.BorderThickness = new Thickness(1);

        Conversation.Children.Add(border);
        Scroller.ScrollToEnd();
        return box;
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        var q = Input.Text;
        Input.Text = "";
        await SendAsync(q, _history.Count == 0);
    }

    private async void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var q = Input.Text;
        Input.Text = "";
        await SendAsync(q, _history.Count == 0);
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var m in _history)
        {
            sb.AppendLine(m.Role == "user" ? "## Question" : "## Answer");
            sb.AppendLine(m.Content);
            sb.AppendLine();
        }
        try { Clipboard.SetText(sb.Length > 0 ? sb.ToString() : _context); } catch { /* clipboard busy */ }
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        Navigator.Navigate(typeof(SettingsPage));
        Application.Current.MainWindow?.Activate();
        Close();
    }
}
