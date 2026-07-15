namespace gsm.Services;

/// <summary>
/// Cấu hình tập trung cho khả năng xử lý nhiều modem. Một COM chỉ thực hiện một
/// chuỗi AT tại một thời điểm; các COM khác nhau được phép chạy độc lập.
/// </summary>
public static class BackendConcurrency
{
    public const int BaselineConcurrentPorts = 64;

    public static void ConfigureThreadPool(int expectedPorts = BaselineConcurrentPorts)
    {
        expectedPorts = Math.Max(BaselineConcurrentPorts, expectedPorts);
        ThreadPool.GetMinThreads(out int workers, out int io);
        ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
        // SerialPort phát DataReceived qua ThreadPool. Giữ sẵn đủ worker theo số
        // modem thực tế để không phải chờ ThreadPool tăng luồng từ từ khi cùng trả dữ liệu.
        ThreadPool.SetMinThreads(
            Math.Min(maxWorkers, Math.Max(workers, (int)Math.Min(int.MaxValue, (long)expectedPorts * 2))),
            Math.Min(maxIo, Math.Max(io, expectedPorts)));
    }

    public static Task ForEachPortAsync<T>(
        IEnumerable<T> ports,
        Func<T, CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken = default)
    {
        T[] snapshot = ports.ToArray();
        ConfigureThreadPool(snapshot.Length);
        return Task.WhenAll(snapshot.Select(port => RunOneAsync(port)));

        async Task RunOneAsync(T port)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operation(port, cancellationToken);
        }
    }
}
