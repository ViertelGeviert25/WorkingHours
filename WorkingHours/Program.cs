using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Security.Principal;
using System.Xml.Linq;

namespace WorkingHours
{
    public class Program
    {
        #region Constants and Fields
        private static readonly DateTime StartDate = new(2026, 11, 1);
        private static readonly int DailyTargetMinutes = 8 * 60;
        private static readonly int DailyBreakMinutes = 60;

        private static readonly int DailyAdditionalWorkMinutes = 3;

        private const int EventWorkstationLocked = 4800;
        private const int EventWorkstationUnlocked = 4801;

        private static readonly string CurrentUser = Environment.UserName;
        private static readonly string CurrentUserSid =
            WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
        #endregion

        #region Data Structures
        private record Workevent(DateTime Timestamp, bool IsUnlocked);
        private record WorkInterval(DateTime Start, DateTime End);
        private record AbsenceEntry(string? Type, string? WorkLocation);

        private class DailyResult
        {
            public int PresenceMinutes { get; set; }
            public int WorkMinutes { get; set; }
            public int BreakMinutes { get; set; }
            public string? AbsenceType { get; set; }
            public string? WorkLocation { get; set; }
            public bool HasManualEntry { get; set; }
        }
        #endregion

        /// <summary>
        /// Configures the console and runs the working-hours evaluation.
        /// </summary>
        /// <param name="args">Command-line arguments.</param>
        public static void Main(string[] args)
        {
            if (!EnsureAdministrator(args))
            {
                return;
            }

            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "Arbeitszeitkonto";

            WriteSectionHeader("ARBEITSZEITKONTO");
            EnsureAuditPolicy();
            Dictionary<DateTime, AbsenceEntry> absences = LoadAbsences();
            Console.WriteLine();

            Console.WriteLine($"Benutzer:       {CurrentUser}");
            Console.WriteLine($"Auswertung ab:  {StartDate:dd.MM.yyyy}");
            Console.WriteLine($"Sollzeit:       40 Stunden pro Woche");
            Console.WriteLine($"Pause:          60 Minuten pro Arbeitstag");
            Console.WriteLine();

            try
            {
                Console.WriteLine("Lese Windows-Ereignisanzeige...");
                List<Workevent> events = LoadEvents();
                Console.WriteLine($"{events.Count} relevante Ereignisse gefunden.");

                if (events.Count == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Keine Ereignisse gefunden!");
                    Console.ResetColor();
                }

                List<WorkInterval> intervals = CreateWorkIntervals(events);
                Console.WriteLine($"{intervals.Count} Arbeitsintervalle erkannt.");

                Dictionary<DateTime, DailyResult> dailyResults = CalculateDailyResults(intervals, absences);

                ShowDailyOverview(dailyResults);
                ShowWeeklyOverview(dailyResults);
                ExportYearlyResults(dailyResults);
                ShowTotalBalance(dailyResults);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Fehler: " + ex.Message);
                Console.ResetColor();
            }

            WaitForExit();
        }

        /// <summary>
        /// Ensures that the application runs with administrator privileges.
        /// </summary>
        /// <param name="args">Command-line arguments to pass to the elevated process.</param>
        /// <returns><see langword="true"/> when the current process is elevated; otherwise, <see langword="false"/>.</returns>
        static bool EnsureAdministrator(string[] args)
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);

            if (principal.IsInRole(WindowsBuiltInRole.Administrator))
            {
                return true;
            }

            try
            {
                string processPath = Environment.ProcessPath
                    ?? throw new InvalidOperationException("Der Pfad der Anwendung konnte nicht ermittelt werden.");

                var startInfo = new ProcessStartInfo
                {
                    FileName = processPath,
                    UseShellExecute = true,
                    Verb = "runas"
                };

                foreach (string argument in args)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                Process.Start(startInfo);
                return false;
            }
            catch (Win32Exception)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Administratorrechte wurden nicht erteilt. Das Programm wird beendet.");
                Console.ResetColor();
                return false;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Programm konnte nicht mit Administratorrechten gestartet werden: {ex.Message}");
                Console.ResetColor();
                return false;
            }
        }

        /// <summary>
        /// Ensures that successful workstation lock and unlock events are audited (auditing must be enabled).
        /// </summary>
        static void EnsureAuditPolicy()
        {
            const string subcategory = "Andere Anmelde-/Abmeldeereignisse";

            try
            {
                // auditpol /get /subcategory:"Andere Anmelde-/Abmeldeereignisse"
                string output = RunAuditPol($"/get /subcategory:\"{subcategory}\"");
                string status = output.Trim();

                if (status.EndsWith("Success", StringComparison.OrdinalIgnoreCase)
                    || status.EndsWith("Erfolg", StringComparison.OrdinalIgnoreCase))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("Erfolgsüberwachung ist aktiv.");
                    Console.ResetColor();
                    return;
                }

                Console.WriteLine("Erfolgsüberwachung ist nicht aktiviert. Aktiviere sie...");
                // auditpol /set /subcategory:"Andere Anmelde-/Abmeldeereignisse" /success:enable
                RunAuditPol($"/set /subcategory:\"{subcategory}\" /success:enable");
                Console.WriteLine("Erfolgsüberwachung wurde aktiviert.");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Audit-Richtlinie konnte nicht geprüft oder gesetzt werden: {ex.Message}");
                Console.ResetColor();
            }
        }

        /// <summary>
        /// Runs auditpol with the specified arguments and returns its standard output.
        /// </summary>
        /// <param name="arguments">Arguments passed to auditpol.</param>
        /// <returns>The command's standard output.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when auditpol exits with a non-zero exit code.
        /// </exception>
        static string RunAuditPol(string arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "auditpol.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? $"auditpol wurde mit dem Exit-Code {process.ExitCode} beendet."
                    : error.Trim());
            }

            return output;
        }

        /// <summary>
        /// Loads relevant workstation lock and unlock events from the Windows event log.
        /// </summary>
        /// <returns>The user's workstation events ordered by timestamp.</returns>
        static List<Workevent> LoadEvents()
        {
            var events = new List<Workevent>();
            string query = "*[System[(EventID=4800 or EventID=4801)]]";

            var logQuery = new EventLogQuery("Security", PathType.LogName, query)
            {
                ReverseDirection = true
            };

            using var reader = new EventLogReader(logQuery);

            EventRecord? record;

            while ((record = reader.ReadEvent()) != null)
            {
                using (record)
                {
                    if (record.TimeCreated == null)
                    {
                        continue;
                    }

                    DateTime timestamp = record.TimeCreated.Value;

                    if (timestamp < StartDate)
                    {
                        break;
                    }

                    try
                    {
                        XElement xml = XElement.Parse(record.ToXml());

                        var eventData = xml.Descendants()
                            .Where(x => x.Name.LocalName == "Data")
                            .ToDictionary(x => (string?)x.Attribute("Name") ?? "", x => x.Value);


                        if (!IsCurrentUserEvent(eventData))
                            continue;

                        bool isUnlocked = record.Id == EventWorkstationUnlocked;
                        events.Add(new Workevent(timestamp, isUnlocked));
                    }
                    catch
                    {
                        // Skip invalid entries.
                    }
                }

            }

            return events.OrderBy(x => x.Timestamp).ToList();
        }

        /// <summary>
        /// Determines whether an event belongs to the current Windows user.
        /// </summary>
        /// <param name="eventData">The event data fields.</param>
        /// <returns><see langword="true"/> when the event belongs to the current user.</returns>
        static bool IsCurrentUserEvent(Dictionary<string, string> eventData)
        {
            string eventUserSid = eventData.GetValueOrDefault("SubjectUserSid")
                ?? eventData.GetValueOrDefault("TargetUserSid")
                ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(eventUserSid))
            {
                return eventUserSid.Equals(CurrentUserSid, StringComparison.OrdinalIgnoreCase);
            }

            string username = eventData.GetValueOrDefault("SubjectUserName")
                ?? eventData.GetValueOrDefault("TargetUserName")
                ?? string.Empty;
            string domain = eventData.GetValueOrDefault("SubjectDomainName")
                ?? eventData.GetValueOrDefault("TargetDomainName")
                ?? string.Empty;
            string qualifiedUsername = $"{domain}\\{username}";
            string currentIdentityName = WindowsIdentity.GetCurrent().Name;

            return username.Equals(CurrentUser, StringComparison.OrdinalIgnoreCase)
                || qualifiedUsername.Equals(currentIdentityName, StringComparison.OrdinalIgnoreCase)
                || username.Equals(currentIdentityName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Creates work intervals from unlock and lock events.
        /// </summary>
        /// <param name="events">The workstation lock and unlock events.</param>
        /// <returns>The resulting work intervals.</returns>
        static List<WorkInterval> CreateWorkIntervals(List<Workevent> events)
        {
            var intervals = new List<WorkInterval>();
            DateTime? workStart = null;

            foreach (var workEvent in events)
            {
                if (workEvent.IsUnlocked)
                {
                    if (workStart == null)
                    {
                        workStart = workEvent.Timestamp;
                    }
                }
                else
                {
                    if (workStart.HasValue)
                    {
                        if (workEvent.Timestamp > workStart.Value)
                        {
                            intervals.Add(new WorkInterval(workStart.Value, workEvent.Timestamp));
                        }
                        workStart = null;
                    }
                }
            }

            // If the workstation is currently unlocked, calculate the interval up to now.
            if (workStart.HasValue)
            {
                DateTime now = DateTime.Now;
                if (now > workStart.Value)
                {
                    intervals.Add(new WorkInterval(workStart.Value, now));
                }
            }

            return intervals;
        }

        /// <summary>
        /// Calculates daily working time from the recorded work intervals and deducts the daily break time.
        /// </summary>
        /// <param name="intervals">The work intervals to process.</param>
        /// <param name="absences">The configured absence dates, types, and work locations.</param>
        /// <returns>Daily presence, break, and work-time results.</returns>
        static Dictionary<DateTime, DailyResult> CalculateDailyResults(
            List<WorkInterval> intervals,
            Dictionary<DateTime, AbsenceEntry> absences)
        {
            var dailyResults = new Dictionary<DateTime, DailyResult>();

            DateTime today = DateTime.Today;

            for (DateTime date = StartDate.Date; date <= today; date = date.AddDays(1))
            {
                if (IsWorkingDay(date))
                {
                    AbsenceEntry? absence = absences.GetValueOrDefault(date);
                    dailyResults[date] = new DailyResult
                    {
                        AbsenceType = absence?.Type,
                        WorkLocation = absence == null ? "Vor Ort" : absence.WorkLocation,
                        HasManualEntry = absence != null
                    };
                }
            }

            foreach (var interval in intervals)
            {
                DateTime date = interval.Start.Date;

                if (IsWorkingDay(date)
                    && dailyResults.TryGetValue(date, out DailyResult? result)
                    && (!result.HasManualEntry || !string.IsNullOrWhiteSpace(result.WorkLocation))
                    && interval.End > interval.Start)
                {
                    int minutes = (int)Math.Round((interval.End - interval.Start).TotalMinutes);
                    result.PresenceMinutes += minutes;
                }
            }

            // Deduct the configured break time per working day.
            foreach (var entry in dailyResults)
            {
                DailyResult result = entry.Value;
                if (result.PresenceMinutes > 0)
                {
                    result.WorkLocation ??= "Vor Ort";
                    result.BreakMinutes = DailyBreakMinutes;
                    result.WorkMinutes = Math.Max(0, result.PresenceMinutes - DailyBreakMinutes);
                }
            }

            return dailyResults;
        }

        /// <summary>
        /// Displays the daily working-hours overview in the console.
        /// </summary>
        /// <param name="dailyResults">The daily results to display.</param>
        static void ShowDailyOverview(Dictionary<DateTime, DailyResult> dailyResults)
        {
            WriteSectionHeader("TAGESÜBERSICHT");

            Console.WriteLine(
                $"{"Datum",-13} {"Anwesenheit",-13} {"Pause",-9} {"Arbeit",-11} {"Saldo"}"
            );
            Console.WriteLine("------------------------------------------------------------------------");

            foreach (var entry in dailyResults.OrderBy(x => x.Key))
            {
                DateTime date = entry.Key;
                DailyResult result = entry.Value;

                int targetMinutes = IsTargetDay(result) ? DailyTargetMinutes : 0;
                int balance = result.WorkMinutes - targetMinutes;

                Console.Write($"{date:dd.MM.yyyy}    ");
                Console.Write($"{FormatTime(result.PresenceMinutes),-14}");
                Console.Write($"{FormatTime(result.BreakMinutes),-10}");
                Console.Write($"{FormatTime(result.WorkMinutes),-12}");

                WriteBalance(balance);

                if (result.AbsenceType != null)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($" ({result.AbsenceType})");
                    Console.ResetColor();
                }
                else if (result.WorkLocation != null)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($" ({result.WorkLocation})");
                    Console.ResetColor();
                }
                else if (result.HasManualEntry)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(" (Urlaub/Gleitzeitausgleich)");
                    Console.ResetColor();
                }
                else if (result.PresenceMinutes == 0)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write(" (Arbeitsort fehlt)");
                    Console.ResetColor();
                }

                Console.WriteLine();
            }
        }

        /// <summary>
        /// Displays the weekly working-hours overview in the console.
        /// </summary>
        /// <param name="dailyResults">The daily results used to build the overview.</param>
        static void ShowWeeklyOverview(Dictionary<DateTime, DailyResult> dailyResults)
        {
            WriteSectionHeader("WOCHENÜBERSICHT");

            Console.WriteLine($"{"Kalenderwoche",-22} {"Arbeit",-13} {"Soll",-11} {"Saldo"}");

            var weeks = dailyResults.GroupBy(x => GetWeekMonday(x.Key)).OrderBy(x => x.Key);

            foreach (var week in weeks)
            {
                DateTime monday = week.Key;
                DateTime sunday = monday.AddDays(6);

                int workedMinutes = week.Sum(x => x.Value.WorkMinutes);
                int workingDays = week.Count(x => IsTargetDay(x.Value));

                int targetMinutes = workingDays * DailyTargetMinutes;
                int balance = workedMinutes - targetMinutes;

                int weekNumber = ISOWeek.GetWeekOfYear(monday);

                Console.Write($"KW {weekNumber:D2} ({monday:dd.MM.}-{sunday:dd:MM.})  ");
                Console.Write($"{FormatTime(workedMinutes),-14}");
                Console.Write($"{FormatTime(targetMinutes),-12}");

                WriteBalance(balance);

                Console.WriteLine();
            }
        }

        /// <summary>
        /// Displays the total working-hours balance in the console.
        /// </summary>
        /// <param name="dailyResults">The daily results used for the total balance.</param>
        static void ShowTotalBalance(Dictionary<DateTime, DailyResult> dailyResults)
        {
            int totalWorkMinutes = dailyResults.Sum(x => x.Value.WorkMinutes);
            int totalTargetMinutes = dailyResults.Count(x => IsTargetDay(x.Value)) * DailyTargetMinutes;
            int totalBreakMinutes = dailyResults.Sum(x => x.Value.BreakMinutes);

            int totalBalance = totalWorkMinutes - totalTargetMinutes;
            WriteSectionHeader("GESAMTERGEBNIS");

            Console.WriteLine($"Auswertungszeitraum: {StartDate:dd.MM.yyyy} bis {DateTime.Today:dd.MM.yyyy}");

            Console.WriteLine($"Arbeitstage: {dailyResults.Count(x => IsTargetDay(x.Value))}");
            Console.WriteLine($"Gesamte Pausenzeit: {FormatTime(totalBreakMinutes)}");
            Console.WriteLine($"Geleistete Arbeitszeit: {FormatTime(totalWorkMinutes)}");
            Console.WriteLine($"Sollarbeitszeit: {FormatTime(totalTargetMinutes)}");

            Console.WriteLine();

            Console.WriteLine(new string('-', 80));
            Console.WriteLine("FINALER ZEITSALDO:");
            Console.WriteLine();
            WriteBalance(totalBalance);

            Console.WriteLine();
            Console.WriteLine(new string('-', 80));
            Console.WriteLine();

            if (totalBalance > 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Du hast {FormatTime(totalBalance)} Überstunden.");
            }
            else if (totalBalance < 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Du hast {FormatTime(Math.Abs(totalBalance))} Minusstunden.");
            }
            else
            {
                Console.WriteLine("Du bist genau im Soll.");
            }

            Console.ResetColor();
        }

        /// <summary>
        /// Exports the daily working-hours results to one Excel-compatible CSV file per year.
        /// </summary>
        /// <param name="dailyResults">The daily results to export.</param>
        static void ExportYearlyResults(Dictionary<DateTime, DailyResult> dailyResults)
        {
            string exportDirectory = Path.Combine(AppContext.BaseDirectory, "Arbeitszeit-Export");
            Directory.CreateDirectory(exportDirectory);

            foreach (var year in dailyResults.GroupBy(entry => entry.Key.Year).OrderBy(group => group.Key))
            {
                int yearWorkMinutes = year.Sum(entry => entry.Value.WorkMinutes);
                int yearTargetMinutes = year.Count(entry => IsTargetDay(entry.Value)) * DailyTargetMinutes;
                int yearBreakMinutes = year.Sum(entry => entry.Value.BreakMinutes);
                int yearBalance = yearWorkMinutes - yearTargetMinutes;

                var csv = new System.Text.StringBuilder();
                csv.AppendLine("Arbeitsdatum;Anwesenheit;Pause;Arbeitszeit;Sollzeit;Saldo;Hinweis");

                foreach (var entry in year.OrderBy(entry => entry.Key))
                {
                    DateTime date = entry.Key;
                    DailyResult result = entry.Value;
                    int targetMinutes = IsTargetDay(result) ? DailyTargetMinutes : 0;
                    int balance = result.WorkMinutes - targetMinutes;
                    string note = result.AbsenceType
                        ?? result.WorkLocation
                        ?? (result.HasManualEntry ? "Urlaub/Gleitzeitausgleich" : "Arbeitsort fehlt");

                    csv.AppendLine(
                        $"{date:dd.MM.yyyy};{FormatTime(result.PresenceMinutes)};" +
                        $"{FormatTime(result.BreakMinutes)};{FormatTime(result.WorkMinutes)};" +
                        $"{FormatTime(targetMinutes)};{FormatSignedTime(balance)};{note.Replace(';', ',')}");
                }

                csv.AppendLine();
                csv.AppendLine(
                    $"Jahressumme {year.Key};{FormatTime(year.Sum(entry => entry.Value.PresenceMinutes))};" +
                    $"{FormatTime(yearBreakMinutes)};{FormatTime(yearWorkMinutes)};" +
                    $"{FormatTime(yearTargetMinutes)};{FormatSignedTime(yearBalance)};");

                string filePath = Path.Combine(exportDirectory, $"{year.Key}_Arbeitszeiten.csv");
                File.WriteAllText(filePath, csv.ToString(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

                Console.WriteLine($"Export gespeichert: {Path.GetFileName(filePath)}");
            }
        }

        /// <summary>
        /// Creates and loads the yearly absence files.
        /// </summary>
        /// <returns>A map of dates to their configured absence types and work locations.</returns>
        static Dictionary<DateTime, AbsenceEntry> LoadAbsences()
        {
            string exportDirectory = Path.Combine(AppContext.BaseDirectory, "Arbeitszeit-Export");
            Directory.CreateDirectory(exportDirectory);

            var absences = new Dictionary<DateTime, AbsenceEntry>();
            int firstYear = StartDate.Year;
            int lastYear = DateTime.Today.Year;

            for (int year = firstYear; year <= lastYear; year++)
            {
                string filePath = Path.Combine(exportDirectory, $"{year}_Abwesenheiten.csv");

                if (!File.Exists(filePath))
                {
                    File.WriteAllText(
                        filePath,
                        "Datum;Typ" + Environment.NewLine,
                        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

                    Console.WriteLine($"Abwesenheitsdatei angelegt: {Path.GetFileName(filePath)}");
                    continue;
                }

                foreach (string line in File.ReadLines(filePath).Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    string[] columns = line.Split(';');
                    if (columns.Length < 2)
                    {
                        continue;
                    }

                    string dateValue = columns[0].Trim();
                    string absenceType = columns[1].Trim();

                    if (string.IsNullOrWhiteSpace(absenceType) && columns.Length >= 3)
                    {
                        absenceType = columns[2].Trim();
                    }

                    if (string.IsNullOrWhiteSpace(absenceType))
                    {
                        continue;
                    }

                    DateTime date;

                    if (DateTime.TryParseExact(
                            dateValue,
                            "dd.MM",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out DateTime dayAndMonth)
                        && dayAndMonth.Day <= DateTime.DaysInMonth(year, dayAndMonth.Month))
                    {
                        date = new DateTime(year, dayAndMonth.Month, dayAndMonth.Day);
                    }
                    else if (!DateTime.TryParseExact(
                                 dateValue,
                                 "dd.MM.yyyy",
                                 CultureInfo.InvariantCulture,
                                 DateTimeStyles.None,
                                 out date)
                             || date.Year != year)
                    {
                        continue;
                    }

                    absences[date.Date] = absenceType.Equals("mobil", StringComparison.OrdinalIgnoreCase)
                        ? new AbsenceEntry(null, "Mobiles Arbeiten")
                        : new AbsenceEntry(absenceType, null);
                }
            }

            return absences;
        }

        #region Helper Methods
        /// <summary>
        /// Determines whether a daily result contributes to the target working time.
        /// </summary>
        /// <param name="result">The daily result to check.</param>
        /// <returns><see langword="true"/> when the day is a regular working day.</returns>
        static bool IsTargetDay(DailyResult result)
        {
            return !string.IsNullOrWhiteSpace(result.WorkLocation);
        }

        /// <summary>
        /// Determines whether a date is a weekday.
        /// </summary>
        /// <param name="date">The date to check.</param>
        /// <returns><see langword="true"/> for Monday through Friday; otherwise, <see langword="false"/>.</returns>
        static bool IsWorkingDay(DateTime date)
        {
            return date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday;
        }

        /// <summary>
        /// Gets the Monday of the week containing the specified date.
        /// </summary>
        /// <param name="date">The date whose week should be determined.</param>
        /// <returns>The Monday of the corresponding week.</returns>
        static DateTime GetWeekMonday(DateTime date)
        {
            int daysToMonday = ((int)date.DayOfWeek + 6) % 7;
            return date.Date.AddDays(-daysToMonday);
        }

        /// <summary>
        /// Formats a duration in minutes as hours and minutes.
        /// </summary>
        /// <param name="totalMinutes">The duration in minutes.</param>
        /// <returns>A duration formatted as <c>HH:mm</c>.</returns>
        static string FormatTime(int totalMinutes)
        {
            int hours = totalMinutes / 60;
            int minutes = totalMinutes % 60;
            return $"{hours:00}:{minutes:00}";
        }

        /// <summary>
        /// Formats a duration with an explicit sign.
        /// </summary>
        /// <param name="minutes">The duration in minutes.</param>
        /// <returns>A signed duration formatted as <c>+HH:mm</c>, <c>-HH:mm</c>, or <c>00:00</c>.</returns>
        static string FormatSignedTime(int minutes)
        {
            return minutes > 0
                ? $"+{FormatTime(minutes)}"
                : minutes < 0
                    ? $"-{FormatTime(Math.Abs(minutes))}"
                    : FormatTime(0);
        }

        /// <summary>
        /// Writes a centered section header to the console.
        /// </summary>
        /// <param name="title">The title to display.</param>
        static void WriteSectionHeader(string title)
        {
            const int width = 80;

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(new string('=', width));
            Console.WriteLine(title.PadLeft((width + title.Length) / 2));
            Console.WriteLine(new string('=', width));
            Console.ResetColor();
        }

        /// <summary>
        /// Writes a colored time balance to the console.
        /// </summary>
        /// <param name="minutes">The balance in minutes.</param>
        static void WriteBalance(int minutes)
        {
            if (minutes > 0)
            {

                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write($"+{FormatTime(minutes)}");
            }
            else if (minutes < 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($"-{FormatTime(Math.Abs(minutes))}");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write(FormatTime(0));
            }

            Console.ResetColor();
        }

        /// <summary>
        /// Waits for a key press before closing the application.
        /// </summary>
        static void WaitForExit()
        {
            Console.WriteLine();
            Console.WriteLine(new string('=', 80));
            Console.WriteLine("Taste drücken zum Beenden...");
            Console.ReadKey();
        }
        #endregion
    }
}