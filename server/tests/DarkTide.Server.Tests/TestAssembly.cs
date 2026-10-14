// 每个测试都会起一个宿主；并行时端口、日志与计时互相干扰，关闭并行换取确定性。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
