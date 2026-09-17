using SemanticDesktop.Adapters.Office;
using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;

namespace SemanticDesktop.Adapters.Tests;

public class OfficeAdapterTests
{
    [Fact]
    public async Task Open_Missing_App_Fails_InvalidArgument()
    {
        var adapter = new OfficeAdapter();
        var result = await adapter.ExecuteAsync(new Adapters.AdapterCommand
        {
            Action = CommandNames.OfficeOpen
        }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, result.ErrorCode);
    }

    [Fact]
    public async Task Open_Unknown_App_Fails()
    {
        var adapter = new OfficeAdapter();
        var result = await adapter.ExecuteAsync(new Adapters.AdapterCommand
        {
            Action = CommandNames.OfficeOpen,
            Params = new Dictionary<string, object?> { ["app"] = "Notepad" }
        }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, result.ErrorCode);
    }

    [Fact]
    public async Task MailCompose_Missing_To_Fails()
    {
        var adapter = new OfficeAdapter();
        var result = await adapter.ExecuteAsync(new Adapters.AdapterCommand
        {
            Action = CommandNames.OfficeMailCompose
        }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ErrorCodes.InvalidArgument, result.ErrorCode);
    }

    [Fact]
    public void BuildMailto_Encodes_Subject_And_Body()
    {
        var mailto = OfficeAdapter.BuildMailto("a@b.com", "Hello & Hi", "Line 1\nLine 2");
        Assert.StartsWith("mailto:a@b.com?", mailto);
        Assert.Contains("subject=", mailto);
        Assert.Contains("body=", mailto);
    }

    [Fact]
    public void StartOfWeek_Is_Monday()
    {
        var wednesday = new DateTime(2026, 9, 16);
        var start = OfficeAdapter.StartOfWeek(wednesday);
        Assert.Equal(DayOfWeek.Monday, start.DayOfWeek);
        Assert.Equal(new DateTime(2026, 9, 14), start);
    }

    [Fact]
    public async Task CalendarWeek_Without_Outlook_Fails_Soft()
    {
        var adapter = new OfficeAdapter();
        var result = await adapter.ExecuteAsync(new Adapters.AdapterCommand
        {
            Action = CommandNames.OfficeCalendarWeek
        }, CancellationToken.None);

        // Either succeeds with appointments JSON when Outlook is installed,
        // or returns AdapterUnavailable soft-fail.
        if (result.Ok)
        {
            Assert.NotNull(result.Data);
            return;
        }

        Assert.Equal(ErrorCodes.AdapterUnavailable, result.ErrorCode);
        Assert.Contains("Outlook", result.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Capabilities_Include_Office_Actions()
    {
        var adapter = new OfficeAdapter();
        var caps = await adapter.GetCapabilitiesAsync(CancellationToken.None);
        Assert.Equal("office", caps.AdapterId);
        Assert.Contains(CommandNames.OfficeOpen, caps.Actions);
        Assert.Contains(CommandNames.OfficeMailCompose, caps.Actions);
        Assert.Contains(CommandNames.OfficeCalendarWeek, caps.Actions);
    }
}
