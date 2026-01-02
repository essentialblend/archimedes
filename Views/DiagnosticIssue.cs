// Views/DiagnosticIssue.cs

namespace archimedes.Views
{
  // Lightweight diagnostic record for file-local checks.
  internal sealed class DiagnosticIssue
  {
    public DiagnosticIssue(int offset, int length, string message)
    {
      Offset = offset;
      Length = length;
      Message = message;
    }

    public int Offset { get; }
    public int Length { get; }
    public string Message { get; }
  }
}
