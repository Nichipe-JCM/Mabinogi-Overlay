namespace TestOverlay.App.Services;

public sealed class LayoutEditHistory<T>
{
    private readonly Stack<T> _undo = new();
    private readonly Stack<T> _redo = new();

    public int UndoCount => _undo.Count;

    public int RedoCount => _redo.Count;

    public void Record(T before, T after, Func<T, T, bool> equals)
    {
        if (equals(before, after))
        {
            return;
        }

        _undo.Push(before);
        _redo.Clear();
    }

    public bool TryUndo(T current, out T previous)
    {
        if (!_undo.TryPop(out previous!))
        {
            return false;
        }

        _redo.Push(current);
        return true;
    }

    public bool TryRedo(T current, out T next)
    {
        if (!_redo.TryPop(out next!))
        {
            return false;
        }

        _undo.Push(current);
        return true;
    }
}
