using System.Text.Json;
using System.IO;

namespace ISLA.Pomodoro;

public enum PomodoroPhase
{
    Focus,
    ShortBreak,
    LongBreak
}

public sealed class PomodoroState
{
    public PomodoroPhase Phase { get; set; } = PomodoroPhase.Focus;
    public int CompletedFocusSessions { get; set; }
    public bool IsRunning { get; set; }
    public DateTime? EndsAtUtc { get; set; }
    public TimeSpan Remaining { get; set; } = TimeSpan.FromMinutes(25);
}

public sealed class PomodoroService
{
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ISLA",
        "pomodoro.json");

    public PomodoroState State { get; private set; } = new();

    public PomodoroService()
    {
        Load();
    }

    public bool Update()
    {
        if (!State.IsRunning || State.EndsAtUtc is not DateTime endsAt)
        {
            return false;
        }

        State.Remaining = endsAt - DateTime.UtcNow;
        if (State.Remaining > TimeSpan.Zero)
        {
            return false;
        }

        State.Remaining = TimeSpan.Zero;
        State.IsRunning = false;
        State.EndsAtUtc = null;
        AdvancePhase();
        Save();
        return true;
    }

    public void StartOrResume()
    {
        if (State.IsRunning)
        {
            return;
        }

        State.EndsAtUtc = DateTime.UtcNow + State.Remaining;
        State.IsRunning = true;
        Save();
    }

    public void Pause()
    {
        Update();
        if (!State.IsRunning)
        {
            return;
        }

        State.Remaining = State.EndsAtUtc!.Value - DateTime.UtcNow;
        State.IsRunning = false;
        State.EndsAtUtc = null;
        Save();
    }

    public void Reset()
    {
        State.IsRunning = false;
        State.EndsAtUtc = null;
        State.Remaining = DurationFor(State.Phase);
        Save();
    }

    private void AdvancePhase()
    {
        if (State.Phase == PomodoroPhase.Focus)
        {
            State.CompletedFocusSessions++;
            State.Phase = State.CompletedFocusSessions % 4 == 0
                ? PomodoroPhase.LongBreak
                : PomodoroPhase.ShortBreak;
        }
        else
        {
            State.Phase = PomodoroPhase.Focus;
        }

        State.Remaining = DurationFor(State.Phase);
    }

    private static TimeSpan DurationFor(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.ShortBreak => TimeSpan.FromMinutes(5),
        PomodoroPhase.LongBreak => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromMinutes(25)
    };

    private void Load()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return;
            }

            var saved = JsonSerializer.Deserialize<PomodoroState>(File.ReadAllText(_statePath));
            if (saved is null)
            {
                return;
            }

            State = saved;
            Update();
        }
        catch (JsonException)
        {
            State = new PomodoroState();
        }
        catch (IOException)
        {
            State = new PomodoroState();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        File.WriteAllText(_statePath, JsonSerializer.Serialize(State, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }
}
