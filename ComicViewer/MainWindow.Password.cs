using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace ComicViewer;

public partial class MainWindow
{
    private Task<string?> ShowPasswordOverlayAsync(string archivePath, bool knownPasswordsTried)
    {
        _passwordPrompt?.TrySetResult(null);
        _passwordPrompt = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        PasswordPromptTextBlock.Text = knownPasswordsTried
            ? "所有已知密码均无法解锁，请输入正确密码"
            : "请输入压缩包密码";
        PasswordArchiveNameTextBlock.Text = Path.GetFileName(archivePath);
        ArchivePasswordBox.Clear();
        PasswordOpenButton.IsEnabled = false;
        PasswordOverlay.Visibility = Visibility.Visible;
        OpenArchiveButton.IsEnabled = false;
        Dispatcher.BeginInvoke((Action)(() => ArchivePasswordBox.Focus()));
        return _passwordPrompt.Task;
    }

    private void CompletePasswordPrompt(string? password)
    {
        var prompt = _passwordPrompt;
        if (prompt is null)
        {
            return;
        }

        _passwordPrompt = null;
        PasswordOverlay.Visibility = Visibility.Collapsed;
        ArchivePasswordBox.Clear();
        OpenArchiveButton.IsEnabled = true;
        prompt.TrySetResult(password);
    }

    private void PasswordOpenButton_Click(object sender, RoutedEventArgs e)
    {
        CompletePasswordPrompt(ArchivePasswordBox.Password);
    }

    private void PasswordCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CompletePasswordPrompt(null);
    }

    private void ArchivePasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(ArchivePasswordBox.Password))
        {
            CompletePasswordPrompt(ArchivePasswordBox.Password);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CompletePasswordPrompt(null);
            e.Handled = true;
        }
    }

    private void ArchivePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        PasswordOpenButton.IsEnabled = !string.IsNullOrWhiteSpace(ArchivePasswordBox.Password);
    }

    private static string PasswordHistoryFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComicViewer", "password-history.json");

    private void LoadPasswordHistory()
    {
        try
        {
            if (!File.Exists(PasswordHistoryFilePath))
            {
                return;
            }

            var passwords = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(PasswordHistoryFilePath));
            foreach (var password in passwords?.Where(password => !string.IsNullOrWhiteSpace(password)).Distinct() ?? [])
            {
                PasswordHistory.Add(password);
            }
        }
        catch
        {
        }
    }

    private void RememberPassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var existing = PasswordHistory.FirstOrDefault(item => string.Equals(item, password, StringComparison.Ordinal));
        if (existing is not null)
        {
            PasswordHistory.Remove(existing);
        }

        PasswordHistory.Insert(0, password);
        while (PasswordHistory.Count > 50)
        {
            PasswordHistory.RemoveAt(PasswordHistory.Count - 1);
        }

        SavePasswordHistory();
    }

    private void SavePasswordHistory()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PasswordHistoryFilePath)!);
            File.WriteAllText(PasswordHistoryFilePath, JsonSerializer.Serialize(PasswordHistory.ToList(), new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch
        {
        }
    }

    private static bool IsLikelyPasswordProblem(Exception exception)
    {
        var message = exception.ToString();
        return message.Contains("password", StringComparison.OrdinalIgnoreCase)
            || message.Contains("encrypted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("crypt", StringComparison.OrdinalIgnoreCase)
            || message.Contains("crc", StringComparison.OrdinalIgnoreCase)
            || message.Contains("data error", StringComparison.OrdinalIgnoreCase);
    }
}
