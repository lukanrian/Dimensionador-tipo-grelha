namespace Grelha.Shared;

public static class OutputPaths
{
    public static string DirectoryFor(string input) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, "Resultados");

    /// <summary>Caminho do resultado, sem criar a pasta (para ler ou conferir se existe).</summary>
    public static string PathFor(string input, string suffix) => Path.Combine(DirectoryFor(input), Path.GetFileNameWithoutExtension(input) + suffix);

    /// <summary>Caminho do resultado, criando a pasta Resultados (para gravar).</summary>
    public static string FileFor(string input, string suffix)
    {
        var directory = DirectoryFor(input);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(input) + suffix);
    }
}
