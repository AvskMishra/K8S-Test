using System.Text.Json;
using System.Text.Json.Serialization;
using PatchOrchestrator.Models;

namespace PatchOrchestrator.Services;

// Reads and writes data/patch-schedule.json - the ONLY file an external system needs to touch
// to schedule a patch window (see README.md for the exact JSON shape). This app reads it every
// tick and writes it back with updated State/History/BootId fields, so restarting the app never
// loses track of an in-progress drain.
//
// A SemaphoreSlim serialises every read/write against the file. That matters here because a
// single tick both loads the file at the start and saves it at the end - without the lock, two
// overlapping ticks (e.g. after a slow Kubernetes API call) could interleave and corrupt the file.
public class ScheduleStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<ScheduleStore> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ScheduleStore(IConfiguration configuration, ILogger<ScheduleStore> logger)
    {
        _logger = logger;
        _filePath = configuration["PatchOrchestrator:SchedulesFilePath"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "patch-schedule.json");
    }

    // Loads every schedule currently in the file. Never throws - a missing or unreadable file
    // just means "nothing scheduled right now" rather than crashing the background service.
    public async Task<List<PatchSchedule>> LoadAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_filePath))
            {
                return new List<PatchSchedule>();
            }

            var json = await File.ReadAllTextAsync(_filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<PatchSchedule>();
            }

            return JsonSerializer.Deserialize<List<PatchSchedule>>(json, JsonOptions) ?? new List<PatchSchedule>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read {FilePath} - treating as 'no schedules' until this is fixed", _filePath);
            return new List<PatchSchedule>();
        }
        finally
        {
            _lock.Release();
        }
    }

    // Overwrites the whole file with the given list, creating the data folder if needed.
    public async Task SaveAllAsync(List<PatchSchedule> schedules)
    {
        await _lock.WaitAsync();
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(schedules, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json);
        }
        finally
        {
            _lock.Release();
        }
    }
}
