using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Models;

namespace SemanticDesktop.Adapters.Office;

public sealed class OfficeAdapter : IApplicationAdapter
{
    public string Id => "office";

    public bool CanHandle(ProcessInfo process)
    {
        var name = process.Name ?? "";
        return name.Contains("WINWORD", StringComparison.OrdinalIgnoreCase)
               || name.Contains("EXCEL", StringComparison.OrdinalIgnoreCase)
               || name.Contains("POWERPNT", StringComparison.OrdinalIgnoreCase)
               || name.Contains("OUTLOOK", StringComparison.OrdinalIgnoreCase);
    }

    public Task<ApplicationCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var outlook = OfficeCom.IsProgIdAvailable("Outlook.Application");
        var word = OfficeCom.IsProgIdAvailable("Word.Application");
        return Task.FromResult(new ApplicationCapabilities
        {
            AdapterId = Id,
            Available = outlook || word || OfficeCom.IsProgIdAvailable("Excel.Application")
                        || OfficeCom.IsProgIdAvailable("PowerPoint.Application"),
            Actions = new[]
            {
                CommandNames.OfficeOpen,
                CommandNames.OfficeMailCompose,
                CommandNames.OfficeCalendarWeek
            },
            Meta = new Dictionary<string, object?>
            {
                ["outlook"] = outlook,
                ["word"] = word,
                ["provider"] = "office-com"
            }
        });
    }

    public async Task<AdapterResult> ExecuteAsync(AdapterCommand command, CancellationToken cancellationToken)
    {
        return command.Action.ToLowerInvariant() switch
        {
            CommandNames.OfficeOpen => await Task.FromResult(Open(command)).ConfigureAwait(false),
            CommandNames.OfficeMailCompose => await Task.FromResult(MailCompose(command)).ConfigureAwait(false),
            CommandNames.OfficeCalendarWeek => await Task.FromResult(CalendarWeek(command)).ConfigureAwait(false),
            _ => AdapterResult.Fail(ErrorCodes.Unsupported, $"Unknown office action '{command.Action}'.")
        };
    }

    private static AdapterResult Open(AdapterCommand command)
    {
        var app = GetString(command.Params, "app");
        if (string.IsNullOrWhiteSpace(app))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "app is required (Word|Excel|PowerPoint|Outlook).");
        }

        var path = GetString(command.Params, "path");
        var progId = app.Trim().ToLowerInvariant() switch
        {
            "word" => "Word.Application",
            "excel" => "Excel.Application",
            "powerpoint" or "ppt" => "PowerPoint.Application",
            "outlook" => "Outlook.Application",
            _ => null
        };

        if (progId is null)
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "app must be Word|Excel|PowerPoint|Outlook.");
        }

        try
        {
            var type = Type.GetTypeFromProgID(progId, throwOnError: false);
            if (type is null)
            {
                return AdapterResult.Fail(ErrorCodes.AdapterUnavailable, $"{app} COM ProgID '{progId}' not registered.");
            }

            dynamic com = Activator.CreateInstance(type)
                          ?? throw new InvalidOperationException("Failed to create Office COM instance.");
            try
            {
                try { com.Visible = true; } catch { /* Outlook has no Visible the same way */ }

                if (!string.IsNullOrWhiteSpace(path))
                {
                    var full = Path.GetFullPath(path);
                    if (!File.Exists(full))
                    {
                        return AdapterResult.Fail(ErrorCodes.NotFound, $"File not found: {full}");
                    }

                    OpenDocument(com, progId, full);
                }
                else if (string.Equals(progId, "Outlook.Application", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        dynamic ns = com.GetNamespace("MAPI");
                        ns.Logon();
                    }
                    catch
                    {
                        // best-effort
                    }
                }

                return AdapterResult.Success(new
                {
                    opened = true,
                    app = NormalizeApp(app),
                    path,
                    provider = "office-com"
                });
            }
            finally
            {
                // Leave Office apps running; do not Quit.
                try { Marshal.FinalReleaseComObject(com); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            return AdapterResult.Fail(ErrorCodes.AdapterFailed, ex.Message);
        }
    }

    private static void OpenDocument(dynamic app, string progId, string path)
    {
        if (progId.StartsWith("Word", StringComparison.OrdinalIgnoreCase))
        {
            app.Documents.Open(path);
            return;
        }

        if (progId.StartsWith("Excel", StringComparison.OrdinalIgnoreCase))
        {
            app.Workbooks.Open(path);
            return;
        }

        if (progId.StartsWith("PowerPoint", StringComparison.OrdinalIgnoreCase))
        {
            app.Presentations.Open(path);
        }
    }

    private static AdapterResult MailCompose(AdapterCommand command)
    {
        var to = GetString(command.Params, "to");
        if (string.IsNullOrWhiteSpace(to))
        {
            return AdapterResult.Fail(ErrorCodes.InvalidArgument, "to is required.");
        }

        var subject = GetString(command.Params, "subject") ?? "";
        var body = GetString(command.Params, "body") ?? "";

        try
        {
            var type = Type.GetTypeFromProgID("Outlook.Application", throwOnError: false);
            if (type is not null)
            {
                dynamic? outlook = Activator.CreateInstance(type);
                if (outlook is not null)
                {
                    try
                    {
                        // olMailItem = 0
                        dynamic mail = outlook.CreateItem(0);
                        mail.To = to;
                        mail.Subject = subject;
                        mail.Body = body;
                        mail.Display(false);
                        return AdapterResult.Success(new
                        {
                            composed = true,
                            to,
                            subject,
                            provider = "outlook-com"
                        });
                    }
                    finally
                    {
                        try { Marshal.FinalReleaseComObject(outlook); } catch { /* ignore */ }
                    }
                }
            }
        }
        catch
        {
            // fall through to mailto
        }

        try
        {
            var mailto = BuildMailto(to, subject, body);
            Process.Start(new ProcessStartInfo
            {
                FileName = mailto,
                UseShellExecute = true
            });
            return AdapterResult.Success(new
            {
                composed = true,
                to,
                subject,
                provider = "mailto"
            });
        }
        catch (Exception ex)
        {
            return AdapterResult.Fail(
                ErrorCodes.AdapterUnavailable,
                "Outlook COM and mailto fallback both failed: " + ex.Message);
        }
    }

    private static AdapterResult CalendarWeek(AdapterCommand command)
    {
        _ = command;
        try
        {
            var type = Type.GetTypeFromProgID("Outlook.Application", throwOnError: false);
            if (type is null)
            {
                return AdapterResult.Fail(
                    ErrorCodes.AdapterUnavailable,
                    "Outlook is not installed (Outlook.Application ProgID missing).");
            }

            dynamic? outlook = Activator.CreateInstance(type);
            if (outlook is null)
            {
                return AdapterResult.Fail(ErrorCodes.AdapterUnavailable, "Failed to create Outlook.Application.");
            }

            try
            {
                dynamic ns = outlook.GetNamespace("MAPI");
                try { ns.Logon(); } catch { /* already logged on */ }

                // olFolderCalendar = 9
                dynamic calendar = ns.GetDefaultFolder(9);
                dynamic items = calendar.Items;
                items.Sort("[Start]");
                try { items.IncludeRecurrences = true; } catch { /* older Outlook */ }

                var now = DateTime.Now;
                var start = StartOfWeek(now);
                var end = start.AddDays(7);
                var filter =
                    $"[Start] >= '{start.ToString("g", CultureInfo.InvariantCulture)}' AND [Start] < '{end.ToString("g", CultureInfo.InvariantCulture)}'";

                dynamic? restricted = null;
                try
                {
                    restricted = items.Restrict(filter);
                }
                catch
                {
                    restricted = items;
                }

                var appointments = new List<object>();
                var count = (int)restricted.Count;
                for (var i = 1; i <= count; i++)
                {
                    try
                    {
                        dynamic item = restricted[i];
                        DateTime itemStart = (DateTime)item.Start;
                        DateTime itemEnd = (DateTime)item.End;
                        if (itemStart < start || itemStart >= end)
                        {
                            continue;
                        }

                        string subject = "";
                        try { subject = (string?)item.Subject ?? ""; } catch { /* ignore */ }
                        string location = "";
                        try { location = (string?)item.Location ?? ""; } catch { /* ignore */ }

                        appointments.Add(new
                        {
                            subject,
                            start = itemStart.ToString("o"),
                            end = itemEnd.ToString("o"),
                            location
                        });
                    }
                    catch
                    {
                        // skip item
                    }
                }

                return AdapterResult.Success(new
                {
                    weekStart = start.ToString("o"),
                    weekEnd = end.ToString("o"),
                    appointments,
                    count = appointments.Count,
                    provider = "outlook-com"
                });
            }
            finally
            {
                try { Marshal.FinalReleaseComObject(outlook); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            return AdapterResult.Fail(
                ErrorCodes.AdapterUnavailable,
                "Outlook calendar unavailable: " + ex.Message);
        }
    }

    internal static string BuildMailto(string to, string subject, string body)
    {
        var sb = new StringBuilder("mailto:");
        sb.Append(to.Trim());
        var q = new List<string>();
        if (!string.IsNullOrEmpty(subject))
        {
            q.Add("subject=" + Uri.EscapeDataString(subject));
        }

        if (!string.IsNullOrEmpty(body))
        {
            q.Add("body=" + Uri.EscapeDataString(body));
        }

        if (q.Count > 0)
        {
            sb.Append('?').Append(string.Join("&", q));
        }

        return sb.ToString();
    }

    internal static DateTime StartOfWeek(DateTime dt)
    {
        var diff = (7 + (dt.DayOfWeek - DayOfWeek.Monday)) % 7;
        return dt.Date.AddDays(-diff);
    }

    private static string NormalizeApp(string app) =>
        app.Trim().ToLowerInvariant() switch
        {
            "ppt" => "PowerPoint",
            "word" => "Word",
            "excel" => "Excel",
            "powerpoint" => "PowerPoint",
            "outlook" => "Outlook",
            _ => app.Trim()
        };

    internal static string? GetString(Dictionary<string, object?>? parameters, string key)
    {
        if (parameters is null || !parameters.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => value.ToString()
        };
    }
}

internal static class OfficeCom
{
    public static bool IsProgIdAvailable(string progId)
    {
        try
        {
            return Type.GetTypeFromProgID(progId, throwOnError: false) is not null;
        }
        catch
        {
            return false;
        }
    }
}
