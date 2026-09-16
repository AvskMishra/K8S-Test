using System.Globalization;
using System.Text.Json.Serialization;

namespace PatchOrchestrator.Models;

// One entry in data/patch-schedule.json - "this server needs a patch window at this date/time".
//
// Only ServerName, Date and Time are meant to be supplied by whoever/whatever schedules the
// patch (a person editing the file, or another system writing to it). Everything below that
// comment is state this app owns: it reads those fields back on every check and writes updated
// values, so restarting the app never loses track of where a schedule was up to.
public class PatchSchedule
{
    // Must exactly match a Kubernetes node name (run `kubectl get nodes` to see the real names).
    public string ServerName { get; set; } = string.Empty;

    // Kept as separate Date/Time strings, not one combined timestamp, because that matches the
    // shape described for the external input ("serverName, Date, Time").
    public string Date { get; set; } = string.Empty; // "yyyy-MM-dd", e.g. "2026-09-12"
    public string Time { get; set; } = string.Empty; // "HH:mm", 24-hour clock, e.g. "10:00"

    // --- state this app owns from here down ---

    public PatchScheduleState State { get; set; } = PatchScheduleState.Pending;

    // The node's Kubernetes "boot ID" captured the moment draining finished, right before we
    // tainted it and handed it over for patching. A node's Ready condition alone can't tell
    // "it rebooted" apart from "it was fine the whole time" - but the boot ID always changes
    // across a real reboot, so comparing against this later is how the app knows the 3rd party's
    // reboot actually happened.
    public string? BootIdAtDrainComplete { get; set; }

    // Plain-English timeline of what this app has done for this schedule, newest entry last.
    // Meant to be read directly out of the JSON file by a human - no separate log file needed
    // to answer "what happened with server01's patch window?".
    public List<string> History { get; set; } = new();

    // Not stored in the JSON file - just Date + Time combined for comparisons against "now".
    // Assumes Date/Time are in the same local time zone as the machine running this app; a
    // multi-timezone deployment would want to make that explicit (e.g. store as UTC instead).
    [JsonIgnore]
    public DateTime PatchAtLocal =>
        DateTime.ParseExact($"{Date} {Time}", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
