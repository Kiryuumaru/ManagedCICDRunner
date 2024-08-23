namespace Application.Common;

public static partial class AbsolutePathExtensions
{
    internal class FileMap
    {
        public required AbsolutePath Source { get; init; }

        public required AbsolutePath[] Files { get; init; }

        public required AbsolutePath[] Folders { get; init; }

        public required (AbsolutePath Link, AbsolutePath Target)[] SymbolicLinks { get; init; }
    }
}
