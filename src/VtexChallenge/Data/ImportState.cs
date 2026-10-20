namespace VtexChallenge.Data;

public enum ImportState
{
    /// <summary>In progress. A run left in this state means the process died before finishing.</summary>
    Running,
    Completed,
    /// <summary>Aborted; all its data was rolled back and only the run row remains.</summary>
    Failed,
}
