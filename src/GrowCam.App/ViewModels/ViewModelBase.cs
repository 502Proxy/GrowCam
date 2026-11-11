using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using GrowCam.App.Services;
using GrowCam.Application;

namespace GrowCam.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
}

/// <summary>Navigation and user feedback, implemented by the main window's view model.</summary>
public interface INavigator
{
    void ShowStart();
    void ShowCamera();
    void ShowDiary();
    void ShowArchive();
    void ShowSettings();
    void ShowWizard();
    void ShowReport(long reportId);
    void ShowArchivedGrow(long growId);
    void Notify(string message, bool isError = false);

    /// <summary>Lets the user choose a photo file. Returns its bytes, or null if cancelled.</summary>
    Task<byte[]?> PickPhotoAsync();

    /// <summary>Called after data changed that is shown in the header.</summary>
    void RefreshHeader();
}

/// <summary>Pages get notified when they become visible / hidden (e.g. to start the live view).</summary>
public interface IPageLifecycle
{
    void OnShown();
    void OnHidden();
}

/// <summary>Base for all pages: busy state and friendly error handling.</summary>
public abstract partial class PageViewModel(AppServices services, INavigator navigator) : ViewModelBase
{
    protected AppServices Services { get; } = services;
    protected INavigator Navigator { get; } = navigator;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = "";

    /// <summary>Reloads the page from the services (also called after background jobs).</summary>
    public virtual void Refresh()
    {
    }

    /// <summary>Runs an action, shows a busy text and turns known errors into a message.</summary>
    protected async Task RunAsync(Func<Task> action, string busyText = "", string successMessage = "")
    {
        if (IsBusy)
            return;
        IsBusy = true;
        BusyText = busyText;
        try
        {
            await action();
            if (successMessage.Length > 0)
                Navigator.Notify(successMessage);
        }
        catch (UserFacingException ex)
        {
            Navigator.Notify(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            FileLog.Error("Unexpected error", ex);
            Navigator.Notify("Da ist etwas schiefgelaufen. Details stehen in der Log-Datei.", isError: true);
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    protected void Run(Action action, string successMessage = "") =>
        _ = RunAsync(() =>
        {
            action();
            return Task.CompletedTask;
        }, successMessage: successMessage);

    protected static Bitmap? ToBitmap(byte[]? jpeg, int? decodeWidth = null)
    {
        if (jpeg is null || jpeg.Length == 0)
            return null;
        using var stream = new MemoryStream(jpeg);
        return decodeWidth is { } width ? Bitmap.DecodeToWidth(stream, width) : new Bitmap(stream);
    }

    protected static string Relative(DateTime value, DateTime now)
    {
        var days = DateOnly.FromDateTime(now).DayNumber - DateOnly.FromDateTime(value).DayNumber;
        return days switch
        {
            0 => $"Heute, {value:HH:mm} Uhr",
            1 => $"Gestern, {value:HH:mm} Uhr",
            _ => $"{Format.Short(DateOnly.FromDateTime(value))}, {value:HH:mm} Uhr",
        };
    }
}

/// <summary>German date formatting used across pages.</summary>
public static class Format
{
    private static readonly string[] Weekdays = ["Mo", "Di", "Mi", "Do", "Fr", "Sa", "So"];

    public static string Date(DateOnly value) => value.ToString("dd.MM.yyyy");

    public static string Short(DateOnly value) => $"{Weekdays[((int)value.DayOfWeek + 6) % 7]}, {value:dd.MM.}";
}
