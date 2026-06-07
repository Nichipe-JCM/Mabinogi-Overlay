using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace TestOverlay.App.Models;

public sealed class SlotCandidate : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _sectionMembership = string.Empty;

    public SlotCandidate(int id, Rect sourceRect, double score)
    {
        Id = id;
        SourceRect = sourceRect;
        Score = score;
        _isSelected = false;
    }

    public int Id { get; }

    public Rect SourceRect { get; private set; }

    public double Score { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string SectionMembership
    {
        get => _sectionMembership;
        set
        {
            if (_sectionMembership == value)
            {
                return;
            }

            _sectionMembership = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Label));
        }
    }

    public string Label =>
        string.IsNullOrWhiteSpace(SectionMembership)
            ? $"#{Id:000}  x={SourceRect.X:0}, y={SourceRect.Y:0}, {SourceRect.Width:0}x{SourceRect.Height:0}"
            : $"#{Id:000}  {SectionMembership}  x={SourceRect.X:0}, y={SourceRect.Y:0}, {SourceRect.Width:0}x{SourceRect.Height:0}";

    public void MoveTo(double x, double y)
    {
        SourceRect = new Rect(x, y, SourceRect.Width, SourceRect.Height);
        OnPropertyChanged(nameof(SourceRect));
        OnPropertyChanged(nameof(Label));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
