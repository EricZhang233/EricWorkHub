using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EricWorkHub;

public abstract class IconViewModel : INotifyPropertyChanged
{
    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set { if (ReferenceEquals(_icon, value)) return; _icon = value; Changed(); Changed(nameof(PlaceholderVisibility)); } }
    public Visibility PlaceholderVisibility => _icon == null ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string property = "") => PropertyChanged?.Invoke(this, new(property));
}

public sealed class RecentItemViewModel : IconViewModel
{
    internal RecentItem Model { get; private set; }
    internal PreviewRequestState Preview { get; } = new();
    internal RecentItemViewModel(RecentItem item) { Model = item; Preview.Update(DocumentPreview.Key(item)); }
    public string Title => Model.Title;
    public string DisplayLocation => Model.DisplayLocation;
    public string TimestampText => Model.TimestampText;
    public string StatusText => Model.StatusText;
    internal void Update(RecentItem model)
    {
        if (Model == model) return;
        var previous = Model;
        Model = model;
        Preview.Update(DocumentPreview.Key(model));
        if (previous.Title != model.Title) Changed(nameof(Title));
        if (previous.DisplayLocation != model.DisplayLocation) Changed(nameof(DisplayLocation));
        if (previous.TimestampText != model.TimestampText) Changed(nameof(TimestampText));
        if (previous.StatusText != model.StatusText) Changed(nameof(StatusText));
    }
}

public sealed class OfficeAppItem : IconViewModel
{
    internal int IconRequest { get; set; }
    internal DetectedApp Model { get; private set; }
    internal OfficeAppItem(DetectedApp app) => Model = app;
    public string DisplayName => Model.Definition.Name;
    public bool IsInstalled => Model.IsInstalled;
    public string Description => Model.Definition.Description;
    public string ToolTipText => Model.Definition.ToolTipText;
    internal void Update(DetectedApp model)
    {
        bool installed = IsInstalled;
        string name = DisplayName;
        string description = Description;
        string tooltip = ToolTipText;
        Model = model;
        if (name != DisplayName) Changed(nameof(DisplayName));
        if (description != Description) Changed(nameof(Description));
        if (tooltip != ToolTipText) Changed(nameof(ToolTipText));
        if (installed != IsInstalled) Changed(nameof(IsInstalled));
    }
}
