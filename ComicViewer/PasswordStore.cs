using System.IO;
using System.Text.Json;

namespace ComicViewer;

internal sealed class PasswordStore
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ComicViewer", "password-history.json");
    private readonly List<string> _passwords = [];

    public PasswordStore()
    {
        try
        {
            if (File.Exists(FilePath))
                _passwords.AddRange((JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? [])
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Take(50));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    public IReadOnlyList<string> Except(string? password) => _passwords.Where(value => value != password).ToArray();

    public void Remember(string? password)
    {
        if (string.IsNullOrWhiteSpace(password)) return;
        _passwords.Remove(password);
        _passwords.Insert(0, password);
        if (_passwords.Count > 50) _passwords.RemoveRange(50, _passwords.Count - 50);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_passwords));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }
}
