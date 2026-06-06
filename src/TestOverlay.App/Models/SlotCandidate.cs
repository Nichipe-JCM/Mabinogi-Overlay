using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace TestOverlay.App.Models;

public sealed class SlotCandidate : INotifyPropertyChanged
{
    private bool _isSelected;

    public SlotCandidate(int id, Rect sourceRect, double score)
    {
        Id = id;
        SourceRect = sourceRect;
        Score = score;
        _isSelected = true;
    }

    public int Id { get; }

    public Rect SourceRect { get; set; }

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

    public string Label =>
        $"#{Id:000}  x={SourceRect.X:0}, y={SourceRect.Y:0}, {SourceRect.Width:0}x{SourceRect.Height:0}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
