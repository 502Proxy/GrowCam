using Avalonia.Controls;
using Avalonia.Controls.Templates;
using GrowCam.App.ViewModels;

namespace GrowCam.App;

/// <summary>Finds the view for a page view model by naming convention (FooViewModel → FooView).</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? data)
    {
        if (data is null)
            return null;
        var name = data.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);
        return type is null
            ? new TextBlock { Text = "View not found: " + name }
            : (Control)Activator.CreateInstance(type)!;
    }

    public bool Match(object? data) => data is PageViewModel;
}
