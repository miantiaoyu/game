using Xunit;

// 指标用的是进程级 Meter 与 MeterListener。xUnit 默认让不同测试类并行运行，
// 那样一个测试的监听器会收到其他测试房间的测量值，断言变得不确定，
// 而且回调会从多个线程并发写入集合。这里关闭并行，换取确定性。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
