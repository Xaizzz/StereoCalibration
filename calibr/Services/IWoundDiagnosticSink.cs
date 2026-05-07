namespace StereoCalibration.Services
{
    /// <summary>
    /// Получает события деформации/согласования для живого журнала (JSONL по сеансу процесса).
    /// Реализация не должна бросать исключения наверх.
    /// </summary>
    public interface IWoundDiagnosticSink
    {
        void Append(string eventType, object? payload);
    }
}
