using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EventLens.Controls;
using EventLens.Core.Util;
using EventLens.Services;
using Wpf.Ui.Controls;

namespace EventLens.Views;

/// <summary>
/// Ask-AI window. The first question sends the (small) context once; follow-ups send only a short reminder
/// and the previous answer. Answers are cached locally, so asking the same thing again costs no tokens.
/// </summary>
public partial class AiWindow : FluentWindow
{
    private readonly string _subject;
    private string _context;
    private string _mode;
    private string _firstQuestion;
    private readonly Func<Task<string>>? _deepContext;
    private readonly string _deepQuestion;
    private readonly List<(string Role, string Text)> _transcript = [];
    private string _lastAnswer = "";
    private bool _busy;

    private AiWindow(string subject, string context, string question, string mode, Func<Task<string>>? deepContext, string? deepQuestion)
    {
        InitializeComponent();
        _subject = subject;
        _context = context;
        _mode = mode;
        _firstQuestion = question;
        _deepContext = deepContext;
        _deepQuestion = deepQuestion ?? question;
        HeaderText.Text = subject;
        ContextBox.Text = context;
        Bar.Title = "Ask AI - EventLens";
        DeepButton.Visibility = deepContext is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateProvider();
    }

    /// <param name="deepContext">Optional: builds the bigger context for "Deep analysis".</param>
    public static void Open(string subject, string context, string question, string mode = "single",
        Func<Task<string>>? deepContext = null, string? deepQuestion = null)
    {
        var w = new AiWindow(subject, context, question, mode, deepContext, deepQuestion) { Owner = Application.Current.MainWindow };
        w.Show();
        if (AiService.IsConfigured) _ = w.AskFirstAsync(force: false);
        else w.ShowSetup();
    }

    private void UpdateProvider()
    {
        var tokens = Math.Max(1, _context.Length / 4);
        ProviderText.Text = $"{AiService.CurrentProvider.Name} · {AiService.CurrentModel} · answers in {AiService.AnswerLanguage} · ~{tokens:N0} tokens sent";
    }

    private void ShowSetup()
    {
        SetupPanel.Visibility = Visibility.Visible;
        Scroller.Visibility = Visibility.Collapsed;
    }

    // ---------- asking ----------

    private async Task AskFirstAsync(bool force)
    {
        if (_busy) return;
        if (!AiService.IsConfigured) { ShowSetup(); return; }
        SetupPanel.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Visible;
        UpdateProvider();

        var key = AiService.CacheKey(_mode, _context, _firstQuestion);
        if (!force && HistoryStore.GetAi(key) is { } cached)
        {
            AddQuestion(_firstQuestion);
            AddAnswer(cached.Answer, $"Saved answer from {cached.Created:g} · no tokens used", allowReask: true);
            _lastAnswer = cached.Answer;
            return;
        }

        var content = $"{_firstQuestion}\n\n---\n{_context}";
        await SendAsync(_firstQuestion, [new ChatMessage("user", content)], key);
    }

    private async Task AskFollowUpAsync(string question)
    {
        if (_busy || string.IsNullOrWhiteSpace(question)) return;
        if (_lastAnswer.Length == 0) { _firstQuestion = question; await AskFirstAsync(force: true); return; }
        await SendAsync(question, [AiService.FollowUp(_subject, _lastAnswer, question)], cacheKey: null);
    }

    private async Task SendAsync(string shownQuestion, IReadOnlyList<ChatMessage> messages, string? cacheKey)
    {
        _busy = true;
        SendButton.IsEnabled = false;
        AddQuestion(shownQuestion);
        var status = AddStatus("Thinking…");

        try
        {
            var answer = await AiService.ChatAsync(messages, default, msg => Dispatcher.Invoke(() => status.Text = msg));
            Conversation.Children.Remove(status.Parent as UIElement ?? status);
            AddAnswer(answer, null, allowReask: false);
            _lastAnswer = answer;
            if (cacheKey is not null) HistoryStore.PutAi(cacheKey, answer, AiService.CurrentProvider.Name, AiService.CurrentModel);
        }
        catch (Exception ex)
        {
            Conversation.Children.Remove(status.Parent as UIElement ?? status);
            AddError(ex.Message, () => _ = RetryAsync(shownQuestion, messages, cacheKey));
        }
        finally
        {
            _busy = false;
            SendButton.IsEnabled = true;
            Scroller.ScrollToEnd();
        }
    }

    private async Task RetryAsync(string shownQuestion, IReadOnlyList<ChatMessage> messages, string? cacheKey)
    {
        // remove the error bubble and the failed question bubble so the retry does not duplicate them
        for (int i = 0; i < 2 && Conversation.Children.Count > 0; i++)
            Conversation.Children.RemoveAt(Conversation.Children.Count - 1);
        if (_transcript.Count > 0 && _transcript[^1].Role == "user") _transcript.RemoveAt(_transcript.Count - 1);
        await SendAsync(shownQuestion, messages, cacheKey);
    }

    // ---------- bubbles ----------

    private void AddQuestion(string text)
    {
        _transcript.Add(("user", text));
        var tb = new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, FontSize = 14 };
        Conversation.Children.Add(Bubble(tb, user: true));
        Scroller.ScrollToEnd();
    }

    private System.Windows.Controls.TextBlock AddStatus(string text)
    {
        var tb = new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.65, FontSize = 14 };
        Conversation.Children.Add(Bubble(tb, user: false));
        Scroller.ScrollToEnd();
        return tb;
    }

    private void AddAnswer(string markdown, string? note, bool allowReask)
    {
        _transcript.Add(("assistant", markdown));
        var rtb = new System.Windows.Controls.RichTextBox
        {
            IsReadOnly = true,
            IsDocumentEnabled = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            Document = MarkdownRenderer.Build(markdown),
        };
        rtb.SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        var panel = new StackPanel();
        panel.Children.Add(rtb);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        if (note is not null)
        {
            var n = new System.Windows.Controls.TextBlock { Text = note, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            n.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            actions.Children.Add(n);
        }
        var copy = new Wpf.Ui.Controls.Button { Content = "Copy", Appearance = ControlAppearance.Transparent, Padding = new Thickness(8, 2, 8, 2) };
        copy.Click += (_, _) => SafeCopy(Markdown.ToPlainText(markdown));
        actions.Children.Add(copy);
        if (allowReask)
        {
            var again = new Wpf.Ui.Controls.Button { Content = "Ask again", Appearance = ControlAppearance.Transparent, Padding = new Thickness(8, 2, 8, 2) };
            again.Click += (_, _) => _ = AskFirstAsync(force: true);
            actions.Children.Add(again);
        }
        panel.Children.Add(actions);

        Conversation.Children.Add(Bubble(panel, user: false));
        Scroller.ScrollToEnd();
    }

    private void AddError(string message, Action retry)
    {
        var panel = new StackPanel();
        var tb = new System.Windows.Controls.TextBlock
        {
            Text = "⚠ " + message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5F)),
            FontSize = 14,
        };
        panel.Children.Add(tb);
        var btn = new Wpf.Ui.Controls.Button { Content = "Retry", Appearance = ControlAppearance.Primary, Margin = new Thickness(0, 10, 0, 0) };
        btn.Click += (_, _) => retry();
        panel.Children.Add(btn);
        Conversation.Children.Add(Bubble(panel, user: false));
        Scroller.ScrollToEnd();
    }

    private static Border Bubble(UIElement child, bool user)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = user ? new Thickness(80, 6, 0, 6) : new Thickness(0, 6, 24, 6),
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(1),
            Child = child,
        };
        border.SetResourceReference(Border.BackgroundProperty, user ? "CardBackgroundFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return border;
    }

    // ---------- events ----------

    private async void OnDeep(object sender, RoutedEventArgs e)
    {
        if (_deepContext is null || _busy) return;
        DeepButton.IsEnabled = false;
        try
        {
            _context = await _deepContext();
            _mode = "deep";
            _firstQuestion = _deepQuestion;
            ContextBox.Text = _context;
            _lastAnswer = "";
            await AskFirstAsync(force: false);
        }
        catch (Exception ex)
        {
            AddStatus("⚠ Could not collect the surrounding events: " + ex.Message);
        }
        finally
        {
            DeepButton.IsEnabled = true;
        }
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        var q = Input.Text;
        Input.Text = "";
        await AskFollowUpAsync(q);
    }

    private async void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var q = Input.Text;
        Input.Text = "";
        await AskFollowUpAsync(q);
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## " + _subject).AppendLine();
        foreach (var (role, text) in _transcript)
        {
            sb.AppendLine(role == "user" ? "### Question" : "### Answer");
            sb.AppendLine(text).AppendLine();
        }
        SafeCopy(_transcript.Count > 0 ? sb.ToString() : _context);
    }

    private static void SafeCopy(string text)
    {
        try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        Navigator.Navigate(typeof(SettingsPage));
        Application.Current.MainWindow?.Activate();
        Close();
    }
}
