namespace FxDbg.Symbols.Windows;

public sealed class LocalVariableSlot
{
    public LocalVariableSlot(string name, int index) { Name = name; Index = index; }
    public string Name { get; }
    public int Index { get; }
}
