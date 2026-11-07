using System.IO;
using System.Text.Json;

namespace ISLA.Reminders;

/// <summary>A single calendar reminder. Times are stored as local wall-clock values.</summary>
public sealed class Reminder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public DateTime DueAt { get; set; }
    public bool Acknowledged { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? AcknowledgedAtUtc { get; set; }
}

/// <summary>
/// Stores reminders in their own JSON file so the settings file stays small. The service is
/// intentionally synchronous: the list is tiny and all callers already run on the UI thread.
/// </summary>
public sealed class ReminderService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _remindersPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ISLA",
        "reminders.json");

    private readonly List<Reminder> _reminders = new();

    public ReminderService()
    {
        Load();
    }

    public IReadOnlyList<Reminder> All => _reminders;

    public Reminder? Get(string id) =>
        _reminders.FirstOrDefault(reminder => reminder.Id == id);

    public IEnumerable<Reminder> ForDate(DateTime date) =>
        _reminders
            .Where(reminder => reminder.DueAt.Date == date.Date)
            .OrderBy(reminder => reminder.DueAt)
            .ThenBy(reminder => reminder.Title, StringComparer.CurrentCultureIgnoreCase);

    public IEnumerable<Reminder> DueUnacknowledged(DateTime now) =>
        _reminders
            .Where(reminder => !reminder.Acknowledged && reminder.DueAt <= now)
            .OrderBy(reminder => reminder.DueAt);

    public Reminder Add(string title, DateTime dueAt)
    {
        var reminder = new Reminder
        {
            Title = title.Trim(),
            DueAt = DateTime.SpecifyKind(dueAt, DateTimeKind.Local)
        };

        _reminders.Add(reminder);
        Save();
        return reminder;
    }

    public bool Delete(string id)
    {
        var reminder = Get(id);
        if (reminder is null)
        {
            return false;
        }

        _reminders.Remove(reminder);
        Save();
        return true;
    }

    public bool Acknowledge(string id)
    {
        var reminder = Get(id);
        if (reminder is null || reminder.Acknowledged)
        {
            return false;
        }

        reminder.Acknowledged = true;
        reminder.AcknowledgedAtUtc = DateTime.UtcNow;
        Save();
        return true;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_remindersPath)!);
            File.WriteAllText(_remindersPath, JsonSerializer.Serialize(_reminders, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Reminders keep working for this session; the next Save retries the write.
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_remindersPath))
            {
                return;
            }

            var stored = JsonSerializer.Deserialize<List<Reminder>>(File.ReadAllText(_remindersPath));
            if (stored is null)
            {
                return;
            }

            _reminders.Clear();
            _reminders.AddRange(stored
                .Where(reminder =>
                    reminder is not null &&
                    !string.IsNullOrWhiteSpace(reminder.Id) &&
                    !string.IsNullOrWhiteSpace(reminder.Title))
                .Select(reminder =>
                {
                    reminder.DueAt = reminder.DueAt.Kind == DateTimeKind.Utc
                        ? reminder.DueAt.ToLocalTime()
                        : DateTime.SpecifyKind(reminder.DueAt, DateTimeKind.Local);
                    return reminder;
                }));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _reminders.Clear();
        }
    }
}
