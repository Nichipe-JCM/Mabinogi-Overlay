namespace TestOverlay.App.Services;

public sealed class ProfileSession
{
    private readonly ProfileStore _store;

    public ProfileSession(ProfileStore store)
    {
        _store = store;
    }

    public IReadOnlyList<string> ProfileNames { get; private set; } = ["default"];

    public string SelectedProfileName { get; set; } = "default";

    public bool IsLoading { get; set; }

    public bool IsDirty { get; set; }

    public bool TryFlush(Func<bool> save)
    {
        ArgumentNullException.ThrowIfNull(save);
        if (!IsDirty)
        {
            return true;
        }

        var saved = save();
        IsDirty = !saved;
        return saved;
    }

    public void RefreshProfileNames(string? preferredName = null)
    {
        var names = _store.ListProfileNames().ToList();
        if (names.Count == 0)
        {
            names.Add("default");
        }

        var preferred = string.IsNullOrWhiteSpace(preferredName)
            ? SelectedProfileName
            : preferredName.Trim();
        ProfileNames = names;
        SelectedProfileName = names.FirstOrDefault(name =>
                                  string.Equals(name, preferred, StringComparison.OrdinalIgnoreCase))
                              ?? names[0];
    }
}
